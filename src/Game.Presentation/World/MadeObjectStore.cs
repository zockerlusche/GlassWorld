using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inventions;

namespace GlassWorld.Presentation.World;

/// <summary>Where a collected object came from (B2811, D1168 stage 0).</summary>
/// <param name="World">The world's id and seed, e.g. <c>meine-insel/123</c>.</param>
/// <param name="Day">Game day it was made.</param>
/// <param name="InhabitantId">Who made it, or null.</param>
/// <param name="TypeId">The invention type it belongs to in that world.</param>
/// <param name="RequestId">The AI job it came from, or null.</param>
/// <param name="Model">The model that made it, or null when the log does not say.</param>
/// <param name="PromptVersion">The prompt version, or null.</param>
/// <param name="Guard">The content guard's result: only approved things are kept.</param>
public sealed record CollectedOrigin(string World, long Day, string? InhabitantId, string TypeId, string? RequestId, string? Model, string? PromptVersion, string Guard);

/// <summary>
/// A1776 (B2811, Marcel W2198, D1168 stage 0): every object the AI made - an invention's description, its building plan,
/// its painted icon, its sound - is kept in a local collection beside the saves, with a stable id (SHA-256 of kind and
/// content, so the same thing from another world is the same id) and where it came from. A duplicate is kept once.
/// Only the look and the words are kept, never what the world knows about the thing (B2741): stage 1 may reuse a look,
/// the properties always come from the own core. One folder per object: <c>&lt;root&gt;/&lt;id[0..2]&gt;/&lt;id&gt;/object.json</c>
/// plus its files.
/// </summary>
public sealed class MadeObjectStore(string root)
{
    /// <summary>The guard result of everything the core approved (only approved inventions exist in a world).</summary>
    public const string Approved = "approved";

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>The collection's folder.</summary>
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>
    /// Keeps one object: <paramref name="content"/> (its words and data, without anything world-specific) and
    /// <paramref name="files"/> (name in the collection, path now) make its id. Returns the id and whether it was new.
    /// </summary>
    public (string Id, bool Added) Add(string kind, JsonNode content, IReadOnlyList<(string Name, string Path)> files, CollectedOrigin origin)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(origin);
        var id = IdOf(kind, content, files);
        var folder = Path.Combine(Root, id[..2], id);
        if (Directory.Exists(folder))
        {
            return (id, false);
        }

        // Written beside, then moved: a folder that exists is always complete.
        var temp = folder + ".tmp";
        Directory.CreateDirectory(temp);
        foreach (var (name, path) in files)
        {
            File.Copy(path, Path.Combine(temp, name), overwrite: true);
        }

        var json = new JsonObject
        {
            ["id"] = id,
            ["kind"] = kind,
            ["content"] = content.DeepClone(),
            ["files"] = new JsonArray([.. files.Select(f => (JsonNode)f.Name)]),
            ["origin"] = JsonSerializer.SerializeToNode(origin, Options),
        };
        File.WriteAllText(Path.Combine(temp, "object.json"), json.ToJsonString(Options), new UTF8Encoding(false));
        Directory.Move(temp, folder);
        return (id, true);
    }

    /// <summary>
    /// Takes in what a world's inventions brought so far (called after each save): the description, the building plan,
    /// the icon and the sound of every invention; what was taken before in this session is skipped quickly, what lies in
    /// the collection already is kept once. Returns how many objects were new.
    /// </summary>
    public int CollectFrom(InventionSystem inventions, string world, long ticksPerDay, string? pictureFolder, string? soundFolder)
    {
        ArgumentNullException.ThrowIfNull(inventions);
        var added = 0;
        var records = inventions.Capture().Inventions.ToDictionary(r => r.TypeId, StringComparer.Ordinal);
        foreach (var info in inventions.Inventions)
        {
            var record = records.GetValueOrDefault(info.TypeId);
            var (model, prompt) = ModelOf(inventions.Log, record?.RequestId);
            var origin = new CollectedOrigin(world, (info.Tick / Math.Max(1, ticksPerDay)) + 1, info.InhabitantId, info.TypeId, record?.RequestId, model, prompt, Approved);
            added += Take($"invention|{world}|{info.TypeId}", "invention", Description(info), [], origin);
            if (inventions.Builds.FirstOrDefault(b => b.TypeId == info.TypeId && b.PlanJson is not null)?.PlanJson is { } plan)
            {
                added += Take($"plan|{world}|{info.TypeId}", "build_plan", JsonNode.Parse(plan)!, [], origin);
            }

            if (inventions.ImageOf(info.TypeId) is { } image && pictureFolder is not null && File.Exists(Path.Combine(pictureFolder, image.File)))
            {
                var meta = new JsonObject { ["width"] = image.Width, ["height"] = image.Height, ["workflow"] = image.Workflow, ["style"] = image.Style };
                added += Take($"image|{world}|{info.TypeId}", "icon", meta, [("icon" + Path.GetExtension(image.File), Path.Combine(pictureFolder, image.File))], origin);
            }

            if (info.SoundFile is { } sound && soundFolder is not null && File.Exists(Path.Combine(soundFolder, sound)))
            {
                var meta = new JsonObject { ["description"] = info.SoundDescription };
                added += Take($"sound|{world}|{info.TypeId}", "sound", meta, [("sound" + Path.GetExtension(sound), Path.Combine(soundFolder, sound))], origin);
            }
        }

        return added;
    }

    /// <summary>The id: SHA-256 of the kind, the content (compact JSON) and every file's name and bytes.</summary>
    public static string IdOf(string kind, JsonNode content, IReadOnlyList<(string Name, string Path)> files)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(files);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(kind + "\n" + content.ToJsonString() + "\n"));
        foreach (var (name, path) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\n"));
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private int Take(string key, string kind, JsonNode content, IReadOnlyList<(string Name, string Path)> files, CollectedOrigin origin)
    {
        if (!_seen.Add(key))
        {
            return 0;
        }

        return Add(kind, content, files, origin).Added ? 1 : 0;
    }

    /// <summary>What an invention is, in words - without its world's type id, so the same idea elsewhere is the same object.</summary>
    private static JsonObject Description(InventionInfo info) => new()
    {
        ["name"] = new JsonObject { ["de"] = info.Name.De, ["en"] = info.Name.En },
        ["category"] = info.Category.ToString().ToLowerInvariant(),
        ["purpose"] = info.Purpose,
        ["base_function"] = info.BaseFunction,
    };

    /// <summary>The model and prompt version of the job that made an invention, from the world's job log.</summary>
    private static (string? Model, string? Prompt) ModelOf(DecisionLog log, string? requestId)
    {
        if (requestId is null || log.Entries.LastOrDefault(e => e.RequestId == requestId && e.Kind == DecisionLogKind.JobResult) is not { } entry)
        {
            return (null, null);
        }

        try
        {
            var node = JsonNode.Parse(entry.Json);
            return (Find(node, "model"), Find(node, "prompt_version"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? Find(JsonNode? node, string name) => node switch
    {
        JsonObject o when o.TryGetPropertyValue(name, out var value) && value is JsonValue v && v.TryGetValue<string>(out var text) => text,
        JsonObject o => o.Select(p => Find(p.Value, name)).FirstOrDefault(t => t is not null),
        _ => null,
    };

    /// <summary>The world part of an origin: its id and seed.</summary>
    public static string WorldOf(string id, ulong seed) => string.Create(CultureInfo.InvariantCulture, $"{id}/{seed}");
}
