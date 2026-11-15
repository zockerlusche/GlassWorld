using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>One ambient animal now (A1383): where, which way it faces, how far its wings are open, how big (0 = not shown).</summary>
/// <param name="Animal">Animal id (sound map).</param>
/// <param name="X">Position x, metres.</param>
/// <param name="Y">Height, metres.</param>
/// <param name="Z">Position z, metres.</param>
/// <param name="Yaw">Heading in radians (0 = +z).</param>
/// <param name="Wing">0..1: how far the wings are spread (1 = open); 1 without wing beats.</param>
/// <param name="Scale">Its size factor now: fades in and out at the start and end of its stay (0..1), and a glow's brightness.</param>
public readonly record struct AmbientCritter(string Animal, double X, double Y, double Z, double Yaw, double Wing, double Scale);

/// <summary>
/// The ambient animals near the camera (A1383, B5-6 R16, B2608), without Godot: presentation only - no core state, nothing
/// saved or hashed. Which animals live here comes from the sound map (biome, season, time of day): what is heard has a
/// visible cause. Each lives in a cell of <c>cell_m</c> around the camera's target, at a home drawn from the world seed,
/// the cell and its stay; it moves around it by its motion and fades out after <c>stay_s</c> to come back elsewhere.
/// A pure function of the inputs, so the same seconds give the same picture.
/// </summary>
public static class AmbientFauna
{
    private const double Fade = 0.15;

    /// <summary>The animals shown now.</summary>
    /// <param name="data">Look and motion (content/fauna/ambient.json).</param>
    /// <param name="living">The sound map's animals that live here now (season and time of day already chosen).</param>
    /// <param name="targetX">Camera target x.</param>
    /// <param name="targetZ">Camera target z.</param>
    /// <param name="seconds">Real seconds the world has run (stops while paused).</param>
    /// <param name="seed">World seed.</param>
    /// <param name="biomeAt">Biome id at a ground point, or null off the map.</param>
    /// <param name="ground">Height they live above: the ground, or the water's surface where there is water (review GW4 #504).</param>
    public static IReadOnlyList<AmbientCritter> At(FaunaAmbient data, IReadOnlyList<SoundForAnimal> living, double targetX, double targetZ, double seconds, ulong seed, Func<double, double, string?> biomeAt, Func<double, double, double> ground)
    {
        var shown = new List<AmbientCritter>();
        Fill(shown, data, living, targetX, targetZ, seconds, seed, biomeAt, ground);
        return shown;
    }

    /// <summary>
    /// The same into a list the caller keeps (cleared first): every frame without new objects - no strings, no LINQ
    /// (review GW4 #504, A1767: the frame budget).
    /// </summary>
    public static void Fill(List<AmbientCritter> shown, FaunaAmbient data, IReadOnlyList<SoundForAnimal> living, double targetX, double targetZ, double seconds, ulong seed, Func<double, double, string?> biomeAt, Func<double, double, double> ground)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(living);
        ArgumentNullException.ThrowIfNull(biomeAt);
        ArgumentNullException.ThrowIfNull(ground);
        shown.Clear();
        var cell = data.CellM;
        var (cx, cz) = ((long)Math.Floor(targetX / cell), (long)Math.Floor(targetZ / cell));
        var reach = (int)Math.Ceiling(data.RadiusM / cell);
        var index = -1;
        foreach (var look in data.Animals)
        {
            index++;
            if (Living(living, look.Animal) is not { } lives)
            {
                continue;
            }

            for (var ix = cx - reach; ix <= cx + reach; ix++)
            {
                for (var iz = cz - reach; iz <= cz + reach; iz++)
                {
                    for (var slot = 0; slot < look.PerCell; slot++)
                    {
                        if (One(look, index, lives, ix, iz, slot, cell, seconds, seed, biomeAt, ground) is not { } critter)
                        {
                            continue;
                        }

                        var distance = Math.Sqrt(((critter.X - targetX) * (critter.X - targetX)) + ((critter.Z - targetZ) * (critter.Z - targetZ)));
                        var edge = Math.Clamp((data.RadiusM - distance) / (data.RadiusM * Fade), 0, 1);
                        if (edge > 0)
                        {
                            shown.Add(critter with { Scale = critter.Scale * edge });
                        }
                    }
                }
            }
        }

    }

    private static SoundForAnimal? Living(IReadOnlyList<SoundForAnimal> living, string animal)
    {
        for (var i = 0; i < living.Count; i++)
        {
            if (string.Equals(living[i].Animal, animal, StringComparison.Ordinal))
            {
                return living[i];
            }
        }

        return null;
    }

    private static AmbientCritter? One(AmbientAnimal look, int index, SoundForAnimal lives, long ix, long iz, int slot, double cell, double seconds, ulong seed, Func<double, double, string?> biomeAt, Func<double, double, double> ground)
    {
        var stay = look.StayS;
        var shifted = seconds + (Unit(seed, index, ix, iz, slot, 0) * stay);
        var round = Math.Floor(shifted / stay);
        var t = shifted - (round * stay);
        var homeX = (ix + Unit(seed, index, ix, iz, slot, (long)(round * 4) + 1)) * cell;
        var homeZ = (iz + Unit(seed, index, ix, iz, slot, (long)(round * 4) + 2)) * cell;
        if (biomeAt(homeX, homeZ) is not { } biome || !lives.Biomes.Contains(biome))
        {
            return null;
        }

        // In and out calmly at both ends of its stay.
        var fade = Math.Min(1, Math.Min(t, stay - t) / (stay * Fade));
        var motion = look.Motion;
        var phase = Unit(seed, index, ix, iz, slot, (long)(round * 4) + 3) * 2 * Math.PI;
        var turn = (2 * Math.PI * motion.Speed * t) + phase;
        var wing = motion.FlapHz is > 0 and var hz ? 0.5 + (0.5 * Math.Cos(2 * Math.PI * hz * t)) : 1;
        double x, z, lift, yaw;
        switch (motion.Kind)
        {
            case AmbientMotionKind.Glide:
                (x, z) = (homeX + (motion.WanderM * Math.Cos(turn)), homeZ + (motion.WanderM * Math.Sin(turn)));
                lift = motion.HeightM;
                yaw = Math.Atan2(-Math.Sin(turn), Math.Cos(turn));
                break;
            case AmbientMotionKind.Flutter:
                (x, z) = (homeX + (motion.WanderM * Math.Sin(turn) * Math.Cos(turn * 0.37)), homeZ + (motion.WanderM * Math.Sin(turn * 0.71)));
                lift = motion.HeightM + (0.15 * Math.Sin(turn * 3.1));
                yaw = turn;
                break;
            case AmbientMotionKind.Hover:
                (x, z) = (homeX + (motion.WanderM * Math.Sin(turn) * Math.Sin(turn * 0.53)), homeZ + (motion.WanderM * Math.Cos(turn * 0.83)));
                lift = motion.HeightM + (0.08 * Math.Sin(turn * 2.3));
                yaw = turn * 0.5;
                break;
            case AmbientMotionKind.Blink:
                (x, z) = (homeX + (motion.WanderM * Math.Sin(turn * 0.6)), homeZ + (motion.WanderM * Math.Cos(turn * 0.4)));
                lift = motion.HeightM + (0.2 * Math.Sin(turn));
                yaw = 0;
                fade *= 0.5 + (0.5 * Math.Max(0, Math.Sin(turn * 1.7)));
                break;
            case AmbientMotionKind.Jump:
                {
                    // Now and then a leap: a short arc, hidden in between (under the water).
                    var leap = (motion.Speed * t) - Math.Floor(motion.Speed * t);
                    const double Share = 0.12;
                    if (leap > Share)
                    {
                        return null;
                    }

                    var u = leap / Share;
                    (x, z) = (homeX + (motion.WanderM * Math.Cos(phase) * (u - 0.5)), homeZ + (motion.WanderM * Math.Sin(phase) * (u - 0.5)));
                    lift = motion.HeightM * Math.Sin(Math.PI * u);
                    yaw = phase;
                    break;
                }

            default:
                {
                    // Hop: back and forth on a line through its home, a small hop at each step.
                    var along = Math.Sin(turn * 0.25);
                    (x, z) = (homeX + (motion.WanderM * along * Math.Cos(phase)), homeZ + (motion.WanderM * along * Math.Sin(phase)));
                    var step = (motion.Speed * t) - Math.Floor(motion.Speed * t);
                    lift = motion.HeightM * Math.Max(0, Math.Sin(Math.PI * step * 2));
                    yaw = phase + (Math.Cos(turn * 0.25) >= 0 ? 0 : Math.PI);
                    break;
                }
        }

        return new AmbientCritter(look.Animal, x, ground(x, z) + lift, z, yaw, wing, fade);
    }

    /// <summary>A number in [0, 1) from the seed and the place of one animal - integers mixed (SplitMix64), no strings.</summary>
    private static double Unit(ulong seed, int animal, long ix, long iz, int slot, long n)
    {
        var x = seed;
        x = Mix(x ^ (ulong)animal);
        x = Mix(x ^ (ulong)ix);
        x = Mix(x ^ (ulong)iz);
        x = Mix(x ^ (ulong)slot);
        x = Mix(x ^ (ulong)n);
        return (x >> 11) * (1.0 / (1UL << 53));
    }

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
