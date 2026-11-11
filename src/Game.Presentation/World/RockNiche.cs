using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// One flat triangle of a shape that is built without Godot: its corners counter-clockwise seen from the
/// side its <paramref name="Normal"/> looks to, the colour role of the look it is painted with, and per
/// corner the direction the ink outline pushes it out along (<see cref="OutlineHull"/>).
/// </summary>
/// <param name="A">First corner (local metres at size factor 1).</param>
/// <param name="B">Second corner.</param>
/// <param name="C">Third corner.</param>
/// <param name="Normal">Unit normal of the face.</param>
/// <param name="Role">Colour role of the look (<see cref="ObjectLook.Color"/>).</param>
/// <param name="HullA">Outline direction of the first corner.</param>
/// <param name="HullB">Outline direction of the second corner.</param>
/// <param name="HullC">Outline direction of the third corner.</param>
public readonly record struct ShapeTriangle(Point3 A, Point3 B, Point3 C, Point3 Normal, int Role, Point3 HullA, Point3 HullB, Point3 HullC);

/// <summary>
/// The measures of a rock niche (<c>niche</c> of a visual in <c>content/look/objects.json</c>, TL #20750),
/// in metres at size factor 1.
/// </summary>
/// <param name="OpeningWidthM">Width of the opening at the ground.</param>
/// <param name="OpeningHeightM">Height of the opening at its highest point.</param>
/// <param name="FrontM">How far the foot of the opening lies in front of the object's position.</param>
/// <param name="DepthM">How far the dark hollow reaches into the rock behind the opening.</param>
/// <param name="SideRockM">Thickness of the rock at either side of the opening.</param>
/// <param name="LintelM">Thickness of the rock above the opening.</param>
/// <param name="OverhangM">How far the lintel juts out over the foot of the opening.</param>
/// <param name="BackM">How far the rock reaches back from the object's position into the wall.</param>
/// <param name="SinkM">How far the rock reaches below the object's position.</param>
/// <param name="Roughness">How uneven the rock is (share of its thickness its corners move by).</param>
public sealed record RockNicheShape(
    double OpeningWidthM,
    double OpeningHeightM,
    double FrontM,
    double DepthM,
    double SideRockM,
    double LintelM,
    double OverhangM,
    double BackM,
    double SinkM,
    double Roughness)
{
    /// <summary>The measures of the data.</summary>
    public static RockNicheShape From(ObjectNiche data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new RockNicheShape(data.OpeningWidthM, data.OpeningHeightM, data.FrontM, data.DepthM, data.SideRockM, data.LintelM, data.OverhangM, data.BackM, data.SinkM, data.Roughness);
    }

    /// <summary>Local x of the foot of the opening: in front of the object's position, which stands at the foot of the wall where the ground already rises.</summary>
    public double LipX => -FrontM;

    /// <summary>Local x of the back of the dark hollow.</summary>
    public double HollowBackX => DepthM - FrontM;

    /// <summary>How far behind the lip the rock ends and the dark begins: the depth of the jambs and of the underside of the lintel one sees.</summary>
    public double RevealM => 0.2 * DepthM;

    /// <summary>Height of the rock above the object's position.</summary>
    public double HeightM => OpeningHeightM + LintelM;
}

/// <summary>
/// The shape of a rock niche (object type <c>rock_niche</c>, TL #20750): a shelter in a rock wall that a
/// player reads as one in the game's low-poly comic look, built without Godot. In local metres, y up: the
/// object's position is the origin, the wall it leans on lies at +x and the open ground at -x
/// (<see cref="ObjectLooks.WallYaw"/> turns it so). Parts:
/// <list type="bullet">
/// <item>the body (colour role 0): a faceted hump of rock from the opening back into the wall and down
/// below the ground (<see cref="RockNicheShape.SinkM"/>), so it sits flush on uneven ground; its front
/// leans out over the opening, and the opening is cut into it with jambs and an underside one can see;</item>
/// <item>the hollow (<see cref="InsideRole"/>): walls and ceiling behind the opening and a floor that climbs
/// from a low step to the top of the back, all facing inwards. The game draws it unlit in its own dark
/// colour, so the opening stays dark in every light and from every side, and it hides the wall behind;</item>
/// <item>the lintel and two side rocks (role 1): a block across the top that juts out over the opening,
/// and a rock at either jamb, each a closed stone of its own; a small stone lies beside the left one.</item>
/// </list>
/// Every part is flat-shaded and carries outline directions that keep the ink hull closed
/// (<see cref="OutlineHull"/>); the hollow has no outline. Deterministic: the same measures give the same
/// triangles.
/// </summary>
public static class RockNiche
{
    /// <summary>Colour role of the rock body.</summary>
    public const int BodyRole = 0;

    /// <summary>Colour role of the lintel and the side rocks.</summary>
    public const int StoneRole = 1;

    /// <summary>Colour role of the dark hollow, which is drawn unlit and without outline.</summary>
    public const int InsideRole = 2;

    // The outline of the rock seen from the front, from the left foot over the top to the right foot:
    // u across (-1..1 of half the width), v up (0..1 of the height). Uneven on purpose.
    private static readonly (double U, double V)[] Outer =
    [
        (-1.0, 0.0), (-1.07, 0.52), (-0.76, 0.96), (-0.12, 1.0), (0.6, 0.88), (1.05, 0.5), (1.0, 0.0),
    ];

    // The outline of the opening, point for point under the outline of the rock.
    private static readonly (double U, double V)[] Opening =
    [
        (-1.0, 0.0), (-1.0, 0.56), (-0.64, 0.94), (-0.06, 1.0), (0.54, 0.9), (1.0, 0.5), (1.0, 0.0),
    ];

    /// <summary>Builds the niche.</summary>
    public static IReadOnlyList<ShapeTriangle> Build(RockNicheShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var all = new List<ShapeTriangle>();
        var (w, h, f, t) = (shape.OpeningWidthM, shape.OpeningHeightM, shape.SideRockM, shape.LintelM);
        var (lip, top, rough) = (shape.LipX, shape.HeightM, shape.Roughness * shape.SideRockM);
        var length = shape.BackM - lip;

        // How far the front leans out at a height: nothing at the foot, half the overhang at the top (the
        // lintel brings the rest).
        double Lean(double y) => 0.5 * shape.OverhangM * Math.Pow(Math.Clamp(y / top, 0, 1), 1.5);

        Point3[] OuterRing(double x, double across, double up, bool leaning, int salt, double jitter)
        {
            var ring = new Point3[Outer.Length];
            for (var k = 0; k < Outer.Length; k++)
            {
                var (u, v) = Outer[k];
                var foot = v <= 0;
                var y = foot ? -shape.SinkM : (v * top * up) + (Jitter(salt, k, 1) * jitter);
                var z = (u * ((w / 2) + f) * across) + (Jitter(salt, k, 2) * jitter);
                ring[k] = new Point3(x - (leaning ? Lean(y) : 0) + (Jitter(salt, k, 0) * jitter * 0.6), y, z);
            }

            return ring;
        }

        Point3[] OpeningRing(double x, double scale, bool leaning)
        {
            var ring = new Point3[Opening.Length];
            for (var k = 0; k < Opening.Length; k++)
            {
                var (u, v) = Opening[k];
                var y = v <= 0 ? -shape.SinkM : v * h * scale;
                ring[k] = new Point3(x - (leaning ? Lean(y) : 0), y, u * (w / 2) * scale);
            }

            return ring;
        }

        // The body: front face, jambs and underside of the opening, then the hump back into the wall.
        var body = new Piece(BodyRole);
        var heart = new Point3((lip + shape.BackM) / 2, 0.3 * top, 0);
        Point3 Outward(Point3 c) => new(c.X - heart.X, c.Y - heart.Y, c.Z - heart.Z);
        Point3 ToAxis(Point3 c) => new(0, (0.45 * h) - c.Y, -c.Z);
        var front = OuterRing(lip, 1, 1, true, 11, rough * 0.7);
        var edge = OpeningRing(lip, 1, true);
        var inner = OpeningRing(lip + shape.RevealM, 1, false);
        Point3[][] rings =
        [
            front,
            OuterRing(lip + (0.3 * length), 1.12, 1.04, false, 12, rough),
            OuterRing(lip + (0.7 * length), 0.98, 0.78, false, 13, rough),
            OuterRing(shape.BackM, 0.62, 0.42, false, 14, rough * 0.6),
        ];
        for (var k = 0; k + 1 < Outer.Length; k++)
        {
            body.Quad(front[k], front[k + 1], edge[k + 1], edge[k], _ => new Point3(-1, 0, 0));
            body.Quad(edge[k], edge[k + 1], inner[k + 1], inner[k], ToAxis);
            for (var r = 0; r + 1 < rings.Length; r++)
            {
                body.Quad(rings[r][k], rings[r][k + 1], rings[r + 1][k + 1], rings[r + 1][k], Outward);
            }

            body.Add(rings[^1][k], rings[^1][k + 1], new Point3(shape.BackM + (0.15 * f), 0.2 * top, 0), new Point3(1, 0.2, 0));
        }

        body.Into(all, hull: true);

        // The hollow: walls and ceiling from where the rock of the opening ends to its back, and a floor
        // that closes it. The object stands at the foot of a wall, where the ground already rises under it:
        // so the floor starts as a low step and climbs to the top of the back, steeper than any ground the
        // generator leaves at the foot of a rock face - the ground never shows through the dark, and the
        // dark is seen from above as well as from the front.
        var hollow = new Piece(InsideRole);
        var back = OpeningRing(shape.HollowBackX, HollowTaper, false);
        var step = FloorStepShare * h;
        var ceiling = back.Max(p => p.Y);
        for (var k = 0; k + 1 < Opening.Length; k++)
        {
            hollow.Quad(inner[k], inner[k + 1], back[k + 1], back[k], ToAxis);
        }

        hollow.Quad(inner[0], inner[^1], inner[^1] with { Y = step }, inner[0] with { Y = step }, _ => new Point3(-1, 0, 0));
        hollow.Quad(inner[0] with { Y = step }, inner[^1] with { Y = step }, back[^1] with { Y = ceiling }, back[0] with { Y = ceiling }, _ => new Point3(-0.3, 1, 0));
        hollow.Into(all, hull: false);

        // The lintel: a block across the top, its underside at the top of the opening, jutting out by the
        // overhang and lying a little askew.
        var slab = new Point3(shape.OverhangM + shape.RevealM + (0.45 * f), t, w + (1.25 * f));
        Block(all, 21, new Point3(lip - shape.OverhangM + (slab.X / 2), h + (0.45 * slab.Y), 0.03 * w), slab, 0.07, rough);

        // A rock at either jamb, the left one taller, and a small stone beside the left one. None of them
        // stands further out than the lintel, so the ground before the opening stays free to walk on.
        Block(all, 22, new Point3(lip + (0.28 * f), 0.34 * h, -((w / 2) + (0.62 * f))), new Point3(1.05 * f, 0.86 * h, 1.15 * f), 0.4, rough);
        Block(all, 23, new Point3(lip + (0.36 * f), 0.22 * h, (w / 2) + (0.7 * f)), new Point3(1.15 * f, 0.58 * h, 1.3 * f), -0.3, rough);
        Block(all, 24, new Point3(lip + (0.3 * f), 0.05 * h, -((w / 2) + (1.6 * f))), new Point3(0.55 * f, 0.18 * h, 0.5 * f), 1.1, rough * 0.4);
        return all;
    }

    /// <summary>How much smaller the outline of the hollow is at its back than at the opening.</summary>
    public const double HollowTaper = 0.82;

    /// <summary>Height of the step the floor of the hollow starts with, as a share of the opening's height.</summary>
    public const double FloorStepShare = 0.3;

    /// <summary>
    /// A flat-shaded block of rock: three uneven four-cornered rows (a foot, a wider belly, a smaller top),
    /// every corner moved by up to <paramref name="jitter"/> metres, turned about the vertical. Angular,
    /// so it reads as a broken piece of rock and not as a ball.
    /// </summary>
    private static void Block(List<ShapeTriangle> all, int seed, Point3 centre, Point3 size, double yaw, double jitter)
    {
        var (cos, sin) = (Math.Cos(yaw), Math.Sin(yaw));
        (double Y, double Wide)[] rows = [(-0.5, 0.88), (0.08, 1.0), (0.5, 0.74)];
        var corners = new Point3[rows.Length][];
        for (var row = 0; row < rows.Length; row++)
        {
            corners[row] = new Point3[4];
            for (var k = 0; k < 4; k++)
            {
                var (sx, sz) = (k is 0 or 3 ? -0.5 : 0.5, k < 2 ? -0.5 : 0.5);
                var x = (sx * size.X * rows[row].Wide) + (Jitter(seed, (row * 4) + k, 0) * jitter);
                var y = (rows[row].Y * size.Y) + (Jitter(seed, (row * 4) + k, 1) * jitter * 0.6);
                var z = (sz * size.Z * rows[row].Wide) + (Jitter(seed, (row * 4) + k, 2) * jitter);
                corners[row][k] = new Point3(centre.X + (x * cos) + (z * sin), centre.Y + y, centre.Z - (x * sin) + (z * cos));
            }
        }

        var stone = new Piece(StoneRole);
        Point3 Outward(Point3 c) => new(c.X - centre.X, c.Y - centre.Y, c.Z - centre.Z);
        for (var row = 0; row + 1 < rows.Length; row++)
        {
            for (var k = 0; k < 4; k++)
            {
                stone.Quad(corners[row][k], corners[row][(k + 1) % 4], corners[row + 1][(k + 1) % 4], corners[row + 1][k], Outward);
            }
        }

        stone.Quad(corners[^1][0], corners[^1][1], corners[^1][2], corners[^1][3], _ => new Point3(0, 1, 0));
        stone.Quad(corners[0][0], corners[0][1], corners[0][2], corners[0][3], _ => new Point3(0, -1, 0));
        stone.Into(all, hull: true);
    }

    /// <summary>A value in -1..1 from three whole numbers, the same at every start.</summary>
    private static double Jitter(int a, int b, int c) => (Hash(a, b, c) * 2) - 1;

    /// <summary>A value in 0..1 from three whole numbers, the same at every start and on every platform.</summary>
    private static double Hash(int a, int b, int c)
    {
        unchecked
        {
            var x = ((uint)a * 73856093u) ^ ((uint)b * 19349663u) ^ ((uint)c * 83492791u);
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return (x & 0xFFFFFF) / (double)0x1000000;
        }
    }

    /// <summary>The triangles of one closed part: collected flat-shaded, then given their outline directions together.</summary>
    private sealed class Piece(int role)
    {
        private readonly List<Point3> _corners = [];
        private readonly List<Point3> _normals = [];

        /// <summary>Adds a triangle whose face looks towards <paramref name="toward"/>, whatever order its corners come in; one without area is left out.</summary>
        public void Add(Point3 a, Point3 b, Point3 c, Point3 toward)
        {
            var (ux, uy, uz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var (vx, vy, vz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            var (nx, ny, nz) = ((uy * vz) - (uz * vy), (uz * vx) - (ux * vz), (ux * vy) - (uy * vx));
            var length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            if (length < 1e-12)
            {
                return;
            }

            (nx, ny, nz) = (nx / length, ny / length, nz / length);
            if ((nx * toward.X) + (ny * toward.Y) + (nz * toward.Z) < 0)
            {
                (b, c, nx, ny, nz) = (c, b, -nx, -ny, -nz);
            }

            var normal = new Point3(nx, ny, nz);
            _corners.AddRange([a, b, c]);
            _normals.AddRange([normal, normal, normal]);
        }

        /// <summary>Adds a four-sided facet as two triangles, each looking the way <paramref name="toward"/> says at its middle.</summary>
        public void Quad(Point3 a, Point3 b, Point3 c, Point3 d, Func<Point3, Point3> toward)
        {
            Add(a, b, c, toward(Middle(a, b, c)));
            Add(a, c, d, toward(Middle(a, c, d)));
        }

        /// <summary>Hands the part over, with outline directions that keep its hull closed, or without an outline (its own normals).</summary>
        public void Into(List<ShapeTriangle> all, bool hull)
        {
            IReadOnlyList<Point3> directions = hull ? OutlineHull.Directions(_corners, _normals) : _normals;
            for (var i = 0; i < _corners.Count; i += 3)
            {
                all.Add(new ShapeTriangle(_corners[i], _corners[i + 1], _corners[i + 2], _normals[i], role, directions[i], directions[i + 1], directions[i + 2]));
            }
        }

        private static Point3 Middle(Point3 a, Point3 b, Point3 c) => new((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
    }
}
