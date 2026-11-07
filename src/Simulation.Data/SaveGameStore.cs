using System.Globalization;
using System.Text.Json;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Random;
using GlassWorld.Simulation.State;
using Microsoft.Data.Sqlite;

namespace GlassWorld.Simulation.Data;

/// <summary>
/// One SQLite file per world (ADR-0003). Saving is crash-safe: the state is written into a fresh copy
/// next to the target and then swapped in. Every file carries a schema version; older files are migrated
/// on load, newer ones are refused. Doubles are stored as SQLite REAL (IEEE 754), so a reload is exact.
/// </summary>
public static class SaveGameStore
{
    /// <summary>Schema version written by this code.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    /// <summary>Migrations: index i lifts a file from version i to i + 1.</summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE streams(name TEXT PRIMARY KEY, state TEXT NOT NULL, increment TEXT NOT NULL);
        CREATE TABLE objects(id TEXT PRIMARY KEY, type TEXT NOT NULL, x REAL NOT NULL, z REAL NOT NULL);
        CREATE TABLE inhabitants(id TEXT PRIMARY KEY, sex TEXT NOT NULL, grp INTEGER NOT NULL, x REAL NOT NULL, z REAL NOT NULL,
          will_to_live REAL NOT NULL, neglect_ticks INTEGER NOT NULL, needs TEXT NOT NULL, moods TEXT NOT NULL, traits TEXT NOT NULL, activity TEXT, planned TEXT);
        CREATE TABLE beliefs(inhabitant TEXT NOT NULL, type TEXT NOT NULL, property TEXT NOT NULL, value INTEGER NOT NULL, confidence REAL NOT NULL,
          PRIMARY KEY(inhabitant, type, property));
        CREATE TABLE questions(id TEXT PRIMARY KEY, data TEXT NOT NULL);
        CREATE TABLE last_asked(key TEXT PRIMARY KEY, tick INTEGER NOT NULL);
        CREATE TABLE decision_log(seq INTEGER PRIMARY KEY, kind TEXT NOT NULL, request_id TEXT NOT NULL, tick INTEGER NOT NULL, json TEXT NOT NULL, note TEXT);
        """,
    ];

    /// <summary>Saves a state (write into a copy, then swap).</summary>
    public static void Save(string path, SaveState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(state);
        var temp = path + ".tmp";
        File.Delete(temp);
        using (var connection = Open(temp))
        {
            Migrate(connection);
            using var tx = connection.BeginTransaction();
            Write(connection, tx, state);
            tx.Commit();
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Loads a state. The file itself is never changed by loading: it is first checked read-only (an
    /// empty or foreign file, or a newer version, is refused with <see cref="InvalidDataException"/>);
    /// an older version is migrated in a temporary copy, and the migrated form reaches the disk only
    /// with the next save (review GW3 #50 K2).
    /// </summary>
    public static SaveState Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Save game not found.", path);
        }

        int version;
        try
        {
            using var probe = Open(path, readOnly: true);
            version = UserVersion(probe);
            if (version >= 1 && !HasTable(probe, "meta"))
            {
                version = 0;
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a GlassWorld save game ({ex.SqliteErrorCode}).", ex);
        }

        if (version == 0)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a GlassWorld save game (no schema version).");
        }

        if (version > SchemaVersion)
        {
            throw new InvalidDataException($"Save game has schema version {version}; this build supports up to {SchemaVersion}.");
        }

        if (version == SchemaVersion)
        {
            using var connection = Open(path, readOnly: true);
            return Read(connection);
        }

        var copy = path + ".load.tmp";
        File.Copy(path, copy, overwrite: true);
        try
        {
            using (var connection = Open(copy))
            {
                Migrate(connection);
                return Read(connection);
            }
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <summary>Schema version of a file (0 = empty).</summary>
    public static int VersionOf(string path)
    {
        using var connection = Open(path, readOnly: true);
        return UserVersion(connection);
    }

    private static SqliteConnection Open(string path, bool readOnly = false)
    {
        // Pooling off: the pool would keep the file open and the swap could fail on Windows (spike D337).
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static bool HasTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static int UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Migrate(SqliteConnection connection)
    {
        var version = UserVersion(connection);
        if (version > SchemaVersion)
        {
            throw new InvalidDataException($"Save game has schema version {version}; this build supports up to {SchemaVersion}.");
        }

        for (; version < SchemaVersion; version++)
        {
            using var tx = connection.BeginTransaction();
            Execute(connection, tx, Migrations[version]);
            Execute(connection, tx, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {version + 1};"));
            tx.Commit();
        }
    }

    private static void Write(SqliteConnection c, SqliteTransaction tx, SaveState s)
    {
        Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("seed", Str(s.Seed)), ("tick", Str(s.Tick)), ("next_object", Str(s.NextObjectNumber)), ("question_counter", Str(s.QuestionCounter)));
        if (s.WorldPreset is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("world_preset", s.WorldPreset));
        }

        foreach (var (name, state) in s.Streams)
        {
            Insert(c, tx, "INSERT INTO streams VALUES($a,$b,$c)", (name, Str(state.State), Str(state.Increment)));
        }

        foreach (var o in s.Objects)
        {
            Insert(c, tx, "INSERT INTO objects VALUES($a,$b,$c,$d)", (o.Id, o.TypeId, o.Position.X, o.Position.Z));
        }

        foreach (var i in s.Inhabitants)
        {
            Insert(c, tx, "INSERT INTO inhabitants VALUES($a,$b,$c,$d,$e,$f,$g,$h,$i,$j,$k,$l)",
                (i.Id, i.Sex, i.Group, i.Position.X, i.Position.Z, i.WillToLive, i.NeglectTicks,
                 JsonSerializer.Serialize(i.Needs), JsonSerializer.Serialize(i.Moods), JsonSerializer.Serialize(i.Traits),
                 i.Activity is null ? null : JsonSerializer.Serialize(i.Activity, Json),
                 i.Planned is null ? null : JsonSerializer.Serialize(i.Planned, Json)));
            foreach (var b in i.Beliefs)
            {
                Insert(c, tx, "INSERT INTO beliefs VALUES($a,$b,$c,$d,$e)", (i.Id, b.TypeId, b.PropertyId, b.Value ? 1 : 0, b.Confidence));
            }
        }

        foreach (var q in s.Questions)
        {
            Insert(c, tx, "INSERT INTO questions VALUES($a,$b)", (q.Id, JsonSerializer.Serialize(q)));
        }

        foreach (var (key, tick) in s.LastAsked)
        {
            Insert(c, tx, "INSERT INTO last_asked VALUES($a,$b)", (key, tick));
        }

        var seq = 0;
        foreach (var e in s.DecisionLog)
        {
            Insert(c, tx, "INSERT INTO decision_log VALUES($a,$b,$c,$d,$e,$f)", (seq++, e.Kind.ToString(), e.RequestId, e.Tick, e.Json, e.Note));
        }
    }

    private static SaveState Read(SqliteConnection c)
    {
        var meta = Query(c, "SELECT key, value FROM meta", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        var beliefs = Query(c, "SELECT inhabitant, type, property, value, confidence FROM beliefs ORDER BY inhabitant, type, property",
                r => (Inhabitant: r.GetString(0), Belief: new SavedBelief(r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetDouble(4))))
            .ToLookup(x => x.Inhabitant, x => x.Belief, StringComparer.Ordinal);
        return new SaveState(
            ulong.Parse(meta["seed"], CultureInfo.InvariantCulture),
            long.Parse(meta["tick"], CultureInfo.InvariantCulture),
            Query(c, "SELECT name, state, increment FROM streams ORDER BY name",
                r => new KeyValuePair<string, PcgState>(r.GetString(0), new PcgState(ulong.Parse(r.GetString(1), CultureInfo.InvariantCulture), ulong.Parse(r.GetString(2), CultureInfo.InvariantCulture)))),
            long.Parse(meta["next_object"], CultureInfo.InvariantCulture),
            Query(c, "SELECT id, type, x, z FROM objects ORDER BY id", r => new WorldObject(r.GetString(0), r.GetString(1), new Position(r.GetDouble(2), r.GetDouble(3)))),
            Query(c, "SELECT id, sex, grp, x, z, will_to_live, neglect_ticks, needs, moods, traits, activity, planned FROM inhabitants ORDER BY id",
                r => new SavedInhabitant(
                    r.GetString(0),
                    r.GetString(1),
                    r.GetInt32(2),
                    new Position(r.GetDouble(3), r.GetDouble(4)),
                    JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(7))!,
                    JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(8))!,
                    JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(9))!,
                    r.GetDouble(5),
                    r.GetInt64(6),
                    r.IsDBNull(10) ? null : JsonSerializer.Deserialize<Activity>(r.GetString(10), Json),
                    r.IsDBNull(11) ? null : JsonSerializer.Deserialize<Activity>(r.GetString(11), Json),
                    [.. beliefs[r.GetString(0)]])),
            long.Parse(meta["question_counter"], CultureInfo.InvariantCulture),
            Query(c, "SELECT data FROM questions ORDER BY id", r => JsonSerializer.Deserialize<SavedQuestion>(r.GetString(0))!),
            Query(c, "SELECT key, tick FROM last_asked ORDER BY key", r => new KeyValuePair<string, long>(r.GetString(0), r.GetInt64(1))),
            Query(c, "SELECT kind, request_id, tick, json, note FROM decision_log ORDER BY seq",
                r => new DecisionLogEntry(Enum.Parse<DecisionLogKind>(r.GetString(0)), r.GetString(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4))),
            meta.GetValueOrDefault("world_preset"));
    }

    private static string Str(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Str(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection c, SqliteTransaction tx, string sql, params System.Runtime.CompilerServices.ITuple[] rows)
    {
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var row in rows)
        {
            command.Parameters.Clear();
            for (var i = 0; i < row.Length; i++)
            {
                command.Parameters.AddWithValue("$" + (char)('a' + i), row[i] ?? DBNull.Value);
            }

            command.ExecuteNonQuery();
        }
    }

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            result.Add(map(reader));
        }

        return result;
    }
}

/// <summary>Saves the world after every game day (Leitbild: at most one game day lost after a crash).</summary>
public sealed class AutoSaver
{
    /// <summary>Hooks the autosave onto the day boundary of a simulation.</summary>
    public AutoSaver(SimulationHost host, string path)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(path);
        host.World.BoundaryReached += (_, boundary) =>
        {
            if (boundary == TimeBoundary.Day)
            {
                SaveGameStore.Save(path, StateCodec.Capture(host));
                SavedTicks.Add(host.World.Tick);
            }
        };
    }

    /// <summary>Ticks at which an autosave happened (tests, logs).</summary>
    public List<long> SavedTicks { get; } = [];
}
