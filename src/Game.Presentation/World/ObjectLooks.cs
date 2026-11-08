using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.World;

/// <summary>How the objects of one visual are drawn: shape, palette colours by role, size, lift.</summary>
/// <param name="VisualId">Visual id from the data (the fallback has <see cref="ObjectLooks.FallbackId"/>).</param>
/// <param name="Shape">Shape builder.</param>
/// <param name="Colors">Palette colour ids by role (main, second, third); never empty.</param>
/// <param name="SizeM">Size factor.</param>
/// <param name="SizeJitter">Size variation share either way.</param>
/// <param name="LiftM">Height above the ground.</param>
/// <param name="Rest">Which ground height within the footprint the object stands on (never tilted).</param>
/// <param name="FootprintM">Radius of the footprint at size factor 1.</param>
public sealed record ObjectLook(string VisualId, ObjectLookShape Shape, IReadOnlyList<string> Colors, double SizeM, double SizeJitter, double LiftM, ObjectLookRest Rest = ObjectLookRest.Centre, double FootprintM = 0)
{
    /// <summary>The colour of a role; a shape with more roles than colours repeats them from the start.</summary>
    public string Color(int role) => Colors[role % Colors.Count];
}

/// <summary>Where one object is drawn: on the ground under its core position, turned and sized from its id.</summary>
/// <param name="ObjectId">Core object id.</param>
/// <param name="X">X in metres (core position).</param>
/// <param name="Y">Height in metres (ground plus lift).</param>
/// <param name="Z">Z in metres (core position).</param>
/// <param name="YawRad">Turn about the up axis.</param>
/// <param name="Scale">Size factor with the per-object variation.</param>
public readonly record struct ObjectPlacement(string ObjectId, double X, double Y, double Z, double YawRad, double Scale);

/// <summary>
/// The object look of the main game's island mode (<c>content/look/objects.json</c>) without Godot: which
/// shape and colours each object type gets, the calm neutral fallback marker for a type without an entry
/// (reported once per type), and a placement per object whose turn and size come from the object id, so
/// the same world looks the same at every start and after loading. Presentation only: it reads core
/// objects and never changes them.
/// </summary>
public sealed class ObjectLooks
{
    /// <summary>Visual id of the fallback marker.</summary>
    public const string FallbackId = "fallback";

    private readonly Dictionary<string, ObjectLook> _byType = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <summary>Reads the data; every colour must be a palette id and every type may appear only once.</summary>
    public ObjectLooks(LookObjects data, IReadOnlySet<string> paletteIds)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(paletteIds);
        void CheckColors(string owner, IEnumerable<string> colors)
        {
            foreach (var color in colors)
            {
                if (!paletteIds.Contains(color))
                {
                    throw new InvalidDataException($"objects.json: {owner} uses colour '{color}', which is not in content/look/palette.json.");
                }
            }
        }

        CheckColors("the fallback", data.Fallback.Colors);
        Fallback = new ObjectLook(FallbackId, data.Fallback.Shape, [.. data.Fallback.Colors], data.Fallback.SizeM, 0, 0);
        var visuals = new List<ObjectLook> { Fallback };
        foreach (var visual in data.Visuals)
        {
            if (visual.Id == FallbackId || visuals.Any(v => v.VisualId == visual.Id))
            {
                throw new InvalidDataException($"objects.json: visual id '{visual.Id}' is used twice or reserved.");
            }

            CheckColors($"visual '{visual.Id}'", visual.Colors);
            var look = new ObjectLook(visual.Id, visual.Shape, [.. visual.Colors], visual.SizeM, visual.SizeJitter, visual.LiftM, visual.Rest, visual.FootprintM);
            visuals.Add(look);
            foreach (var type in visual.Types)
            {
                if (!_byType.TryAdd(type, look))
                {
                    throw new InvalidDataException($"objects.json: object type '{type}' has two visuals ('{_byType[type].VisualId}' and '{visual.Id}').");
                }
            }
        }

        Visuals = visuals;
    }

    /// <summary>The fallback marker.</summary>
    public ObjectLook Fallback { get; }

    /// <summary>Every visual, the fallback first.</summary>
    public IReadOnlyList<ObjectLook> Visuals { get; }

    /// <summary>Whether the data has an entry for the type.</summary>
    public bool Knows(string typeId) => _byType.ContainsKey(typeId);

    /// <summary>The look of a type, or the fallback marker.</summary>
    public ObjectLook For(string typeId) => _byType.TryGetValue(typeId, out var look) ? look : Fallback;

    /// <summary>True the first time an unknown type is asked for in this run (log it once), false after.</summary>
    public bool FirstUnknown(string typeId) => !Knows(typeId) && _reported.Add(typeId);

    /// <summary>
    /// Groups the objects by visual (none-shapes left out) and places each on the ground; calls
    /// <paramref name="unknown"/> once per unknown type.
    /// </summary>
    public IReadOnlyDictionary<string, List<ObjectPlacement>> Layout(IEnumerable<WorldObject> objects, Func<double, double, double> ground, Action<string>? unknown = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(ground);
        var layout = new Dictionary<string, List<ObjectPlacement>>(StringComparer.Ordinal);
        foreach (var obj in objects)
        {
            if (FirstUnknown(obj.TypeId))
            {
                unknown?.Invoke(obj.TypeId);
            }

            var look = For(obj.TypeId);
            if (look.Shape == ObjectLookShape.None)
            {
                continue;
            }

            if (!layout.TryGetValue(look.VisualId, out var list))
            {
                layout[look.VisualId] = list = [];
            }

            list.Add(Place(obj, look, ground));
        }

        return layout;
    }

    /// <summary>
    /// One object on the ground, upright (never tilted to the slope): turn and size variation from its id
    /// (deterministic, no RNG state); its height is the ground under its position, or the lowest or highest
    /// ground within its footprint (<see cref="ObjectLook.Rest"/>), plus the lift.
    /// </summary>
    public static ObjectPlacement Place(WorldObject obj, ObjectLook look, Func<double, double, double> ground)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(ground);
        var (x, z) = (obj.Position.X, obj.Position.Z);
        var yaw = Hash01(obj.Id, 1) * 2 * Math.PI;
        var scale = look.SizeM * (1 + (look.SizeJitter * ((Hash01(obj.Id, 2) * 2) - 1)));
        return new ObjectPlacement(obj.Id, x, Rest(look, x, z, scale, ground) + look.LiftM, z, yaw, scale);
    }

    /// <summary>Samples in the footprint: the centre and two rings of eight points.</summary>
    private static double Rest(ObjectLook look, double x, double z, double scale, Func<double, double, double> ground)
    {
        var height = ground(x, z);
        var radius = look.FootprintM * scale;
        if (look.Rest == ObjectLookRest.Centre || radius <= 0)
        {
            return height;
        }

        foreach (var share in (ReadOnlySpan<double>)[0.5, 1.0])
        {
            for (var k = 0; k < 8; k++)
            {
                var a = k * Math.PI / 4;
                var h = ground(x + (Math.Cos(a) * radius * share), z + (Math.Sin(a) * radius * share));
                height = look.Rest == ObjectLookRest.Highest ? Math.Max(height, h) : Math.Min(height, h);
            }
        }

        return height;
    }

    /// <summary>
    /// A fingerprint of the drawn object set (ids, types, positions): the view rebuilds only when it
    /// changes (an object eaten, a gift placed, something moved).
    /// </summary>
    public static long Signature(IEnumerable<WorldObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        unchecked
        {
            long sum = 17;
            long count = 0;
            foreach (var obj in objects)
            {
                // Order-independent: a sum of per-object hashes.
                var h = (long)Fnv(obj.Id, 0) * 31;
                h ^= (long)Fnv(obj.TypeId, 7);
                h = (h * 1_000_003) ^ BitConverter.DoubleToInt64Bits(obj.Position.X);
                h = (h * 1_000_003) ^ BitConverter.DoubleToInt64Bits(obj.Position.Z);
                sum += h;
                count++;
            }

            return (sum * 31) + count;
        }
    }

    /// <summary>Deterministic value in [0, 1) from a text and a salt (FNV-1a, stable across runs and platforms).</summary>
    public static double Hash01(string text, int salt) => Fnv(text, salt) / 4294967296.0;

    private static uint Fnv(string text, int salt)
    {
        ArgumentNullException.ThrowIfNull(text);
        unchecked
        {
            var hash = 2166136261u ^ (uint)salt;
            foreach (var c in text)
            {
                hash = (hash ^ c) * 16777619u;
            }

            // Final avalanche so neighbouring ids spread over the whole range.
            hash ^= hash >> 15;
            hash *= 0x2c1b3c6du;
            hash ^= hash >> 12;
            return hash;
        }
    }
}
