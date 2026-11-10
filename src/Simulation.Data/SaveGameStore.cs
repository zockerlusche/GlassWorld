using System.Globalization;
using System.Text.Json;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
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
    /// <summary>Meta key of the content fingerprint (<see cref="ContentFingerprint"/>), written by headless and game.</summary>
    public const string ContentFingerprintKey = "content_fingerprint";

    /// <summary>Schema version written by this code.</summary>
    public const int SchemaVersion = 8;

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
        """
        CREATE TABLE inventions(type_id TEXT PRIMARY KEY, request_id TEXT NOT NULL, inhabitant TEXT NOT NULL, tick INTEGER NOT NULL, invention TEXT NOT NULL, sound TEXT);
        CREATE TABLE wants(inhabitant TEXT PRIMARY KEY, data TEXT NOT NULL);
        CREATE TABLE invention_timers(kind TEXT NOT NULL, key TEXT NOT NULL, tick INTEGER NOT NULL, PRIMARY KEY(kind, key));
        CREATE TABLE job_log(seq INTEGER PRIMARY KEY, kind TEXT NOT NULL, request_id TEXT NOT NULL, tick INTEGER NOT NULL, json TEXT NOT NULL, note TEXT);
        CREATE TABLE chronicle(seq INTEGER PRIMARY KEY, tick INTEGER NOT NULL, kind TEXT NOT NULL, inhabitant TEXT NOT NULL, target TEXT, action TEXT);
        """,
        """
        CREATE TABLE island(id INTEGER PRIMARY KEY CHECK (id = 1), data BLOB NOT NULL);
        """,
        """
        ALTER TABLE beliefs ADD COLUMN source TEXT NOT NULL DEFAULT 'experienced';
        ALTER TABLE beliefs ADD COLUMN told_by TEXT;
        ALTER TABLE beliefs ADD COLUMN tick INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE relationships(inhabitant TEXT NOT NULL, other TEXT NOT NULL, affection REAL NOT NULL, trust REAL NOT NULL, respect REAL NOT NULL,
          PRIMARY KEY(inhabitant, other));
        """,

        // 5 (F4 hair kit and outfits): hair styles and outfits of the first look that were renamed or
        // dropped get their current ids in the saved appearance records (the frozen copy of
        // renamed_hair_styles / renamed_outfits in content/look/appearance.json at this version).
        """
        UPDATE meta SET value = (
          SELECT json_group_object(e.key, json(json_set(e.value,
            '$.HairStyle', CASE json_extract(e.value, '$.HairStyle')
              WHEN 'curly' THEN 'curls' WHEN 'braided' THEN 'braid' WHEN 'tied' THEN 'bun'
              ELSE json_extract(e.value, '$.HairStyle') END,
            '$.ClothingStyle', CASE json_extract(e.value, '$.ClothingStyle')
              WHEN 'wrap' THEN 'tunic'
              ELSE json_extract(e.value, '$.ClothingStyle') END)))
          FROM json_each(meta.value) AS e)
        WHERE key = 'appearances';
        """,

        // 6 (hydrology, #134): the island table notes the blob format (IslandCodec: 2 adds the hydrology and
        // underground layers; rows from before are format 1 and still decode).
        """
        ALTER TABLE island ADD COLUMN format INTEGER NOT NULL DEFAULT 1;
        """,

        // 7 (cloud cover, B2-6 R14): the seen cells, one row per owner ('' = the player's view, else an
        // inhabitant id), one bit per cell of the grid (CellSet). Files from before have no rows: such a
        // world starts with what its inhabitants see when it is loaded.
        """
        CREATE TABLE exploration(owner TEXT PRIMARY KEY, width INTEGER NOT NULL, depth INTEGER NOT NULL, cells BLOB NOT NULL);
        """,

        // 8 (sight in the weather, B2594): the last look of each inhabitant (the cell it looked from and its
        // sight range then, in cells) on its row of seen cells. Rows from before and the player's view have
        // none: such inhabitants look again when the world goes on.
        """
        ALTER TABLE exploration ADD COLUMN looked_cell INTEGER;
        ALTER TABLE exploration ADD COLUMN looked_range INTEGER;
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
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("island_hash", s.IslandHash ?? string.Empty));
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("occurrences", JsonSerializer.Serialize(s.Occurrences ?? [])));
            if (s.IslandData is not null)
            {
                // The generated island as one blob (B2 R11; GW2 review #94: no base64 in meta).
                // The blob format (schema 6): 2 adds the hydrology and underground layers; older rows are
                // format 1 and still decode, as islands without them (IslandCodec).
                Insert(c, tx, "INSERT INTO island(id, data, format) VALUES($a,$b,$c)", (1, s.IslandData, Map.IslandCodec.FormatOf(s.IslandData)));
            }
        }

        // Appearance records (B2 R27) as one JSON object by inhabitant id; older saves have none.
        var appearances = s.Inhabitants.Where(i => i.Appearance is not null).ToDictionary(i => i.Id, i => i.Appearance!, StringComparer.Ordinal);
        if (appearances.Count > 0)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("appearances", JsonSerializer.Serialize(appearances, Json)));
        }

        // Searches under way (give-up clock) as one JSON object by inhabitant id; older saves have none.
        var searches = s.Inhabitants.Where(i => i.SearchSince is { Count: > 0 }).ToDictionary(i => i.Id, i => i.SearchSince!, StringComparer.Ordinal);
        if (searches.Count > 0)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("search_since", JsonSerializer.Serialize(searches, Json)));
        }

        // Repeats without effect and resting actions (W1696) as one JSON object by inhabitant id; older saves have none.
        var repeats = s.Inhabitants.Where(i => i.Repeat is not null || i.Resting is { Count: > 0 })
            .ToDictionary(i => i.Id, i => new SavedRepeats(i.Repeat, i.Resting), StringComparer.Ordinal);
        if (repeats.Count > 0)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("repeats", JsonSerializer.Serialize(repeats, Json)));
        }

        var places = s.Inhabitants.Where(i => i.Places is { Count: > 0 }).ToDictionary(i => i.Id, i => i.Places!, StringComparer.Ordinal);
        if (places.Count > 0)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("places", JsonSerializer.Serialize(places, Json)));
        }

        // Own names and known names (W1698) by inhabitant id; older saves have no entry (the core names them on load).
        var names = s.Inhabitants.Where(i => i.Name is not null || i.KnownNames is { Count: > 0 })
            .ToDictionary(i => i.Id, i => new SavedNames(i.Name, i.KnownNames), StringComparer.Ordinal);
        if (s.NamesSaved)
        {
            // Written even when empty: its presence marks a save game that knows names.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("names", JsonSerializer.Serialize(names, Json)));
        }

        if (s.Player is not null)
        {
            // Player part (B6) as one JSON value; doubles round-trip exactly, so the hash survives.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("player", JsonSerializer.Serialize(s.Player, Json)));
        }

        if (s.Writing is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("writing", JsonSerializer.Serialize(s.Writing, Json)));
        }

        if (s.SkillGains is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("skill_gains", JsonSerializer.Serialize(s.SkillGains, Json)));
        }

        if (s.WritingSeen is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("writing_seen", JsonSerializer.Serialize(s.WritingSeen, Json)));
        }

        if (s.Weather is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("weather", JsonSerializer.Serialize(s.Weather, Json)));
        }

        if (s.Exploration is { } explored)
        {
            // Seen cells (B2-6 R14): the player's view under the empty owner, then one row per inhabitant.
            Insert(c, tx, "INSERT INTO exploration(owner, width, depth, cells) VALUES($a,$b,$c,$d)", (string.Empty, explored.Width, explored.Depth, explored.Player));
            var looks = (explored.Looks ?? []).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
            foreach (var (owner, cells) in explored.Seen)
            {
                Simulation.Exploration.LastLook? look = looks.TryGetValue(owner, out var last) ? last : null;
                Insert(c, tx, "INSERT INTO exploration(owner, width, depth, cells, looked_cell, looked_range) VALUES($a,$b,$c,$d,$e,$f)", (owner, explored.Width, explored.Depth, cells, look?.Cell, look?.RangeCells));
            }
        }

        if (s.LookEpoch is not null)
        {
            // Look epoch (B2-5 R16): written once the world left its first epoch; no entry = first epoch.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("look_epoch", s.LookEpoch));
        }

        if (s.Skills is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("skills", JsonSerializer.Serialize(s.Skills, Json)));
        }

        if (s.Mailbox is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("mailbox", JsonSerializer.Serialize(s.Mailbox, Json)));
        }

        if (s.Conversation is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("conversation", JsonSerializer.Serialize(s.Conversation, Json)));
        }

        if (s.ContentFingerprint is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", (ContentFingerprintKey, s.ContentFingerprint));
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
                Insert(c, tx, "INSERT INTO beliefs VALUES($a,$b,$c,$d,$e,$f,$g,$h)", (i.Id, b.TypeId, b.PropertyId, b.Value ? 1 : 0, b.Confidence, b.Source, b.ToldBy, b.Tick));
            }

            foreach (var r in i.Relations ?? [])
            {
                Insert(c, tx, "INSERT INTO relationships VALUES($a,$b,$c,$d,$e)", (i.Id, r.Other, r.Affection, r.Trust, r.Respect));
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

        // The whole log is saved, never trimmed: replays need every entry, and a restore takes the request
        // language from the last set_display_locale command in it (SimulationHost.UseLoggedDisplayLocale).
        // Any future trimming or compaction must keep the set_display_locale commands (#20043).
        var seq = 0;
        foreach (var e in s.DecisionLog)
        {
            Insert(c, tx, "INSERT INTO decision_log VALUES($a,$b,$c,$d,$e,$f)", (seq++, e.Kind.ToString(), e.RequestId, e.Tick, e.Json, e.Note));
        }

        WriteInventions(c, tx, s.Inventions);
        foreach (var r in s.Chronicle ?? [])
        {
            Insert(c, tx, "INSERT INTO chronicle VALUES($a,$b,$c,$d,$e,$f)", (r.Seq, r.Tick, r.Kind, r.InhabitantId, r.TargetId, r.Action));
        }
    }

    /// <summary>
    /// Invention state (D471 D5). Runtime types are not stored separately: they follow from the inventions
    /// (one source of truth) and are registered again on load.
    /// </summary>
    private static void WriteInventions(SqliteConnection c, SqliteTransaction tx, InventionState? state)
    {
        if (state is null)
        {
            return;
        }

        Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("invention_counter", Str(state.Counter)));
        if (state.Builds is { Count: > 0 } builds)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("builds", JsonSerializer.Serialize(builds, Json)));
        }

        if (state.FirstUsed is { Count: > 0 } firstUsed)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("first_used", JsonSerializer.Serialize(firstUsed, Json)));
        }

        if (state.Sounds is { Count: > 0 } sounds)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("invention_sounds", JsonSerializer.Serialize(sounds, Json)));
        }

        foreach (var r in state.Inventions)
        {
            Insert(c, tx, "INSERT INTO inventions VALUES($a,$b,$c,$d,$e,$f)", (r.TypeId, r.RequestId, r.InhabitantId, r.Tick, r.InventionJson, r.SoundJson));
        }

        foreach (var w in state.Wants)
        {
            Insert(c, tx, "INSERT INTO wants VALUES($a,$b)", (w.InhabitantId, JsonSerializer.Serialize(w)));
        }

        foreach (var (key, tick) in state.Below)
        {
            Insert(c, tx, "INSERT INTO invention_timers VALUES($a,$b,$c)", ("below", key, tick));
        }

        foreach (var (key, tick) in state.Cooldowns)
        {
            Insert(c, tx, "INSERT INTO invention_timers VALUES($a,$b,$c)", ("cooldown", key, tick));
        }

        var seq = 0;
        foreach (var e in state.Log)
        {
            Insert(c, tx, "INSERT INTO job_log VALUES($a,$b,$c,$d,$e,$f)", (seq++, e.Kind.ToString(), e.RequestId, e.Tick, e.Json, e.Note));
        }
    }

    private static InventionState ReadInventions(SqliteConnection c, Dictionary<string, string> meta)
    {
        var timers = Query(c, "SELECT kind, key, tick FROM invention_timers ORDER BY kind, key", r => (Kind: r.GetString(0), Entry: new KeyValuePair<string, long>(r.GetString(1), r.GetInt64(2))));
        return new InventionState(
            meta.TryGetValue("invention_counter", out var counter) ? long.Parse(counter, CultureInfo.InvariantCulture) : 0,
            [.. timers.Where(t => t.Kind == "below").Select(t => t.Entry)],
            [.. timers.Where(t => t.Kind == "cooldown").Select(t => t.Entry)],
            Query(c, "SELECT data FROM wants ORDER BY inhabitant", r => JsonSerializer.Deserialize<OpenWant>(r.GetString(0))!),
            Query(c, "SELECT type_id, request_id, inhabitant, tick, invention, sound FROM inventions ORDER BY type_id",
                r => new InventionRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5))),
            Query(c, "SELECT kind, request_id, tick, json, note FROM job_log ORDER BY seq",
                r => new DecisionLogEntry(Enum.Parse<DecisionLogKind>(r.GetString(0)), r.GetString(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4))),
            meta.TryGetValue("builds", out var builds) ? JsonSerializer.Deserialize<List<BuildTask>>(builds, Json) : null,
            meta.TryGetValue("first_used", out var firstUsed) ? JsonSerializer.Deserialize<List<string>>(firstUsed, Json) : null,
            meta.TryGetValue("invention_sounds", out var sounds) ? JsonSerializer.Deserialize<List<SoundTask>>(sounds, Json) : null);
    }

    private static SaveState Read(SqliteConnection c)
    {
        var meta = Query(c, "SELECT key, value FROM meta", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        var beliefs = Query(c, "SELECT inhabitant, type, property, value, confidence, source, told_by, tick FROM beliefs ORDER BY inhabitant, type, property",
                r => (Inhabitant: r.GetString(0), Belief: new SavedBelief(r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetDouble(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7))))
            .ToLookup(x => x.Inhabitant, x => x.Belief, StringComparer.Ordinal);
        var relations = Query(c, "SELECT inhabitant, other, affection, trust, respect FROM relationships ORDER BY inhabitant, other",
                r => (Inhabitant: r.GetString(0), Relation: new SavedRelation(r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4))))
            .ToLookup(x => x.Inhabitant, x => x.Relation, StringComparer.Ordinal);
        var appearances = meta.TryGetValue("appearances", out var appearanceJson)
            ? JsonSerializer.Deserialize<Dictionary<string, Appearance>>(appearanceJson, Json) ?? []
            : [];
        var searches = meta.TryGetValue("search_since", out var searchJson)
            ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(searchJson, Json) ?? []
            : [];
        var repeats = meta.TryGetValue("repeats", out var repeatJson)
            ? JsonSerializer.Deserialize<Dictionary<string, SavedRepeats>>(repeatJson, Json) ?? []
            : [];
        var places = meta.TryGetValue("places", out var placeJson)
            ? JsonSerializer.Deserialize<Dictionary<string, List<SavedPlace>>>(placeJson, Json) ?? []
            : [];
        var names = meta.TryGetValue("names", out var nameJson)
            ? JsonSerializer.Deserialize<Dictionary<string, SavedNames>>(nameJson, Json) ?? []
            : [];
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
                    [.. beliefs[r.GetString(0)]],
                    appearances.GetValueOrDefault(r.GetString(0)),
                    [.. relations[r.GetString(0)]],
                    searches.GetValueOrDefault(r.GetString(0)),
                    places.GetValueOrDefault(r.GetString(0)),
                    repeats.GetValueOrDefault(r.GetString(0))?.Repeat,
                    repeats.GetValueOrDefault(r.GetString(0))?.Resting,
                    names.GetValueOrDefault(r.GetString(0))?.Name,
                    names.GetValueOrDefault(r.GetString(0))?.KnownNames)),
            long.Parse(meta["question_counter"], CultureInfo.InvariantCulture),
            Query(c, "SELECT data FROM questions ORDER BY id", r => JsonSerializer.Deserialize<SavedQuestion>(r.GetString(0))!),
            Query(c, "SELECT key, tick FROM last_asked ORDER BY key", r => new KeyValuePair<string, long>(r.GetString(0), r.GetInt64(1))),
            Query(c, "SELECT kind, request_id, tick, json, note FROM decision_log ORDER BY seq",
                r => new DecisionLogEntry(Enum.Parse<DecisionLogKind>(r.GetString(0)), r.GetString(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4))),
            meta.GetValueOrDefault("world_preset"),
            meta.GetValueOrDefault("island_hash") is { Length: > 0 } islandHash ? islandHash : null,
            meta.TryGetValue("occurrences", out var occurrences) ? JsonSerializer.Deserialize<List<Map.Occurrence>>(occurrences) : null,
            ReadInventions(c, meta),
            Query(c, "SELECT seq, tick, kind, inhabitant, target, action FROM chronicle ORDER BY seq",
                r => new ChronicleRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5))))
        {
            // Save games from before the player system have no entry: the world keeps its defaults.
            Player = meta.TryGetValue("player", out var player) ? JsonSerializer.Deserialize<Simulation.Player.PlayerState>(player, Json) : null,
            Writing = meta.TryGetValue("writing", out var writing) ? JsonSerializer.Deserialize<List<Simulation.Writing.WrittenText>>(writing, Json) : null,
            WritingSeen = meta.TryGetValue("writing_seen", out var seen) ? JsonSerializer.Deserialize<List<string>>(seen, Json) : null,
            SkillGains = meta.TryGetValue("skill_gains", out var gains) ? JsonSerializer.Deserialize<List<Simulation.Inhabitants.SkillGain>>(gains, Json) : null,
            IslandData = ReadIsland(c, meta),
            Weather = meta.TryGetValue("weather", out var weather) ? JsonSerializer.Deserialize<Weather.WeatherState>(weather, Json) : null,
            LookEpoch = meta.GetValueOrDefault("look_epoch"),
            Exploration = ReadExploration(c),
            Skills = meta.TryGetValue("skills", out var skills) ? JsonSerializer.Deserialize<Simulation.Inhabitants.SkillRegisterState>(skills, Json) : null,
            Mailbox = meta.TryGetValue("mailbox", out var mailbox) ? JsonSerializer.Deserialize<Simulation.Player.MailboxState>(mailbox, Json) : null,
            NamesSaved = meta.ContainsKey("names"),
            Conversation = meta.TryGetValue("conversation", out var conversation) ? JsonSerializer.Deserialize<Simulation.Player.ConversationState>(conversation, Json) : null,
            ContentFingerprint = meta.GetValueOrDefault(ContentFingerprintKey),
        };
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

    /// <summary>
    /// The island blob (schema 3); stands written between B2-4 builds kept it as base64 in meta and are
    /// still read.
    /// </summary>
    private static byte[]? ReadIsland(SqliteConnection c, Dictionary<string, string> meta)
    {
        if (HasTable(c, "island"))
        {
            var rows = Query(c, "SELECT data FROM island WHERE id = 1", r => (byte[])r.GetValue(0));
            if (rows.Count > 0)
            {
                return rows[0];
            }
        }

        return meta.TryGetValue("island_data", out var base64) ? Convert.FromBase64String(base64) : null;
    }

    /// <summary>The seen cells (schema 7); null when the file has no row for the player's view (worlds without an island, files from before).</summary>
    private static Simulation.Exploration.ExplorationState? ReadExploration(SqliteConnection c)
    {
        var rows = Query(
            c,
            "SELECT owner, width, depth, cells, looked_cell, looked_range FROM exploration",
            r => (Owner: r.GetString(0), Width: r.GetInt32(1), Depth: r.GetInt32(2), Cells: (byte[])r.GetValue(3), Look: r.IsDBNull(4) || r.IsDBNull(5) ? (Simulation.Exploration.LastLook?)null : new Simulation.Exploration.LastLook(r.GetInt32(4), r.GetInt32(5))));
        var player = rows.Where(r => r.Owner.Length == 0).ToList();
        var seen = rows.Where(r => r.Owner.Length > 0).OrderBy(r => r.Owner, StringComparer.Ordinal).ToList();
        return player.Count == 0
            ? null
            : new Simulation.Exploration.ExplorationState(
                player[0].Width,
                player[0].Depth,
                player[0].Cells,
                [.. seen.Select(r => new KeyValuePair<string, byte[]>(r.Owner, r.Cells))])
            {
                // The last looks (schema 8); rows from before have none.
                Looks = [.. seen.Where(r => r.Look is not null).Select(r => new KeyValuePair<string, Simulation.Exploration.LastLook>(r.Owner, r.Look!.Value))],
            };
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

    /// <summary>
    /// An inhabitant's repeats without effect and resting actions as saved (W1696). Optional meta entry: saves
    /// without it load with nothing resting; unknown fields are ignored on read.
    /// </summary>
    private sealed record SavedRepeats(RepeatRun? Repeat, IReadOnlyList<RestingAction>? Resting);

    private sealed record SavedNames(string? Name, IReadOnlyList<string>? KnownNames);
}

/// <summary>Saves the world after every game day (Leitbild: at most one game day lost after a crash).</summary>
public sealed class AutoSaver
{
    /// <summary>Hooks the autosave onto the day boundary of a simulation.</summary>
    /// <param name="host">The simulation.</param>
    /// <param name="path">Save game.</param>
    /// <param name="contentFingerprint">Fingerprint of the content folder to store with every save (null = none).</param>
    public AutoSaver(SimulationHost host, string path, string? contentFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(path);
        host.World.BoundaryReached += (_, boundary) =>
        {
            if (boundary == TimeBoundary.Day)
            {
                SaveGameStore.Save(path, StateCodec.Capture(host) with { ContentFingerprint = contentFingerprint });
                SavedTicks.Add(host.World.Tick);
            }
        };
    }

    /// <summary>Ticks at which an autosave happened (tests, logs).</summary>
    public List<long> SavedTicks { get; } = [];
}
