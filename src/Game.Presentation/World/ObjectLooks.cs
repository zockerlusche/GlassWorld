using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.World;

/// <summary>How the objects of one visual are drawn: shape, palette colours by role, size, lift, how they meet the ground.</summary>
/// <param name="VisualId">Visual id from the data (the fallback has <see cref="ObjectLooks.FallbackId"/>).</param>
/// <param name="Shape">Shape builder.</param>
/// <param name="Colors">Palette colour ids by role (main, second, third); never empty.</param>
/// <param name="SizeM">Size factor.</param>
/// <param name="SizeJitter">Size variation share either way.</param>
/// <param name="LiftM">Height above the ground.</param>
/// <param name="Rest">Which ground height within the footprint an upright object stands on.</param>
/// <param name="FootprintM">Radius of the footprint at size factor 1 (half the length along local x for a long object).</param>
/// <param name="Align">Upright, or lying on the ground following its slope (D576).</param>
/// <param name="FootprintWidthM">Half width of an elongated footprint across local x at size factor 1 (null = round).</param>
public sealed record ObjectLook(
    string VisualId,
    ObjectLookShape Shape,
    IReadOnlyList<string> Colors,
    double SizeM,
    double SizeJitter,
    double LiftM,
    ObjectLookRest Rest = ObjectLookRest.Centre,
    double FootprintM = 0,
    ObjectLookAlign Align = ObjectLookAlign.Upright,
    double? FootprintWidthM = null)
{
    /// <summary>The colour of a role; a shape with more roles than colours repeats them from the start.</summary>
    public string Color(int role) => Colors[role % Colors.Count];

    /// <summary>Whether the object lies on the ground and follows its slope.</summary>
    public bool Lies => Align != ObjectLookAlign.Upright;

    /// <summary>Half width of the footprint across local x at size factor 1.</summary>
    public double HalfWidthM => FootprintWidthM ?? FootprintM;
}

/// <summary>How lying objects meet the ground (<c>content/look/objects.json</c>, D576).</summary>
/// <param name="ToleranceM">Largest float or sink of a lying object's underside; a worse fit stands upright instead.</param>
/// <param name="TurnTries">Turns a long lying object tries over half a circle when its own turn fits badly.</param>
/// <param name="MinFootprintM">Smallest footprint radius a lying object samples the ground over.</param>
public sealed record GroundFit(double ToleranceM, int TurnTries, double MinFootprintM)
{
    /// <summary>Defaults for callers without look data (tests): the data's placeholder values.</summary>
    public static GroundFit Default { get; } = new(0.06, 8, 0.05);
}

/// <summary>Where one object is drawn: on the ground under its core position, turned and sized from its id.</summary>
/// <param name="ObjectId">Core object id.</param>
/// <param name="X">X in metres (core position).</param>
/// <param name="Y">Height in metres of its origin (ground plus lift).</param>
/// <param name="Z">Z in metres (core position).</param>
/// <param name="YawRad">Turn about the up axis (local x towards (cos, 0, -sin), as Godot's <c>Basis(Vector3.Up, yaw)</c>).</param>
/// <param name="Scale">Size factor with the per-object variation.</param>
/// <param name="Up">The object's up axis: straight up for upright objects, the ground normal for lying ones (D576).</param>
public readonly record struct ObjectPlacement(string ObjectId, double X, double Y, double Z, double YawRad, double Scale, Point3 Up)
{
    /// <summary>
    /// The object's local axes in world space, scaled (the columns of its basis): turned by the yaw about
    /// the vertical, then tilted by the shortest turn that brings the vertical onto <see cref="Up"/>.
    /// </summary>
    public (Point3 X, Point3 Y, Point3 Z) Axes()
    {
        var (c, s, k) = (Math.Cos(YawRad), Math.Sin(YawRad), Scale);
        var (x, y, z) = (Tilt(new Point3(c, 0, -s)), Tilt(new Point3(0, 1, 0)), Tilt(new Point3(s, 0, c)));
        return (new Point3(x.X * k, x.Y * k, x.Z * k), new Point3(y.X * k, y.Y * k, y.Z * k), new Point3(z.X * k, z.Y * k, z.Z * k));
    }

    /// <summary>A point of the object's shape (local metres at size factor 1) in world space.</summary>
    public Point3 ToWorld(Point3 local)
    {
        var (ax, ay, az) = Axes();
        return new Point3(
            X + (ax.X * local.X) + (ay.X * local.Y) + (az.X * local.Z),
            Y + (ax.Y * local.X) + (ay.Y * local.Y) + (az.Y * local.Z),
            Z + (ax.Z * local.X) + (ay.Z * local.Y) + (az.Z * local.Z));
    }

    /// <summary>Rodrigues turn of <paramref name="v"/> about (up x n) by the angle between up and the normal n.</summary>
    private Point3 Tilt(Point3 v)
    {
        var length = Math.Sqrt((Up.X * Up.X) + (Up.Y * Up.Y) + (Up.Z * Up.Z));
        if (length < 1e-12)
        {
            return v;
        }

        var (nx, ny, nz) = (Up.X / length, Up.Y / length, Up.Z / length);
        var (kx, kz) = (nz, -nx);
        var sin2 = (kx * kx) + (kz * kz);
        if (sin2 < 1e-18)
        {
            return ny >= 0 ? v : new Point3(v.X, -v.Y, -v.Z);
        }

        // R v = v cos + k x v + k (k . v) (1 - cos) / |k|^2 with k = (kx, 0, kz), |k| = sin.
        var (cos, dot) = (ny, (kx * v.X) + (kz * v.Z));
        var f = dot * (1 - cos) / sin2;
        return new Point3(
            (v.X * cos) + (-kz * v.Y) + (kx * f),
            (v.Y * cos) + ((kz * v.X) - (kx * v.Z)),
            (v.Z * cos) + (kx * v.Y) + (kz * f));
    }
}

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

    // Placements by object id, type and position: the ground never changes at run time, so an object is
    // placed once and a refresh (an object eaten, one regrown) only places the new ones (GW3 #120).
    private Dictionary<(string Id, string Type, double X, double Z), ObjectPlacement> _placed = [];
    private Func<double, double, double>? _placedOn;

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
        Fit = new GroundFit(data.GroundToleranceM, data.TurnTries, data.MinFootprintM);
        Fallback = new ObjectLook(FallbackId, data.Fallback.Shape, [.. data.Fallback.Colors], data.Fallback.SizeM, 0, 0);
        var visuals = new List<ObjectLook> { Fallback };
        foreach (var visual in data.Visuals)
        {
            if (visual.Id == FallbackId || visuals.Any(v => v.VisualId == visual.Id))
            {
                throw new InvalidDataException($"objects.json: visual id '{visual.Id}' is used twice or reserved.");
            }

            CheckColors($"visual '{visual.Id}'", visual.Colors);
            var look = new ObjectLook(visual.Id, visual.Shape, [.. visual.Colors], visual.SizeM, visual.SizeJitter, visual.LiftM, visual.Rest, visual.FootprintM, visual.Align, visual.FootprintWidthM);
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

    /// <summary>How far a lying object's underside may float above or sink below the drawn ground (metres).</summary>
    public double GroundToleranceM => Fit.ToleranceM;

    /// <summary>How lying objects meet the ground, from the data.</summary>
    public GroundFit Fit { get; }

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
    /// <paramref name="unknown"/> once per unknown type. Placements are kept for the objects still there
    /// (by id, type and position), so only new or moved objects are placed again; another ground function
    /// starts afresh.
    /// </summary>
    public IReadOnlyDictionary<string, List<ObjectPlacement>> Layout(IEnumerable<WorldObject> objects, Func<double, double, double> ground, Action<string>? unknown = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(ground);
        var layout = new Dictionary<string, List<ObjectPlacement>>(StringComparer.Ordinal);
        var known = ReferenceEquals(ground, _placedOn) ? _placed : [];
        var placed = new Dictionary<(string Id, string Type, double X, double Z), ObjectPlacement>(known.Count);
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

            var key = (obj.Id, obj.TypeId, obj.Position.X, obj.Position.Z);
            if (!known.TryGetValue(key, out var placement))
            {
                placement = Place(obj, look, ground, Fit);
            }

            placed[key] = placement;
            list.Add(placement);
        }

        (_placed, _placedOn) = (placed, ground);
        return layout;
    }

    /// <summary>
    /// One object on the ground, turn and size variation from its id (deterministic, no RNG state). An
    /// upright object stands straight up on the ground under its position, or the lowest or highest ground
    /// within its footprint (<see cref="ObjectLook.Rest"/>). A lying object (D576) takes the plane that
    /// fits the drawn ground under its footprint best: its up axis is that plane's normal and its height
    /// the plane's, so its underside neither floats nor sinks more than the ground's own bumps; a long one
    /// tries other turns if its own fits badly and keeps the one that fits best; an uphill one turns its
    /// local +x side up the slope. A lying object whose best fit is still beyond the tolerance (across a
    /// rock wall) stands upright on the ground under its position instead. The lift comes on top.
    /// </summary>
    public static ObjectPlacement Place(WorldObject obj, ObjectLook look, Func<double, double, double> ground, GroundFit? fit = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(ground);
        fit ??= GroundFit.Default;
        var (x, z) = (obj.Position.X, obj.Position.Z);
        var yaw = Hash01(obj.Id, 1) * 2 * Math.PI;
        var scale = look.SizeM * (1 + (look.SizeJitter * ((Hash01(obj.Id, 2) * 2) - 1)));
        if (!look.Lies)
        {
            return new ObjectPlacement(obj.Id, x, Rest(look, x, z, scale, ground) + look.LiftM, z, yaw, scale, new Point3(0, 1, 0));
        }

        var (length, width) = (Math.Max(look.FootprintM * scale, fit.MinFootprintM), Math.Max(look.HalfWidthM * scale, fit.MinFootprintM));
        PlaneFit best;
        if (look.Align == ObjectLookAlign.Ground_uphill)
        {
            // Up the slope of the round fit; on flat ground the turn from the id.
            var probe = FitPlane(x, z, yaw, length, length, ground);
            var turn = Math.Abs(probe.Gx) + Math.Abs(probe.Gz) > 1e-9 ? Math.Atan2(-probe.Gz, probe.Gx) : yaw;
            best = FitPlane(x, z, turn, length, width, ground);
        }
        else
        {
            best = FitPlane(x, z, yaw, length, width, ground);
            for (var k = 1; k < fit.TurnTries && Math.Abs(length - width) > 1e-9 && best.Error > fit.ToleranceM / 2; k++)
            {
                var other = FitPlane(x, z, yaw + (k * Math.PI / fit.TurnTries), length, width, ground);
                if (other.Error < best.Error - 1e-9)
                {
                    best = other;
                }
            }
        }

        if (best.Error > fit.ToleranceM)
        {
            return new ObjectPlacement(obj.Id, x, ground(x, z) + look.LiftM, z, yaw, scale, new Point3(0, 1, 0));
        }

        var up = new Point3(-best.Gx, 1, -best.Gz);
        var norm = Math.Sqrt((up.X * up.X) + 1 + (up.Z * up.Z));
        var lying = new ObjectPlacement(obj.Id, x, best.Height + look.LiftM, z, best.Yaw, scale, new Point3(up.X / norm, 1 / norm, up.Z / norm));

        // The fit works on the ground under the footprint, the tilted object turns about its centre: where that
        // lifts a rim point beyond the tolerance after all (steep, uneven ground), it stands upright instead.
        return RestsWithin(lying, look, ground, fit.ToleranceM) ? lying : new ObjectPlacement(obj.Id, x, ground(x, z) + look.LiftM, z, yaw, scale, new Point3(0, 1, 0));
    }

    /// <summary>Whether every rim point of the tilted footprint (16 directions, centre, half and full) lies within the tolerance of the ground.</summary>
    private static bool RestsWithin(ObjectPlacement placed, ObjectLook look, Func<double, double, double> ground, double tolerance)
    {
        for (var k = 0; k < 16; k++)
        {
            var a = k * Math.PI / 8;
            foreach (var share in (ReadOnlySpan<double>)[0.5, 1.0])
            {
                var p = placed.ToWorld(new Point3(Math.Cos(a) * look.FootprintM * share, 0, Math.Sin(a) * look.HalfWidthM * share));
                if (Math.Abs(p.Y - look.LiftM - ground(p.X, p.Z)) > tolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The plane that fits the ground under an elliptic footprint (half axes <paramref name="length"/> along
    /// the turned local x, <paramref name="width"/> across) best: least squares over its centre and two rings,
    /// then shifted so the largest float equals the largest sink (<see cref="PlaneFit.Error"/>).
    /// </summary>
    private static PlaneFit FitPlane(double x, double z, double yaw, double length, double width, Func<double, double, double> ground)
    {
        var (c, s) = (Math.Cos(yaw), Math.Sin(yaw));
        Span<double> dx = stackalloc double[17], dz = stackalloc double[17], h = stackalloc double[17];
        var n = 0;
        foreach (var share in (ReadOnlySpan<double>)[0.0, 0.5, 1.0])
        {
            for (var k = 0; k < (share == 0 ? 1 : 8); k++)
            {
                var a = k * Math.PI / 4;
                var (lx, lz) = (Math.Cos(a) * length * share, Math.Sin(a) * width * share);
                (dx[n], dz[n]) = ((lx * c) + (lz * s), (-lx * s) + (lz * c));
                h[n] = ground(x + dx[n], z + dz[n]);
                n++;
            }
        }

        // Normal equations of h = c0 + gx dx + gz dz.
        double sx = 0, sz = 0, sxx = 0, szz = 0, sxz = 0, sh = 0, sxh = 0, szh = 0;
        for (var i = 0; i < n; i++)
        {
            (sx, sz, sxx, szz, sxz) = (sx + dx[i], sz + dz[i], sxx + (dx[i] * dx[i]), szz + (dz[i] * dz[i]), sxz + (dx[i] * dz[i]));
            (sh, sxh, szh) = (sh + h[i], sxh + (dx[i] * h[i]), szh + (dz[i] * h[i]));
        }

        static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
            (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
        var det = Det(n, sx, sz, sx, sxx, sxz, sz, sxz, szz);
        double c0 = sh / n, gx = 0, gz = 0;
        if (Math.Abs(det) > 1e-12)
        {
            c0 = Det(sh, sx, sz, sxh, sxx, sxz, szh, sxz, szz) / det;
            gx = Det(n, sh, sz, sx, sxh, sxz, sz, szh, szz) / det;
            gz = Det(n, sx, sh, sx, sxx, sxh, sz, sxz, szh) / det;
        }

        double above = double.MinValue, below = double.MaxValue;
        for (var i = 0; i < n; i++)
        {
            var e = h[i] - (c0 + (gx * dx[i]) + (gz * dz[i]));
            (above, below) = (Math.Max(above, e), Math.Min(below, e));
        }

        return new PlaneFit(c0 + ((above + below) / 2), gx, gz, (above - below) / 2, yaw);
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

    /// <summary>A plane under a footprint: height at the centre, slope along x and z, the largest float or sink, and the turn it was fitted for.</summary>
    private readonly record struct PlaneFit(double Height, double Gx, double Gz, double Error, double Yaw);

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
