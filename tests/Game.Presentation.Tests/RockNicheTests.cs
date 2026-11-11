using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// The rock niche (TL #20750, survey D704): a shelter in a rock wall one can read - a dark opening under a
/// lintel between side rocks, flush on the ground, opening away from the wall - and the ink outline that
/// sits on flat-shaded rocks instead of floating above them.
/// </summary>
public sealed class RockNicheTests
{
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestPaths.Root, "content")));

    private static T Read<T>(params string[] path)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    private static ObjectLooks Looks() =>
        new(Read<LookObjects>("look", "objects.json"), Read<LookPalette>("look", "palette.json").Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal));

    private static ObjectLook Niche() => Looks().For("rock_niche");

    private static Point3 Minus(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static Point3 Cross(Point3 a, Point3 b) => new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    private static double Dot(Point3 a, Point3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    /// <summary>The first triangle a ray hits (Möller–Trumbore), or null.</summary>
    private static ShapeTriangle? FirstHit(IEnumerable<ShapeTriangle> triangles, Point3 from, Point3 direction)
    {
        ShapeTriangle? first = null;
        var nearest = double.MaxValue;
        foreach (var t in triangles)
        {
            var (e1, e2) = (Minus(t.B, t.A), Minus(t.C, t.A));
            var p = Cross(direction, e2);
            var det = Dot(e1, p);
            if (Math.Abs(det) < 1e-12)
            {
                continue;
            }

            var s = Minus(from, t.A);
            var u = Dot(s, p) / det;
            var q = Cross(s, e1);
            var v = Dot(direction, q) / det;
            var distance = Dot(e2, q) / det;
            if (u >= 0 && v >= 0 && u + v <= 1 && distance > 1e-9 && distance < nearest)
            {
                (first, nearest) = (t, distance);
            }
        }

        return first;
    }

    [Fact]
    public void TheDataDrawsTheRockNicheAsAShelterThatLeansOnItsWall()
    {
        var look = Niche();
        Assert.Equal((ObjectLookShape.Rock_niche, ObjectLookAlign.Wall), (look.Shape, look.Align));
        Assert.False(look.Lies, "it stands upright, it does not follow the slope");
        Assert.NotNull(look.Niche);
        Assert.True(look.WallProbeM > 0 && look.WallMinRiseM > 0);

        // The inside has a colour of its own, clearly darker than the rock around it.
        var palette = Read<LookPalette>("look", "palette.json").Colors.ToDictionary(c => c.Id, c => Rgb.Parse(c.Color), StringComparer.Ordinal);
        static float Light(Rgb c) => (c.R + c.G + c.B) / 3;
        var (rock, stone, inside) = (palette[look.Color(RockNiche.BodyRole)], palette[look.Color(RockNiche.StoneRole)], palette[look.Color(RockNiche.InsideRole)]);
        Assert.True(Light(inside) < 0.5f * Math.Min(Light(rock), Light(stone)), "the inside is less than half as light as the rock");

        // An opening an inhabitant fits under, in a rock that is larger than its opening.
        var shape = look.Niche;
        Assert.InRange(shape.OpeningHeightM * look.SizeM, 1.2, 2.5);
        Assert.InRange(shape.OpeningWidthM * look.SizeM, 1.0, 3.0);
        Assert.True(shape.DepthM > shape.RevealM && shape.BackM > shape.HollowBackX);
    }

    [Fact]
    public void ANicheWithoutItsMeasuresIsRefused()
    {
        var data = Read<LookObjects>("look", "objects.json");
        data.Visuals.Single(v => v.Shape == ObjectLookShape.Rock_niche).Niche = null;
        var palette = Read<LookPalette>("look", "palette.json").Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("niche", Assert.Throws<InvalidDataException>(() => new ObjectLooks(data, palette)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOpeningIsDarkFromTheFrontAndRockFromEveryOtherSide()
    {
        var shape = Niche().Niche!;
        var niche = RockNiche.Build(shape);
        Assert.Contains(niche, t => t.Role == RockNiche.BodyRole);
        Assert.Contains(niche, t => t.Role == RockNiche.StoneRole);
        Assert.Contains(niche, t => t.Role == RockNiche.InsideRole);
        var (w, h) = (shape.OpeningWidthM, shape.OpeningHeightM);

        // Looking into the opening from the open side (local -x), level and from above as the camera
        // does: across its clear width one sees the dark inside, from its front side - never rock in the way.
        foreach (var down in (ReadOnlySpan<double>)[0.0, 0.5, 1.0])
        {
            foreach (var across in (ReadOnlySpan<double>)[-0.3, -0.15, 0, 0.15, 0.3])
            {
                foreach (var up in (ReadOnlySpan<double>)[0.1, 0.25, 0.4, 0.55])
                {
                    var target = new Point3(shape.LipX + shape.RevealM, up * h, across * w);
                    var direction = new Point3(1, -down, 0);
                    var from = new Point3(target.X - (4 * direction.X), target.Y - (4 * direction.Y), target.Z);
                    var hit = FirstHit(niche, from, direction);
                    Assert.True(hit is { Role: RockNiche.InsideRole }, $"looking {down:0.0} down at height {up:0.00} h, {across:0.00} w across: {(hit is null ? "nothing" : $"role {hit.Value.Role}")} instead of the dark inside");
                    Assert.True(Dot(hit!.Value.Normal, direction) < 0, "the inside is seen from its front side");
                }
            }
        }

        // From behind, from either side and from above one sees rock.
        foreach (var (from, direction) in new[]
        {
            (new Point3(shape.BackM + 4, 0.5 * h, 0), new Point3(-1, 0, 0)),
            (new Point3(0, 0.4 * h, -6), new Point3(0, 0, 1)),
            (new Point3(0, 0.4 * h, 6), new Point3(0, 0, -1)),
            (new Point3(0.2, 8, 0), new Point3(0, -1, 0)),
            (new Point3(shape.LipX, 8, 0), new Point3(0, -1, 0)),
        })
        {
            var hit = FirstHit(niche, from, direction);
            Assert.True(hit is { Role: RockNiche.BodyRole or RockNiche.StoneRole }, $"from ({from.X:0.0}, {from.Y:0.0}, {from.Z:0.0}): {(hit is null ? "nothing" : $"role {hit.Value.Role}")} instead of rock");
        }
    }

    [Fact]
    public void ALintelJutsOutOverTheOpeningBetweenTwoSideRocksAndTheRockReachesBelowTheGround()
    {
        var shape = Niche().Niche!;
        var niche = RockNiche.Build(shape);
        var (w, h) = (shape.OpeningWidthM, shape.OpeningHeightM);
        static IEnumerable<Point3> Corners(ShapeTriangle t) => [t.A, t.B, t.C];
        var rock = niche.Where(t => t.Role != RockNiche.InsideRole).SelectMany(Corners).ToList();
        var stones = niche.Where(t => t.Role == RockNiche.StoneRole).SelectMany(Corners).ToList();
        var inside = niche.Where(t => t.Role == RockNiche.InsideRole).SelectMany(Corners).ToList();

        // The lintel: rock above the opening, across its whole width, further out than its foot.
        var above = stones.Where(p => p.Y > h).ToList();
        Assert.True(above.Min(p => p.X) <= shape.LipX - (0.6 * shape.OverhangM), $"the lintel reaches {shape.LipX - above.Min(p => p.X):0.00} m out over the foot (overhang {shape.OverhangM})");
        Assert.True(above.Min(p => p.Z) < -w / 2 && above.Max(p => p.Z) > w / 2, "the lintel spans the opening");
        Assert.True(rock.Max(p => p.Y) >= h + (0.8 * shape.LintelM), "rock above the opening");

        // A rock at either side, standing on the ground, clear of the opening.
        foreach (var side in (ReadOnlySpan<int>)[-1, 1])
        {
            var jamb = stones.Where(p => p.Y < 0.5 * h && p.Z * side > 0).ToList();
            Assert.NotEmpty(jamb);
            Assert.True(jamb.Min(p => p.Z * side) > 0.3 * w, "a side rock leaves the opening free");
            Assert.True(jamb.Min(p => p.Y) < 0, "a side rock stands in the ground");
        }

        // Flush on the ground: the body reaches down to the sink all around its foot, nothing ends above y = 0.
        var feet = niche.Where(t => t.Role == RockNiche.BodyRole).SelectMany(Corners).Where(p => p.Y < 0).ToList();
        Assert.All(feet, p => Assert.Equal(-shape.SinkM, p.Y, 9));
        Assert.True(feet.Min(p => p.Z) < -w / 2 && feet.Max(p => p.Z) > w / 2 && feet.Max(p => p.X) > shape.HollowBackX);

        // The dark hollow lies inside the rock: behind the lip, in front of the back of the rock, under its top.
        Assert.All(inside, p => Assert.InRange(p.X, shape.LipX, shape.BackM));
        Assert.All(inside, p => Assert.InRange(p.Y, -shape.SinkM - 1e-9, h + 1e-9));
        Assert.All(inside, p => Assert.InRange(p.Z, (-w / 2) - 1e-9, (w / 2) + 1e-9));

        // Nothing stands further out than the lintel: the ground before the opening stays free.
        Assert.True(rock.Min(p => p.X) >= shape.LipX - shape.OverhangM - (0.5 * shape.SideRockM));
    }

    [Fact]
    public void EveryFaceIsFlatWithItsNormalAndTheSameMeasuresGiveTheSameNiche()
    {
        var shape = Niche().Niche!;
        var niche = RockNiche.Build(shape);
        Assert.Equal(niche, RockNiche.Build(shape));
        Assert.All(niche, t =>
        {
            var n = Cross(Minus(t.B, t.A), Minus(t.C, t.A));
            var length = Math.Sqrt(Dot(n, n));
            Assert.True(length > 1e-9, "no face without area");
            Assert.Equal(1, Dot(n, t.Normal) / length, 9);
        });

        // A different measure changes it: the shape is data, not a fixed model.
        Assert.NotEqual(niche, RockNiche.Build(shape with { OpeningWidthM = shape.OpeningWidthM * 1.2 }));
    }

    [Fact]
    public void TheOutlineOfTheRockIsOneClosedSkinOnTheBody()
    {
        // The cause of the floating strokes (survey D704): pushed out along its own flat normals, every
        // face of the hull moves off by itself. With the common directions all corners at a place move
        // together, and every face still lies a whole outline width off its face of the body.
        var rock = RockNiche.Build(Niche().Niche!).Where(t => t.Role != RockNiche.InsideRole).ToList();
        var places = new Dictionary<(long, long, long), List<(Point3 Hull, Point3 Normal)>>();
        foreach (var t in rock)
        {
            foreach (var (p, hull) in new[] { (t.A, t.HullA), (t.B, t.HullB), (t.C, t.HullC) })
            {
                var key = ((long)Math.Round(p.X / OutlineHull.WeldM), (long)Math.Round(p.Y / OutlineHull.WeldM), (long)Math.Round(p.Z / OutlineHull.WeldM));
                if (!places.TryGetValue(key, out var list))
                {
                    places[key] = list = [];
                }

                list.Add((hull, t.Normal));
            }
        }

        var torn = 0;
        foreach (var corners in places.Values)
        {
            // One direction per place: the hull does not open there.
            Assert.All(corners, c => Assert.True(Dot(Minus(c.Hull, corners[0].Hull), Minus(c.Hull, corners[0].Hull)) < 1e-18, "the corners of one place move apart"));

            // Along their own normals they would: this is what floated.
            torn += corners.Any(c => Dot(Minus(c.Normal, corners[0].Normal), Minus(c.Normal, corners[0].Normal)) > 1e-6) ? 1 : 0;

            // Every face moves outwards, as a rule by a whole width, never by more than the cap.
            Assert.All(corners, c => Assert.InRange(Dot(c.Hull, c.Normal), 0.05, OutlineHull.MaxLength + 1e-9));
        }

        Assert.True(torn > places.Count / 2, "most places of a flat-shaded rock have faces with different normals");
        var whole = places.Values.SelectMany(c => c).Count(c => Dot(c.Hull, c.Normal) >= 1 - 1e-9);
        Assert.True(whole >= 0.9 * places.Values.Sum(c => c.Count), "nine in ten face corners lie a whole outline width out");
    }

    [Fact]
    public void OutlineDirectionsOfACubeAreItsDiagonalsAndALeafKeepsItsNormals()
    {
        // A flat-shaded cube: three faces meet at every corner, each with its own normal.
        var positions = new List<Point3>();
        var normals = new List<Point3>();
        foreach (var axis in (ReadOnlySpan<int>)[0, 1, 2])
        {
            foreach (var side in (ReadOnlySpan<int>)[-1, 1])
            {
                foreach (var (a, b) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                {
                    positions.Add(axis switch { 0 => new Point3(side, a, b), 1 => new Point3(a, side, b), _ => new Point3(a, b, side) });
                    normals.Add(axis switch { 0 => new Point3(side, 0, 0), 1 => new Point3(0, side, 0), _ => new Point3(0, 0, side) });
                }
            }
        }

        var hull = OutlineHull.Directions(positions, normals);
        for (var i = 0; i < positions.Count; i++)
        {
            // Out along the diagonal, long enough that each of the three faces moves one whole width.
            Assert.Equal(positions[i].X, hull[i].X, 9);
            Assert.Equal(positions[i].Y, hull[i].Y, 9);
            Assert.Equal(positions[i].Z, hull[i].Z, 9);
            Assert.Equal(1, Dot(hull[i], normals[i]), 9);
        }

        // The two sides of a leaf cancel: each keeps its own normal, as before.
        Point3[] leaf = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 0), new(0, 1, 0), new(1, 0, 0)];
        Point3[] sides = [new(0, 0, 1), new(0, 0, 1), new(0, 0, 1), new(0, 0, -1), new(0, 0, -1), new(0, 0, -1)];
        Assert.Equal(sides, OutlineHull.Directions(leaf, sides));

        // A sharp spike is capped.
        Point3[] spike = [new(0, 0, 0), new(0, 0, 0)];
        Point3[] apart = [new(1, 0, 0), new(-0.999, Math.Sqrt(1 - (0.999 * 0.999)), 0)];
        Assert.All(OutlineHull.Directions(spike, apart), d => Assert.InRange(Math.Sqrt(Dot(d, d)), 1, OutlineHull.MaxLength + 1e-9));
        Assert.Throws<ArgumentException>(() => OutlineHull.Directions(leaf, apart));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(-1, 1)]
    public void TheOpeningFacesAwayFromTheWall(int wallX, int wallZ)
    {
        // A rock face 0.3 m beyond the object, 1.5 m high, on the side (wallX, wallZ); level ground elsewhere.
        var length = Math.Sqrt((wallX * wallX) + (wallZ * wallZ));
        var (nx, nz) = (wallX / length, wallZ / length);
        Func<double, double, double> ground = (x, z) => (((x - 50) * nx) + ((z - 50) * nz)) > 0.3 ? 4.5 : 3.0;
        var look = Niche();
        var placed = ObjectLooks.Place(new WorldObject("obj-7", "rock_niche", new Position(50, 50)), look, ground);
        Assert.Equal(new Point3(0, 1, 0), placed.Up);

        // Local +x (the back of the niche) points into the wall, so the opening (local -x) faces the open ground.
        var back = Minus(placed.ToWorld(new Point3(1, 0, 0)), placed.ToWorld(new Point3(0, 0, 0)));
        Assert.Equal(nx * placed.Scale, back.X, 6);
        Assert.Equal(nz * placed.Scale, back.Z, 6);
        var before = placed.ToWorld(new Point3(look.Niche!.LipX - 1, 0, 0));
        var behind = placed.ToWorld(new Point3(look.Niche.BackM, 0, 0));
        Assert.Equal(3.0, ground(before.X, before.Z));
        Assert.Equal(4.5, ground(behind.X, behind.Z));

        // It stands on the low ground before the wall, not on top of it.
        Assert.Equal(3.0 + look.LiftM, placed.Y, 9);
    }

    [Fact]
    public void WithoutAWallTheNicheKeepsTheTurnFromItsId()
    {
        var look = Niche();
        var obj = new WorldObject("obj-7", "rock_niche", new Position(50, 50));
        var own = ObjectLooks.Hash01("obj-7", 1) * 2 * Math.PI;

        // Level ground, a rise too low to be a wall, and a pit with walls all around: no side to lean on.
        Assert.Equal(own, ObjectLooks.Place(obj, look, (_, _) => 3).YawRad, 9);
        Assert.Equal(own, ObjectLooks.Place(obj, look, (x, _) => x > 50.3 ? 3 + (0.5 * look.WallMinRiseM) : 3).YawRad, 9);
        Assert.Null(ObjectLooks.WallYaw((x, z) => Math.Sqrt(Math.Pow(x - 50, 2) + Math.Pow(z - 50, 2)) > 0.3 ? 5 : 3, 50, 50, look.WallProbeM, look.WallMinRiseM));
        Assert.Null(ObjectLooks.WallYaw((x, _) => x > 50.3 ? 5 : 3, 50, 50, 0, look.WallMinRiseM));

        // A wall on one side gives the turn, whatever the id.
        var east = ObjectLooks.WallYaw((x, _) => x > 50.3 ? 5 : 3, 50, 50, look.WallProbeM, look.WallMinRiseM);
        Assert.NotNull(east);
        Assert.Equal(0, east.Value, 9);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void TheNichesOfTheRealIslandsOpenToTheWalkableGroundAndSitInIt(ulong seed)
    {
        var island = WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed);
        var map = island.Map;
        var terrain = TerrainLook.From(Read<LookTerrain>("look", "terrain.json"), Read<LookPalette>("look", "palette.json"), map, Content.Value.WorldGen.Presets["start_island"]);
        Func<double, double, double> ground = (x, z) => TerrainMesher.GroundHeight(map, terrain, x, z);
        var looks = Looks();
        var look = looks.For("rock_niche");
        var shape = look.Niche!;
        var triangles = RockNiche.Build(shape);
        var niches = island.Objects.Where(o => o.TypeId == "rock_niche").ToList();
        Assert.NotEmpty(niches);
        var number = 0;
        foreach (var o in niches)
        {
            var name = $"seed {seed}: the niche at cell ({o.X}, {o.Z})";
            var placed = ObjectLooks.Place(new WorldObject($"obj-{number++:000000}", o.TypeId, new Position(o.X + 0.5, o.Z + 0.5)), look, ground, looks.Fit);
            Assert.Equal(new Point3(0, 1, 0), placed.Up);
            Assert.True(ObjectLooks.WallYaw(ground, placed.X, placed.Z, look.WallProbeM, look.WallMinRiseM) is not null, $"{name} finds its wall");

            // The opening faces the walkable ground: one metre before it the ground lies about level with
            // its floor and belongs to a cell one can walk on; behind it the rock stands higher.
            var before = placed.ToWorld(new Point3(shape.LipX - 1, 0, 0));
            var behind = placed.ToWorld(new Point3(shape.HollowBackX + 1, 0, 0));
            Assert.InRange(ground(before.X, before.Z) - placed.Y, -0.6, 0.6);
            Assert.True(ground(behind.X, behind.Z) > ground(before.X, before.Z) + 0.5, $"{name} has higher ground behind it ({ground(behind.X, behind.Z):0.00}) than before it ({ground(before.X, before.Z):0.00})");
            var cell = map.Cell((int)Math.Floor(before.X), (int)Math.Floor(before.Z));
            Assert.True(cell.WalkCost != MapCell.Blocked && cell.Water == WaterKind.None, $"{name} opens to ground one can walk on");

            // Flush: the foot of the rock lies in the ground all around (nothing floats) ...
            foreach (var t in triangles.Where(t => t.Role == RockNiche.BodyRole))
            {
                foreach (var p in new[] { t.A, t.B, t.C }.Where(p => p.Y < 0))
                {
                    var world = placed.ToWorld(p);
                    Assert.True(world.Y <= ground(world.X, world.Z) + 1e-6, $"{name}: the foot of the rock at ({world.X:0.0}, {world.Z:0.0}) hangs {world.Y - ground(world.X, world.Z):0.00} m above the ground");
                }
            }

            // ... and the ground does not come up through the dark of the opening: across the middle of
            // the hollow (its corners may stand in the rock of a narrow notch) it stays below the dark floor.
            foreach (var t in triangles.Where(t => t.Role == RockNiche.InsideRole && t.Normal.Y > 0.3))
            {
                foreach (var (u, v) in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (0.34, 0.33), (0.5, 0.5), (0.5, 0.0), (0.0, 0.5) })
                {
                    var local = new Point3(
                        t.A.X + (u * (t.B.X - t.A.X)) + (v * (t.C.X - t.A.X)),
                        t.A.Y + (u * (t.B.Y - t.A.Y)) + (v * (t.C.Y - t.A.Y)),
                        t.A.Z + (u * (t.B.Z - t.A.Z)) + (v * (t.C.Z - t.A.Z)));
                    if (Math.Abs(local.Z) > 0.25 * shape.OpeningWidthM)
                    {
                        continue;
                    }

                    var world = placed.ToWorld(local);
                    Assert.True(ground(world.X, world.Z) <= world.Y + 0.02, $"{name}: the ground shows {ground(world.X, world.Z) - world.Y:0.00} m above the dark floor at ({world.X:0.0}, {world.Z:0.0})");
                }
            }
        }
    }
}
