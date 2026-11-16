using System.Globalization;
using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>One thing the late game scenario lays out: its object type and where it lies.</summary>
/// <param name="TypeId">Catalog id.</param>
/// <param name="X">Where (m).</param>
/// <param name="Z">Where (m).</param>
public sealed record LatePlacement(string TypeId, double X, double Z);

/// <summary>
/// The late game scenario of the benchmark (B2810, Marcel W2197): what each crowd figure carries, the things laid out
/// around the crowd (fires first, then the placed things in turn) and the weather at a moment of a pass. The spots are
/// spread evenly over a disc on land; deterministic: the same data and seed give the same scenario on every machine.
/// </summary>
public sealed class BenchmarkLatePlan
{
    private const double GoldenAngle = 2.399963229728653;
    private readonly BenchmarkLateGame _data;

    private BenchmarkLatePlan(BenchmarkLateGame data, IReadOnlyList<LatePlacement> placements) => (_data, Placements) = (data, placements);

    /// <summary>Fires first, then the placed things, each on land.</summary>
    public IReadOnlyList<LatePlacement> Placements { get; }

    /// <summary>Plans the scenario around <paramref name="centre"/>; <paramref name="onLand"/> says whether a spot is dry ground.</summary>
    public static BenchmarkLatePlan Build(BenchmarkLateGame data, ulong seed, (double X, double Z) centre, Func<double, double, bool> onLand)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(onLand);
        var count = data.Fires + data.PlacedCount;
        var types = data.PlacedTypes.ToList();
        var placements = new List<LatePlacement>(count);

        // Sunflower spots over the whole disc, turned by the seed; water is skipped. The pattern gets denser until enough
        // spots lie on land, then every n-th of them is taken, so the things spread over the whole disc on land.
        var turn = (seed % 3600) / 3600.0 * 2 * Math.PI;
        var land = new List<(double X, double Z)>();
        for (var trials = Math.Max(count, 1); land.Count < count; trials *= 2)
        {
            if (trials > Math.Max(64, count * 64))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Only {land.Count} of {count} late game spots lie on land; lower late_game.placed_count or raise placed_radius_m (content/look/benchmark.json)."));
            }

            land.Clear();
            for (var k = 0; k < trials; k++)
            {
                var r = data.PlacedRadiusM * Math.Sqrt((k + 0.5) / trials);
                var a = turn + (k * GoldenAngle);
                var (x, z) = (centre.X + (r * Math.Cos(a)), centre.Z + (r * Math.Sin(a)));
                if (onLand(x, z))
                {
                    land.Add((x, z));
                }
            }
        }

        for (var i = 0; i < count; i++)
        {
            var (x, z) = land[(int)((long)i * land.Count / count)];
            placements.Add(new LatePlacement(i < data.Fires ? data.FireType : types[(i - data.Fires) % types.Count], x, z));
        }

        return new BenchmarkLatePlan(data, placements);
    }

    /// <summary>The object type the crowd figure with this index carries.</summary>
    public string HeldType(int figure)
    {
        var held = _data.HeldTypes.ToList();
        return held[((figure % held.Count) + held.Count) % held.Count];
    }

    /// <summary>The weather <paramref name="timeS"/> real seconds into a pass (the data's cycle, one step per change time).</summary>
    public string WeatherAt(double timeS)
    {
        var cycle = _data.WeatherCycle.ToList();
        var step = (long)Math.Floor(Math.Max(0, timeS) / _data.WeatherChangeS);
        return cycle[(int)(step % cycle.Count)];
    }
}
