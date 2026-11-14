using System.Globalization;
using System.Security.Cryptography;
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
/// next to the target, read back and checked, and then swapped in; the file it replaces stays as the last good
/// state (<see cref="PreviousPath"/>), and a SHA-256 beside the file lets loading tell a damaged file (B2712,
/// D1056). Every file carries a schema version; older files are migrated
/// on load, newer ones are refused. Doubles are stored as SQLite REAL (IEEE 754), so a reload is exact.
/// </summary>
public static class SaveGameStore
{
    /// <summary>Meta key of the content fingerprint (<see cref="ContentFingerprint"/>), written by headless and game.</summary>
    public const string ContentFingerprintKey = "content_fingerprint";

    /// <summary>Schema version written by this code.</summary>
    public const int SchemaVersion = 11;

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

        // 9 (diary, TL #20472): each inhabitant's diary of started actions (presentation-only history, not
        // in the world hash). Files from before have no rows: their inhabitants start with an empty diary.
        """
        CREATE TABLE IF NOT EXISTS diary(seq INTEGER PRIMARY KEY, tick INTEGER NOT NULL, inhabitant TEXT NOT NULL, action TEXT NOT NULL, target TEXT, target_type TEXT, topic TEXT);
        """,

        // 10 (B4a R6/R8): imprinting counters and who has met whom. Files from before have no counters; the
        // members of a start group have met (as at a new start), relationships stay as they are, and
        // everybody else meets when they first see each other.
        """
        CREATE TABLE IF NOT EXISTS imprints(inhabitant TEXT NOT NULL, action TEXT NOT NULL, about_kind TEXT NOT NULL, about_id TEXT NOT NULL, time_of_day TEXT NOT NULL, count REAL NOT NULL,
          PRIMARY KEY(inhabitant, action, about_kind, about_id, time_of_day));
        CREATE TABLE IF NOT EXISTS met(inhabitant TEXT NOT NULL, other TEXT NOT NULL, PRIMARY KEY(inhabitant, other));
        INSERT OR IGNORE INTO met SELECT a.id, b.id FROM inhabitants a JOIN inhabitants b ON a.grp = b.grp AND a.id <> b.id;
        """,

        // 11 (memory, B4a R5): each inhabitant's summary with its running numbers (newest event, sent to a
        // condensing) and what it has remembered for the first time, and its remembered events with how
        // often each happened and since when. Files from before have no rows: their inhabitants start with
        // an empty memory and remember from then on.
        """
        CREATE TABLE IF NOT EXISTS memories(inhabitant TEXT PRIMARY KEY, summary TEXT NOT NULL, last_seq INTEGER NOT NULL, firsts TEXT NOT NULL, sealed_seq INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS memory_events(inhabitant TEXT NOT NULL, seq INTEGER NOT NULL, tick INTEGER NOT NULL, kind TEXT NOT NULL, about_kind TEXT, about_id TEXT,
          formative INTEGER NOT NULL, condensed INTEGER NOT NULL, times INTEGER NOT NULL DEFAULT 1, since INTEGER, outcome TEXT, PRIMARY KEY(inhabitant, seq));
        """,
    ];

    /// <summary>
    /// Saves a state (B2712): written into a copy, read back and checked (SQLite quick_check, schema version, the
    /// state reads and is the one written); only then swapped in. The file it replaces stays as the last good state
    /// (<see cref="PreviousPath"/>, one generation), and the SHA-256 of the new file is kept beside it. A copy
    /// that fails the check never replaces anything (<see cref="InvalidDataException"/>).
    /// </summary>
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

        try
        {
            ReadBack(temp, state);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }

        // The checksum goes beside the file in three steps, so that a crash at any point leaves a file whose
        // checksum is known: the new one waits as .new until the swap is done; the old one goes with the old file.
        var sum = SumPath(path);
        File.WriteAllText(sum + ".new", Sha256Of(temp));
        if (File.Exists(path))
        {
            var previous = PreviousPath(path);
            if (File.Exists(sum))
            {
                File.Copy(sum, SumPath(previous), overwrite: true);
            }
            else
            {
                File.Delete(SumPath(previous));
            }

            File.Replace(temp, path, previous);
        }
        else
        {
            File.Move(temp, path);
        }

        File.Move(sum + ".new", sum, overwrite: true);
    }

    /// <summary>Key in <see cref="Exception.Data"/> of a damaged save's exception: the path of the last good state.</summary>
    public const string PreviousKey = "previous";

    /// <summary>The last good state kept beside a save game (the file the latest save replaced).</summary>
    public static string PreviousPath(string path) => path + ".prev";

    /// <summary>
    /// Whether a save game is whole (B2712): its SHA-256 matches the one kept beside it (a file without one, e.g.
    /// from before or imported, counts as whole), and SQLite's quick_check passes. Never changes the file.
    /// </summary>
    public static bool IsIntact(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path) || !SumMatches(path))
        {
            return false;
        }

        try
        {
            using var connection = Open(path, readOnly: true);
            return QuickCheck(connection);
        }
        catch (SqliteException)
        {
            return false;
        }
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

        // B2712: a file whose checksum does not match was damaged after it was written; the game can offer the last
        // good state (Data[PreviousKey], when there is one).
        if (!SumMatches(path))
        {
            var damaged = new InvalidDataException($"'{Path.GetFileName(path)}' was damaged after it was saved.");
            if (File.Exists(PreviousPath(path)))
            {
                damaged.Data[PreviousKey] = PreviousPath(path);
            }

            throw damaged;
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

    private static string SumPath(string path) => path + ".sha256";

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>No checksum kept, or the file matches it (or the one a save left as .new when it stopped after the swap).</summary>
    private static bool SumMatches(string path)
    {
        var sum = SumPath(path);
        var kept = new[] { sum, sum + ".new" }.Where(File.Exists).Select(f => File.ReadAllText(f).Trim()).ToList();
        if (kept.Count == 0)
        {
            return true;
        }

        var actual = Sha256Of(path);
        return kept.Any(k => string.Equals(k, actual, StringComparison.OrdinalIgnoreCase));
    }

    private static bool QuickCheck(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        return string.Equals(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.Ordinal);
    }

    /// <summary>The written copy reads back as the state written: quick_check, schema version, world, tick and objects.</summary>
    private static void ReadBack(string temp, SaveState state)
    {
        using var connection = Open(temp, readOnly: true);
        if (!QuickCheck(connection) || UserVersion(connection) != SchemaVersion)
        {
            throw new InvalidDataException("The written save game did not pass its check; the previous one is kept.");
        }

        var read = Read(connection);
        if (read.Seed != state.Seed || read.Tick != state.Tick || read.Objects.Count != state.Objects.Count || read.Inhabitants.Count != state.Inhabitants.Count)
        {
            throw new InvalidDataException("The written save game reads back differently; the previous one is kept.");
        }
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

        // Ideas and intentions (A1584) by inhabitant id; older saves have no entry.
        var ideas = s.Inhabitants.Where(i => i.Ideas is not null).ToDictionary(i => i.Id, i => i.Ideas!, StringComparer.Ordinal);
        if (ideas.Count > 0)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("ideas", JsonSerializer.Serialize(ideas, Json)));
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

        if (s.Climate is not null)
        {
            // The chosen climate (B5 R1, B2676): no entry = the preset's climate, as in every older save.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("climate", s.Climate));
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

        if (s.Needs is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("needs", JsonSerializer.Serialize(s.Needs, Json)));
        }

        if (s.Mailbox is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("mailbox", JsonSerializer.Serialize(s.Mailbox, Json)));
        }

        if (s.Favourites is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("favourites", JsonSerializer.Serialize(s.Favourites, Json)));
        }

        if (s.Conversation is not null)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("conversation", JsonSerializer.Serialize(s.Conversation, Json)));
        }

        if (s.NightJobs is not null)
        {
            // Open memory and question jobs of the compute night (schema 11); no entry = none.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("night_jobs", JsonSerializer.Serialize(s.NightJobs, Json)));
        }

        if (s.Processes is not null)
        {
            // Judged tries, open process jobs and variant types (B2687 K2); no entry = none.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("processes", JsonSerializer.Serialize(s.Processes, Json)));
        }

        if (s.Burning is not null)
        {
            // Burning things and when they burn down (B2687 K3); no entry = none.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("burning", JsonSerializer.Serialize(s.Burning, Json)));
        }

        if (s.Forgetting is not null)
        {
            // Fading and forgotten knowledge (D1017 G1); no entry = none.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("forgetting", JsonSerializer.Serialize(s.Forgetting, Json)));
        }

        if (s.Nature is not null)
        {
            // The nature takt's sums (A1380); no entry = none.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("nature", JsonSerializer.Serialize(s.Nature, Json)));
        }

        if (s.Placed is not null)
        {
            // Things that came after the world start (durability, D711 section 5); no entry = none.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("placed", JsonSerializer.Serialize(s.Placed, Json)));
        }

        if (s.Wet is not null)
        {
            // How wet the inhabitants are (B5-11); no entry = nobody.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("wet", JsonSerializer.Serialize(s.Wet, Json)));
        }

        if (s.Milestones is not null)
        {
            // Reached milestones (B2598); no entry = none.
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("milestones", JsonSerializer.Serialize(s.Milestones, Json)));
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

            foreach (var other in i.Met ?? [])
            {
                Insert(c, tx, "INSERT INTO met VALUES($a,$b)", (i.Id, other));
            }

            foreach (var m in i.Imprints ?? [])
            {
                Insert(c, tx, "INSERT INTO imprints VALUES($a,$b,$c,$d,$e,$f)", (i.Id, m.Action, m.AboutKind, m.AboutId, m.TimeOfDay, m.Count));
            }

            if (i.Memory is { } memory)
            {
                Insert(c, tx, "INSERT INTO memories VALUES($a,$b,$c,$d,$e)", (i.Id, memory.Summary, memory.LastSeq, JsonSerializer.Serialize(memory.Firsts), memory.SealedSeq));
                foreach (var e in memory.Events)
                {
                    Insert(c, tx, "INSERT INTO memory_events VALUES($a,$b,$c,$d,$e,$f,$g,$h,$i,$j,$k)", (i.Id, e.Seq, e.Tick, e.Kind, e.AboutKind, e.AboutId, e.Formative ? 1 : 0, e.Condensed ? 1 : 0, e.Times, e.Since, e.Outcome));
                }
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
        // language from the last set_display_locale command in it (SimulationHost.UseLoggedPlayerSettings).
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

        if (s.DiaryVersion is { } diaryVersion)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("diary_version", Str(diaryVersion)));
        }

        foreach (var d in s.Diary ?? [])
        {
            Insert(c, tx, "INSERT INTO diary VALUES($a,$b,$c,$d,$e,$f,$g)", (d.Seq, d.Tick, d.InhabitantId, d.Action, d.TargetId, d.TargetType, d.Topic));
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

        if (state.Images is { Count: > 0 } images)
        {
            Insert(c, tx, "INSERT INTO meta VALUES($a,$b)", ("invention_images", JsonSerializer.Serialize(images, Json)));
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
            meta.TryGetValue("invention_sounds", out var sounds) ? JsonSerializer.Deserialize<List<SoundTask>>(sounds, Json) : null,
            meta.TryGetValue("invention_images", out var images) ? JsonSerializer.Deserialize<List<ImageTask>>(images, Json) : null);
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
        var met = Query(c, "SELECT inhabitant, other FROM met ORDER BY inhabitant, other", r => (Inhabitant: r.GetString(0), Other: r.GetString(1)))
            .ToLookup(x => x.Inhabitant, x => x.Other, StringComparer.Ordinal);
        var imprints = Query(c, "SELECT inhabitant, action, about_kind, about_id, time_of_day, count FROM imprints ORDER BY inhabitant, action, about_kind, about_id, time_of_day",
                r => (Inhabitant: r.GetString(0), Imprint: new SavedImprint(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDouble(5))))
            .ToLookup(x => x.Inhabitant, x => x.Imprint, StringComparer.Ordinal);
        var memoryEvents = Query(c, "SELECT inhabitant, seq, tick, kind, about_kind, about_id, formative, condensed, times, since, outcome FROM memory_events ORDER BY inhabitant, seq",
                r => (Inhabitant: r.GetString(0), Entry: new MemoryEntry(r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6) != 0, r.GetInt64(7) != 0, r.GetInt32(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetString(10))))
            .ToLookup(x => x.Inhabitant, x => x.Entry, StringComparer.Ordinal);
        var memories = Query(c, "SELECT inhabitant, summary, last_seq, firsts, sealed_seq FROM memories ORDER BY inhabitant",
                r => (Inhabitant: r.GetString(0), Summary: r.GetString(1), LastSeq: r.GetInt64(2), Firsts: JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? [], SealedSeq: r.GetInt64(4)))
            .ToDictionary(x => x.Inhabitant, x => new SavedMemory(x.Summary, x.LastSeq, [.. memoryEvents[x.Inhabitant]], x.Firsts, x.SealedSeq), StringComparer.Ordinal);
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
        var ideas = meta.TryGetValue("ideas", out var ideaJson)
            ? JsonSerializer.Deserialize<Dictionary<string, SavedIdeas>>(ideaJson, Json) ?? []
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
                    names.GetValueOrDefault(r.GetString(0))?.KnownNames,
                    met[r.GetString(0)].ToList() is { Count: > 0 } metList ? metList : null,
                    imprints[r.GetString(0)].ToList() is { Count: > 0 } imprintList ? imprintList : null,
                    memories.GetValueOrDefault(r.GetString(0)),
                    ideas.GetValueOrDefault(r.GetString(0)))),
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
            Climate = meta.GetValueOrDefault("climate"),
            Exploration = ReadExploration(c),
            Skills = meta.TryGetValue("skills", out var skills) ? JsonSerializer.Deserialize<Simulation.Inhabitants.SkillRegisterState>(skills, Json) : null,
            Needs = meta.TryGetValue("needs", out var needs) ? JsonSerializer.Deserialize<Simulation.Inhabitants.NeedRegisterState>(needs, Json) : null,
            Mailbox = meta.TryGetValue("mailbox", out var mailbox) ? JsonSerializer.Deserialize<Simulation.Player.MailboxState>(mailbox, Json) : null,
            Favourites = meta.TryGetValue("favourites", out var favourites) ? JsonSerializer.Deserialize<Simulation.Player.FavouritesState>(favourites, Json) : null,
            NamesSaved = meta.ContainsKey("names"),
            Conversation = meta.TryGetValue("conversation", out var conversation) ? JsonSerializer.Deserialize<Simulation.Player.ConversationState>(conversation, Json) : null,
            ContentFingerprint = meta.GetValueOrDefault(ContentFingerprintKey),
            NightJobs = meta.TryGetValue("night_jobs", out var nightJobs) ? JsonSerializer.Deserialize<Simulation.Night.NightJobsState>(nightJobs, Json) : null,
            Processes = meta.TryGetValue("processes", out var processes) ? JsonSerializer.Deserialize<Simulation.Objects.ProcessState>(processes, Json) : null,
            Burning = meta.TryGetValue("burning", out var burning) ? JsonSerializer.Deserialize<List<Simulation.Objects.BurningThing>>(burning, Json) : null,
            Forgetting = meta.TryGetValue("forgetting", out var forgetting) ? JsonSerializer.Deserialize<Simulation.Inhabitants.ForgettingState>(forgetting, Json) : null,
            Nature = meta.TryGetValue("nature", out var nature) ? JsonSerializer.Deserialize<Simulation.Nature.NatureState>(nature, Json) : null,
            Placed = meta.TryGetValue("placed", out var placed) ? JsonSerializer.Deserialize<List<Simulation.Objects.PlacedThing>>(placed, Json) : null,
            Wet = meta.TryGetValue("wet", out var wet) ? JsonSerializer.Deserialize<List<Simulation.Inhabitants.WetInhabitant>>(wet, Json) : null,
            Milestones = meta.TryGetValue("milestones", out var milestones) ? JsonSerializer.Deserialize<List<Simulation.Inventions.ReachedMilestone>>(milestones, Json) : null,
            DiaryVersion = meta.TryGetValue("diary_version", out var diaryVersion) ? long.Parse(diaryVersion, CultureInfo.InvariantCulture) : null,
            Diary = Query(c, "SELECT seq, tick, inhabitant, action, target, target_type, topic FROM diary ORDER BY seq",
                r => new Simulation.Inhabitants.DiaryEntry(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6))),
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
