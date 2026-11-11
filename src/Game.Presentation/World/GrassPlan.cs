using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.World;

/// <summary>The distances of the two grass detail levels (metres from the camera to a chunk's centre).</summary>
/// <param name="NearEndM">Up to here every plant of the quality level's density is drawn.</param>
/// <param name="FarEndM">Up to here the thinned plants are drawn; beyond it none.</param>
/// <param name="MarginM">Hysteresis of the switches.</param>
public readonly record struct GrassRanges(double NearEndM, double FarEndM, double MarginM)
{
    /// <summary>Whether there is a thinned level beyond the near one.</summary>
    public bool HasFar => FarEndM > NearEndM;
}

/// <summary>The plants of one look in one chunk: all of them for the near level, the thinned ones for the far level.</summary>
/// <param name="VisualId">Object look (content/look/objects.json).</param>
/// <param name="ChunkX">Chunk index along x.</param>
/// <param name="ChunkZ">Chunk index along z.</param>
/// <param name="Near">Plants of the near level.</param>
/// <param name="Far">Plants of the far level (a part of <paramref name="Near"/>, so none moves at the switch).</param>
public sealed record GrassChunk(string VisualId, int ChunkX, int ChunkZ, IReadOnlyList<ObjectPlacement> Near, IReadOnlyList<ObjectPlacement> Far);

/// <summary>
/// Grass and small plants in detail levels (B2-5 R21): the decorative plants named in
/// <c>content/look/nature.json</c> are sorted into square chunks, each drawn as one MultiMesh per look and
/// level with a visibility range. The quality level (<c>content/look/quality.json</c>) gives the density
/// (<c>grass_density</c>: the share of plants drawn at all) and the distance (<c>grass_distance_m</c>);
/// beyond <c>near_share</c> of it only <c>far_density_share</c> of them remain. Which plants stay is
/// decided by a hash of the plant's id, so it is the same every time and independent of the others.
/// Presentation only.
/// </summary>
public static class GrassPlan
{
    private const int RankSalt = 7717;

    /// <summary>Splits the decorative plants into grass (drawn in detail levels) and the rest (drawn with the objects).</summary>
    public static (IReadOnlyList<WorldObject> Grass, IReadOnlyList<WorldObject> Other) Split(IReadOnlyList<WorldObject> decor, NatureGrass data)
    {
        ArgumentNullException.ThrowIfNull(decor);
        ArgumentNullException.ThrowIfNull(data);
        var types = data.Types.ToHashSet(StringComparer.Ordinal);
        return ([.. decor.Where(d => types.Contains(d.TypeId))], [.. decor.Where(d => !types.Contains(d.TypeId))]);
    }

    /// <summary>The quality level in use: the default level (which level a hardware tier uses is decided in B7).</summary>
    public static QualityLevel Level(LookQuality quality) => Level(quality, null);

    /// <summary>The quality level <paramref name="id"/> (e.g. the benchmark's <c>--quality=ID</c>), or the default level when null.</summary>
    public static QualityLevel Level(LookQuality quality, string? id)
    {
        ArgumentNullException.ThrowIfNull(quality);
        var wanted = id ?? quality.DefaultLevel;
        return quality.Levels.FirstOrDefault(l => l.Id == wanted)
            ?? throw new InvalidDataException($"Unknown quality level '{wanted}' (content/look/quality.json: {string.Join(", ", quality.Levels.Select(l => l.Id))}).");
    }

    /// <summary>The distances of the levels for a quality level.</summary>
    public static GrassRanges Ranges(NatureGrass data, QualityLevel level)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(level);
        var far = level.GrassDistanceM;
        var near = data.FarDensityShare > 0 ? far * data.NearShare : far;
        return new GrassRanges(near, far, data.SwitchMarginM);
    }

    /// <summary>A plant's rank 0..1: it is drawn while the rank is below the density.</summary>
    public static double Rank(string plantId) => ObjectLooks.Hash01(plantId, RankSalt);

    /// <summary>
    /// The chunks of a layout (placements per look, <see cref="ObjectLooks.Layout"/>), ordered by look, then
    /// z, then x. Empty when the level draws no grass.
    /// </summary>
    public static IReadOnlyList<GrassChunk> Chunks(IReadOnlyDictionary<string, List<ObjectPlacement>> layout, NatureGrass data, QualityLevel level)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(level);
        var result = new List<GrassChunk>();
        if (level.GrassDistanceM <= 0 || level.GrassDensity <= 0)
        {
            return result;
        }

        var farDensity = level.GrassDensity * data.FarDensityShare;
        foreach (var (visual, placements) in layout.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            var chunks = new SortedDictionary<(int Z, int X), (List<ObjectPlacement> Near, List<ObjectPlacement> Far)>();
            foreach (var p in placements)
            {
                var rank = Rank(p.ObjectId);
                if (rank >= level.GrassDensity)
                {
                    continue;
                }

                var key = ((int)Math.Floor(p.Z / data.ChunkM), (int)Math.Floor(p.X / data.ChunkM));
                if (!chunks.TryGetValue(key, out var chunk))
                {
                    chunks[key] = chunk = ([], []);
                }

                chunk.Near.Add(p);
                if (rank < farDensity)
                {
                    chunk.Far.Add(p);
                }
            }

            result.AddRange(chunks.Select(c => new GrassChunk(visual, c.Key.X, c.Key.Z, c.Value.Near, c.Value.Far)));
        }

        return result;
    }
}
