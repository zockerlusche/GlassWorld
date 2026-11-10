using GlassWorld.Contracts;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.World;

/// <summary>Rock walls as natural rock faces (TL #20449), resolved from the look data.</summary>
/// <param name="LeanM">How far the foot of a face stands out from its top edge (metres).</param>
/// <param name="LedgeLeanM">The same where the foot stands on a ledge one cell wide with the next drop behind it (metres).</param>
/// <param name="BevelM">Size of the broken top edge: how far the face steps out and down below its top edge (metres).</param>
/// <param name="JitterM">Largest displacement of a face vertex by the hash of its grid corner (metres).</param>
/// <param name="RowM">Height of one row of facets (metres): a taller face bends more often on its way down.</param>
/// <param name="Blend">How far the colour at the top edge and at the foot takes on the colour of the ground there (share).</param>
public sealed record RockFaceLook(float LeanM, float LedgeLeanM, float BevelM, float JitterM, float RowM, float Blend)
{
    /// <summary>Most rows of facets between the broken top edge and the foot of a face.</summary>
    public const int MaxRows = 4;

    /// <summary>
    /// Resolves the face data for a grid of the given cell size. A face must stay a steep wall next to its
    /// cliff: with its jitter it ends before the middle of the cell at its foot, where the core lets
    /// inhabitants stand.
    /// </summary>
    public static RockFaceLook From(TerrainRockFace face, float cellM)
    {
        ArgumentNullException.ThrowIfNull(face);
        var halfCellMm = cellM * 500f;
        if (Math.Max(face.LeanMm, face.LedgeLeanMm) + face.JitterMm >= halfCellMm)
        {
            throw new InvalidDataException($"Terrain look rock.face: lean_mm {face.LeanMm} and ledge_lean_mm {face.LedgeLeanMm} plus jitter_mm {face.JitterMm} must stay below half a cell ({halfCellMm:0} mm).");
        }

        if (face.BevelMm > face.LeanMm || face.JitterMm * 2 > face.LeanMm)
        {
            throw new InvalidDataException($"Terrain look rock.face: bevel_mm {face.BevelMm} must be at most lean_mm {face.LeanMm} and jitter_mm {face.JitterMm} at most half of it.");
        }

        return new RockFaceLook(face.LeanMm / 1000f, face.LedgeLeanMm / 1000f, face.BevelMm / 1000f, face.JitterMm / 1000f, face.RowMm / 1000f, face.BlendPermille / 1000f);
    }
}

/// <content>
/// Rock faces (TL #20449): with <see cref="RockLook.Face"/> a cliff wall is no vertical plane on the grid
/// line but a skin over the unchanged ground. Its top edge is the terrace edge (the ground vertices of the
/// higher cells); from there it steps out over a broken edge and leans down to a foot that lies on the
/// ground of the lower cells. Every vertex below the top edge is moved by a hash of its grid corner, so no
/// two stretches are alike and none is a straight block side; the same map always gives the same faces.
/// The ground mesh, its heights and <see cref="GroundHeight"/> are untouched: the core's cliffs stay
/// where they are, and the face stays steep and ends before the middle of the cell at its foot.
/// </content>
public static partial class TerrainMesher
{
    // Edge-neighbour pairs among the four cells around a grid corner (bits by Slot).
    private static readonly (int A, int B)[] SlotNeighbours = [(0, 1), (2, 3), (0, 2), (1, 3)];

    /// <summary>
    /// One end of a rock face at the grid corner (gx, gz), from the top edge down to the foot, between the
    /// cell <paramref name="own"/> (higher) and its neighbour <paramref name="other"/>. Where exactly two
    /// height groups meet at the corner the face leans out towards the lower one: top edge, broken edge,
    /// a bend per row of facets and a foot on the lower ground. The shape depends on the corner and its
    /// groups alone, so every wall that ends at this corner (the next stretch, the wall around an outer or
    /// inner corner) gets the same vertices and the faces stay closed. Everywhere else (the wall runs out,
    /// three levels meet, the lower ground is itself broken or rises steeply) the end is the plain vertical
    /// one of <see cref="Side"/>.
    /// </summary>
    private static List<((float X, float Y, float Z) P, Rgb C)> FaceSide(WorldMap map, TerrainLook look, RockFaceLook face, int gx, int gz, (int X, int Z) own, (int X, int Z) other, int top, int foot, float px, float pz)
    {
        var plain = Side(map, look, gx, gz, top, foot, px, pz);
        if (top <= foot)
        {
            return plain;
        }

        var (upper, all) = CornerGroup(map, look, gx, gz, Slot(gx, gz, own.X, own.Z));
        var lower = CornerGroup(map, look, gx, gz, Slot(gx, gz, other.X, other.Z)).Members;
        if ((upper | lower) != all)
        {
            return plain;
        }

        // Outwards: from the cells of the higher group to those of the lower one (straight along a wall,
        // diagonal around a corner).
        int ox = 0, oz = 0;
        var ledge = false;
        for (var s = 0; s < 4; s++)
        {
            var (sx, sz) = ((s & 1) == 0 ? -1 : 1, (s >> 1) == 0 ? -1 : 1);
            var sign = (lower & (1 << s)) != 0 ? 1 : (upper & (1 << s)) != 0 ? -1 : 0;
            (ox, oz) = (ox + (sign * sx), oz + (sign * sz));
        }

        (ox, oz) = (Math.Sign(ox), Math.Sign(oz));
        if (ox == 0 && oz == 0)
        {
            return plain;
        }

        // The lower ground must be one closed surface around the corner; a ledge is lower ground with the
        // next drop right behind it.
        foreach (var (a, b) in SlotNeighbours)
        {
            if ((lower & (1 << a)) != 0 && (lower & (1 << b)) != 0 && !Joined(CellAt(a), CellAt(b), look))
            {
                return plain;
            }
        }

        for (var s = 0; s < 4; s++)
        {
            if ((lower & (1 << s)) != 0)
            {
                int cx = gx - 1 + (s & 1), cz = gz - 1 + (s >> 1);
                ledge |= DropsBehind(map, look, cx, cz, ox, 0) || DropsBehind(map, look, cx, cz, 0, oz);
            }
        }

        var lean = ledge ? face.LedgeLeanM : face.LeanM;
        if (lean <= 0)
        {
            return plain;
        }

        var length = MathF.Sqrt((ox * ox) + (oz * oz));
        (float X, float Z) Out(int row, float outM)
        {
            // The jitter never reaches back to the wall line, so the vertex stays over the lower ground.
            var reach = MathF.Min(face.JitterM, outM / 2);
            return (
                Math.Clamp(px + (ox / length * outM) + (reach * Jitter(gx, gz, row * 3)), 0f, map.WidthCells * look.CellM),
                Math.Clamp(pz + (oz / length * outM) + (reach * Jitter(gx, gz, (row * 3) + 1)), 0f, map.DepthCells * look.CellM));
        }

        // The foot stands on the ground of the lower group.
        var (kx, kz) = Out(0, lean);
        int fx = Math.Min((int)MathF.Floor(kx / look.CellM), map.WidthCells - 1), fz = Math.Min((int)MathF.Floor(kz / look.CellM), map.DepthCells - 1);
        if (fx < gx - 1 || fx > gx || fz < gz - 1 || fz > gz || (lower & (1 << Slot(gx, gz, fx, fz))) == 0)
        {
            return plain;
        }

        float topY = top * look.UnitM, footY = GroundHeight(map, look, kx, kz);
        var drop = topY - footY;
        if (drop < (top - foot) * look.UnitM / 2)
        {
            return plain;
        }

        var rock = look.Rock!;
        Rgb upperColor = WallColor(look, px, pz, foot: false), lowerColor = WallColor(look, px, pz, foot: true);
        var ground = GroupColor(map, look, gx, gz, lower);
        ground = rock.Scree > 0 ? ground.Lerp(look.Cliff, rock.Scree) : ground;
        float Grain(int row) => 1f + (rock.Variation / 2 * Jitter(gx, gz, 64 + row));

        // The top edge is the ground vertex of the higher group and takes on its colour, so the eye finds
        // the edge of the rock at the broken edge below it, not on the straight grid line.
        var result = new List<((float X, float Y, float Z) P, Rgb C)> { ((px, topY, pz), upperColor.Lerp(GroupColor(map, look, gx, gz, upper), face.Blend)) };

        // Broken top edge: a short step out and down.
        var edgeOut = MathF.Min(face.BevelM, lean / 2);
        var edgeDrop = MathF.Min(face.BevelM, drop * 0.3f);
        var edgeY = topY - edgeDrop + (MathF.Min(face.JitterM, edgeDrop * 0.3f) * Jitter(gx, gz, 5));
        if (edgeOut > 0 && edgeDrop > 0)
        {
            var (sx, sz) = Out(1, edgeOut);
            result.Add(((sx, edgeY, sz), upperColor.Scaled(Grain(1))));
        }

        // The face bends once per row of facets on its way down, darker towards the foot.
        var bends = face.RowM > 0 ? Math.Clamp((int)((edgeY - footY) / face.RowM), 1, RockFaceLook.MaxRows) : 1;
        for (var bend = 1; bend <= bends; bend++)
        {
            var share = bend / (bends + 1f);
            var (mx, mz) = Out(1 + bend, edgeOut + ((lean - edgeOut) * share));
            var y = edgeY + ((footY - edgeY) * share) + (MathF.Min(face.JitterM, (edgeY - footY) / (bends + 1) * 0.3f) * Jitter(gx, gz, ((1 + bend) * 3) + 2));
            result.Add(((mx, y, mz), upperColor.Lerp(lowerColor, share).Scaled(Grain(1 + bend))));
        }

        result.Add(((kx, footY, kz), lowerColor.Lerp(ground, face.Blend)));
        return result;

        MapCell CellAt(int s) => map.Cell(gx - 1 + (s & 1), gz - 1 + (s >> 1));
    }

    /// <summary>Whether the cell stands a rock cliff above its neighbour in the given direction (no direction = no).</summary>
    private static bool DropsBehind(WorldMap map, TerrainLook look, int x, int z, int dx, int dz)
    {
        if ((dx == 0 && dz == 0) || !map.Contains(x + dx, z + dz))
        {
            return false;
        }

        var (cell, behind) = (map.Cell(x, z), map.Cell(x + dx, z + dz));
        return behind.Height < cell.Height && !Joined(cell, behind, look);
    }

    /// <summary>A fixed value in -1..1 per grid corner and salt: the hash of the rock's colour noise, no random numbers.</summary>
    private static float Jitter(int gx, int gz, int salt) => (float)RockLook.Lattice(gx, gz, 0xFACE00u + (uint)salt);

    /// <summary>
    /// The face between two ends (<see cref="FaceSide"/>), seen from the lower side. Above the feet the two
    /// ends are joined like a ladder, top down. The foot line between the two feet follows the ground: it
    /// gets a vertex wherever it crosses an edge of the ground mesh (<see cref="FootLine"/>), so every piece
    /// of it lies in one ground triangle and the face neither floats above the ground nor leaves a gap.
    /// </summary>
    private static void Face(WorldMap map, TerrainLook look, MeshData mesh, List<((float X, float Y, float Z) P, Rgb C)> sideA, List<((float X, float Y, float Z) P, Rgb C)> sideB)
    {
        int lastA = Math.Max(0, sideA.Count - 2), lastB = Math.Max(0, sideB.Count - 2);
        int i = 0, j = 0;
        while (i < lastA || j < lastB)
        {
            if (i < lastA && (j >= lastB || sideA[i + 1].P.Y >= sideB[j + 1].P.Y))
            {
                Add(sideA[i], sideB[j], sideA[i + 1]);
                i++;
            }
            else
            {
                Add(sideA[i], sideB[j], sideB[j + 1]);
                j++;
            }
        }

        var (a, b) = (sideA[lastA], sideB[lastB]);
        var foot = FootLine(map, look, sideA[^1], sideB[^1]);

        // A fan from each end over its half of the foot line, and the triangle between the two ends.
        var middle = a.P == foot[0].V.P ? 0 : foot.Count - 1;
        if (middle != 0 && b.P != foot[^1].V.P)
        {
            for (var k = 0; k < foot.Count; k++)
            {
                middle = Math.Abs(foot[k].T - 0.5) < Math.Abs(foot[middle].T - 0.5) ? k : middle;
            }
        }

        for (var k = 0; k < middle; k++)
        {
            Add(a, foot[k + 1].V, foot[k].V);
        }

        Add(a, b, foot[middle].V);
        for (var k = middle; k + 1 < foot.Count; k++)
        {
            Add(b, foot[k + 1].V, foot[k].V);
        }

        void Add(((float X, float Y, float Z) P, Rgb C) u, ((float X, float Y, float Z) P, Rgb C) v, ((float X, float Y, float Z) P, Rgb C) w)
        {
            // An end where the wall runs out is one vertex: no sliver.
            if (u.P != v.P && v.P != w.P && u.P != w.P)
            {
                mesh.Triangle(u.P, v.P, w.P, u.C, v.C, w.C);
            }
        }
    }

    /// <summary>
    /// The foot line of a face from one foot to the other, with their share along it (0..1): the two feet
    /// and a vertex on the ground wherever the straight line between them crosses an edge of the ground
    /// mesh, that is a grid line or the diagonal a cell is split along (both in a stream trough). Two feet
    /// on the same grid line (plain ends) are joined directly: that is the ground's own edge.
    /// </summary>
    private static List<(double T, ((float X, float Y, float Z) P, Rgb C) V)> FootLine(WorldMap map, TerrainLook look, ((float X, float Y, float Z) P, Rgb C) from, ((float X, float Y, float Z) P, Rgb C) to)
    {
        double ax = from.P.X / look.CellM, az = from.P.Z / look.CellM, bx = to.P.X / look.CellM, bz = to.P.Z / look.CellM;
        var cuts = new List<(double T, bool Diagonal02, bool Diagonal13)>();
        void Crossings(double start, double end, bool diagonal02, bool diagonal13)
        {
            for (var k = (int)Math.Floor(Math.Min(start, end)) + 1; k < Math.Max(start, end); k++)
            {
                cuts.Add(((k - start) / (end - start), diagonal02, diagonal13));
            }
        }

        Crossings(ax, bx, diagonal02: false, diagonal13: false);
        Crossings(az, bz, diagonal02: false, diagonal13: false);
        Crossings(ax - az, bx - bz, diagonal02: true, diagonal13: false);
        Crossings(ax + az, bx + bz, diagonal02: false, diagonal13: true);
        cuts.Sort((p, q) => p.T.CompareTo(q.T));

        var line = new List<(double T, ((float X, float Y, float Z) P, Rgb C) V)> { (0, from) };
        Span<int> h = stackalloc int[4];
        foreach (var (t, diagonal02, diagonal13) in cuts)
        {
            if (t <= 1e-6 || t >= 1 - 1e-6 || t - line[^1].T <= 1e-6)
            {
                continue;
            }

            double x = ax + ((bx - ax) * t), z = az + ((bz - az) * t);
            if (diagonal02 || diagonal13)
            {
                // Only the diagonal the cell is split along is an edge of its ground.
                int cx = Math.Clamp((int)Math.Floor(x), 0, map.WidthCells - 1), cz = Math.Clamp((int)Math.Floor(z), 0, map.DepthCells - 1);
                if (!IsTrough(map, cx, cz))
                {
                    CornerHeights(map, cx, cz, look, h);
                    if (SplitsAlong02(h) != diagonal02)
                    {
                        continue;
                    }
                }
            }

            var (px, pz) = ((float)(x * look.CellM), (float)(z * look.CellM));
            line.Add((t, ((px, GroundHeight(map, look, px, pz), pz), from.C.Lerp(to.C, (float)t))));
        }

        line.Add((1, to));
        return line;
    }
}
