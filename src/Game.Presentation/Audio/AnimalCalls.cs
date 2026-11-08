using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Audio;

/// <summary>One animal call to play.</summary>
/// <param name="Animal">Animal id (B2481).</param>
/// <param name="SoundId">Sound id from the sound map.</param>
/// <param name="Variant">Recipe variant.</param>
/// <param name="Position">Where it sounds (ground plane).</param>
/// <param name="Tick">World tick of the call.</param>
/// <param name="Seed">Seed of this call (songbird melody).</param>
/// <param name="Sung">True when the sound is the songbird melody instead of its recipe.</param>
public sealed record AnimalCall(string Animal, string SoundId, int Variant, Position Position, long Tick, ulong Seed, bool Sung);

/// <summary>Season and phase of the day at the start of a game hour.</summary>
/// <param name="Season">Season id.</param>
/// <param name="TimeOfDay">Phase id (time_of_day vocabulary).</param>
public readonly record struct HourTime(string Season, string TimeOfDay)
{
    /// <summary>From the world calendar (phase as its time_of_day id).</summary>
    public static HourTime Of(Simulation.Time.WorldTime time) => new(time.SeasonId, time.Phase switch
    {
        Simulation.Time.DayPhase.Dawn => "dawn",
        Simulation.Time.DayPhase.Day => "day",
        Simulation.Time.DayPhase.Dusk => "dusk",
        _ => "night",
    });
}

/// <summary>
/// Animal sounds near the camera (B8 R4, B2481): which animal calls where, when and how often comes
/// from the sound map (biome, season, time of day, calls per hour). Calls are fixed per game hour by
/// the world seed, so they do not depend on the frame rate or on how a time span is split into frames.
/// Until animals are objects (B5) a call sounds in a ring around the listener. Presentation only.
/// </summary>
public sealed class AnimalCalls(SoundLibrary library, AnimalSoundSettings settings)
{
    private readonly SoundLibrary _library = library ?? throw new ArgumentNullException(nameof(library));
    private readonly AnimalSoundSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>
    /// Calls with a tick in [<paramref name="fromTick"/>, <paramref name="toTick"/>) for the biome at the
    /// listener. No biome (no map): none.
    /// </summary>
    /// <param name="fromTick">First tick (inclusive).</param>
    /// <param name="toTick">Last tick (exclusive).</param>
    /// <param name="ticksPerHour">Ticks per game hour.</param>
    /// <param name="timeAt">Season and phase at a tick.</param>
    /// <param name="biome">Biome at the listener, or null.</param>
    /// <param name="listener">Listener position on the ground.</param>
    /// <param name="worldSeed">World seed.</param>
    public IReadOnlyList<AnimalCall> Between(long fromTick, long toTick, int ticksPerHour, Func<long, HourTime> timeAt, string? biome, Position listener, ulong worldSeed)
    {
        ArgumentNullException.ThrowIfNull(timeAt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerHour);
        var calls = new List<AnimalCall>();
        if (biome is null || toTick <= fromTick)
        {
            return calls;
        }

        for (var hour = fromTick / ticksPerHour; hour * ticksPerHour < toTick; hour++)
        {
            var time = timeAt(hour * ticksPerHour);
            foreach (var animal in _library.AudibleAnimals(biome, time.Season, time.TimeOfDay))
            {
                // A fraction of per_hour is the chance of one more call in this hour (review #91).
                var whole = (int)Math.Floor(animal.PerHour);
                var extra = new SplitMix64(SplitMix64.SeedOf(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{worldSeed}|{animal.Animal}|{hour}|extra")));
                var count = whole + ((extra.NextSigned() + 1) / 2 < animal.PerHour - whole ? 1 : 0);
                for (var k = 0; k < count; k++)
                {
                    var random = new SplitMix64(SplitMix64.SeedOf(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{worldSeed}|{animal.Animal}|{hour}|{k}")));
                    var tick = (hour * ticksPerHour) + (long)(random.Next() % (ulong)ticksPerHour);
                    if (tick < fromTick || tick >= toTick)
                    {
                        continue;
                    }

                    var angle = ((random.NextSigned() + 1) / 2) * 2 * Math.PI;
                    var ring = _settings.CallRadiusM;
                    var radius = ring.Min + ((Math.Max(ring.Min, ring.Max) - ring.Min) * (random.NextSigned() + 1) / 2);
                    var variants = Math.Max(1, _library.Recipe(animal.Sound).Variants);
                    calls.Add(new AnimalCall(
                        animal.Animal,
                        animal.Sound,
                        (int)(random.Next() % (ulong)variants),
                        new Position(listener.X + (radius * Math.Cos(angle)), listener.Z + (radius * Math.Sin(angle))),
                        tick,
                        random.Next(),
                        animal.Sound == _settings.Birdsong.Sound));
                }
            }
        }

        // Stable: calls of one tick keep the order they were made in (hour, animal, number).
        return [.. calls.OrderBy(c => c.Tick).ThenBy(c => c.Animal, StringComparer.Ordinal)];
    }
}

/// <summary>
/// At most a number of calls per real second (review #91): at fast forward or after a jump the planner
/// yields many calls at once; two calls are at least 1/perSecond real seconds apart, the rest are skipped,
/// so the world stays calm.
/// </summary>
/// <param name="perSecond">Allowed calls per real second (data).</param>
public sealed class CallBudget(double perSecond)
{
    private readonly double _gap = 1 / Math.Max(1e-6, perSecond);
    private double? _last;

    /// <summary>True if a call may start at <paramref name="now"/> (real seconds); then it is counted.</summary>
    public bool TryTake(double now)
    {
        if (_last is { } last && now - last < _gap)
        {
            return false;
        }

        _last = now;
        return true;
    }
}
