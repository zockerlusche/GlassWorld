using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.World;

/// <summary>A colour with components 0..1.</summary>
public readonly record struct Rgb(float R, float G, float B)
{
    /// <summary>Parses lowercase <c>#rrggbb</c>.</summary>
    public static Rgb Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrEmpty(hex);
        if (hex.Length != 7 || hex[0] != '#')
        {
            throw new FormatException($"Colour '{hex}' is not #rrggbb.");
        }

        var value = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgb(((value >> 16) & 0xff) / 255f, ((value >> 8) & 0xff) / 255f, (value & 0xff) / 255f);
    }

    /// <summary>Linear blend towards <paramref name="other"/> (0 = this, 1 = other).</summary>
    public Rgb Lerp(Rgb other, float t) => new(R + ((other.R - R) * t), G + ((other.G - G) * t), B + ((other.B - B) * t));
}

/// <summary>Colours and units for drawing the island grid, resolved from look data (B2 R18–R20).</summary>
/// <param name="BiomeColors">Ground colour per biome index of the map.</param>
/// <param name="Cliff">Cliff walls.</param>
/// <param name="Bed">Ground under water.</param>
/// <param name="SeaShallow">Shallow sea.</param>
/// <param name="SeaDeep">Deep sea.</param>
/// <param name="Fresh">Lakes and streams.</param>
/// <param name="Swamp">Murky swamp water.</param>
/// <param name="DeepFromUnits">Depth from which water counts as deep (height units).</param>
/// <param name="SwampMaxUnits">Fresh water this shallow is swamp (height units).</param>
/// <param name="SlopeShade">How much steep ground darkens towards the cliff colour (0..1).</param>
/// <param name="SlopeShadeGain">How fast the darkening rises with steepness ((1 - normal.y) times this, capped at 1).</param>
/// <param name="CellM">Edge length of a cell in metres.</param>
/// <param name="UnitM">One height unit in metres.</param>
/// <param name="CliffDrop">Height difference (units) from which neighbours form a rock wall instead of a slope (if the higher one may, see <paramref name="CliffBiomes"/>).</param>
/// <param name="ShoreLiftM">How far a stream's edge lies above the dry bank (metres), so it never sinks into it.</param>
/// <param name="StreamWidthM">Width of a stream band in metres (A1456).</param>
/// <param name="StreamSmoothing">Rounds of corner cutting along a stream's centre line (0 = cell-centre polyline).</param>
/// <param name="StreamSourceTaperM">Length over which a stream widens from its source to its full width (metres).</param>
/// <param name="StreamSourceWidthShare">Width of a stream at its source as a share of its full width.</param>
/// <param name="CliffBiomes">Per biome index of the map: whether its cells form rock walls where they stand at least <paramref name="CliffDrop"/> above a neighbour (null = every biome).</param>
public sealed record TerrainLook(
    IReadOnlyList<Rgb> BiomeColors,
    Rgb Cliff,
    Rgb Bed,
    Rgb SeaShallow,
    Rgb SeaDeep,
    Rgb Fresh,
    Rgb Swamp,
    int DeepFromUnits,
    int SwampMaxUnits,
    float SlopeShade,
    float SlopeShadeGain,
    float CellM,
    float UnitM,
    int CliffDrop,
    float ShoreLiftM,
    float StreamWidthM = 1.9f,
    int StreamSmoothing = 3,
    IReadOnlyList<bool>? CliffBiomes = null,
    float StreamSourceTaperM = 0,
    float StreamSourceWidthShare = 1)
{
    /// <summary>Resolves the look data against the palette, the map's biome order and the preset.</summary>
    public static TerrainLook From(LookTerrain terrain, LookPalette palette, WorldMap map, WorldPreset preset)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(preset);
        var colors = palette.Colors.ToDictionary(c => c.Id, c => Rgb.Parse(c.Color), StringComparer.Ordinal);
        Rgb Color(string id) => colors.TryGetValue(id, out var c) ? c : throw new InvalidDataException($"Terrain look names unknown palette colour '{id}'.");
        var byBiome = terrain.BiomeColors.ToDictionary(b => b.Biome, b => Color(b.Color), StringComparer.Ordinal);
        var biomeColors = map.BiomeIds
            .Select(id => byBiome.TryGetValue(id, out var c) ? c : throw new InvalidDataException($"Terrain look has no colour for biome '{id}'."))
            .ToList();
        foreach (var biome in terrain.Cliffs.Biomes.Where(b => !map.BiomeIds.Contains(b, StringComparer.Ordinal)))
        {
            throw new InvalidDataException($"Terrain look names unknown cliff biome '{biome}'.");
        }

        // The walls must stand where the core has its cliffs: every rock step of the core's cliff drop is a
        // wall (caves lie at their foot, preset caves.cliff_biome) and no walkable slope is (GW3 #120).
        var unitMm = preset.Grid.HeightUnitMm;
        var dropUnits = (terrain.Cliffs.MinDropMm + unitMm - 1) / unitMm;
        if (dropUnits > preset.Terrain.CliffMinDrop || dropUnits <= preset.Terrain.WalkableSlope)
        {
            throw new InvalidDataException($"Terrain look cliffs.min_drop_mm {terrain.Cliffs.MinDropMm} must lie above the preset's walkable slope ({preset.Terrain.WalkableSlope} units) and at most at its cliff drop ({preset.Terrain.CliffMinDrop} units of {unitMm} mm).");
        }

        if (!terrain.Cliffs.Biomes.Contains(preset.Caves.CliffBiome, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Terrain look cliffs.biomes must contain the preset's cave cliff biome '{preset.Caves.CliffBiome}'.");
        }
        return new TerrainLook(
            biomeColors,
            Color(terrain.CliffColor),
            Color(terrain.BedColor),
            Color(terrain.Water.SeaShallow),
            Color(terrain.Water.SeaDeep),
            Color(terrain.Water.Fresh),
            Color(terrain.Water.Swamp),
            terrain.Water.DeepFromUnits,
            terrain.Water.SwampMaxUnits,
            terrain.SlopeShadePermille / 1000f,
            (float)terrain.SlopeShadeGain,
            preset.Grid.CellSizeCm / 100f,
            preset.Grid.HeightUnitMm / 1000f,
            dropUnits,
            terrain.Water.StreamShoreLiftMm / 1000f,
            (float)terrain.Water.StreamWidthM,
            terrain.Water.StreamSmoothing,
            [.. map.BiomeIds.Select(id => terrain.Cliffs.Biomes.Contains(id, StringComparer.Ordinal))],
            (float)terrain.Water.StreamSourceTaperM,
            (float)terrain.Water.StreamSourceWidthShare);
    }
}

/// <summary>Triangle lists for one chunk (flat shaded: every triangle has its own three vertices).</summary>
public sealed class MeshData
{
    /// <summary>Vertex positions x, y, z in metres.</summary>
    public List<float> Positions { get; } = [];

    /// <summary>Vertex normals x, y, z.</summary>
    public List<float> Normals { get; } = [];

    /// <summary>Vertex colours r, g, b.</summary>
    public List<float> Colors { get; } = [];

    /// <summary>Per vertex flow direction x, z for water (0, 0 for still water and ground).</summary>
    public List<float> Flow { get; } = [];

    /// <summary>Number of vertices.</summary>
    public int VertexCount => Positions.Count / 3;

    /// <summary>Number of triangles.</summary>
    public int TriangleCount => VertexCount / 3;

    /// <summary>Adds a triangle with its face normal (counter-clockwise seen from the normal side).</summary>
    public void Triangle((float X, float Y, float Z) a, (float X, float Y, float Z) b, (float X, float Y, float Z) c, Rgb color, (float X, float Z) flow = default) =>
        Triangle(a, b, c, color, color, color, flow);

    /// <summary>Adds a triangle with one colour per vertex (blended across the face).</summary>
    public void Triangle((float X, float Y, float Z) a, (float X, float Y, float Z) b, (float X, float Y, float Z) c, Rgb colorA, Rgb colorB, Rgb colorC, (float X, float Z) flow = default)
    {
        float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        float nx = (uy * vz) - (uz * vy), ny = (uz * vx) - (ux * vz), nz = (ux * vy) - (uy * vx);
        var length = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (length > 0)
        {
            nx /= length;
            ny /= length;
            nz /= length;
        }

        foreach (var ((x, y, z), color) in new[] { (a, colorA), (b, colorB), (c, colorC) })
        {
            Positions.AddRange([x, y, z]);
            Normals.AddRange([nx, ny, nz]);
            Colors.AddRange([color.R, color.G, color.B]);
            Flow.AddRange([flow.X, flow.Z]);
        }
    }
}

/// <summary>
/// Builds low-poly meshes of the island grid per chunk (B2 R18, R20), without Godot. Neighbours meet in
/// smooth slopes (shared corner heights, colours blended across the drop); only real rock cliffs - a
/// step of at least the cliff drop below a cell of a cliff biome (D576) - become vertical walls, whose
/// top edge is the terrace edge itself. Water surfaces are flat quads at bed height plus depth, coloured by kind and depth, with
/// the flow direction the core gave the stream. Deterministic: same map, same meshes. The base grid never
/// changes at run time, so a chunk's meshes are built once per detail level (<see cref="TerrainLevel"/>).
/// </summary>
public static partial class TerrainMesher
{
    // Corner offsets of a cell in the order (x, z), (x + 1, z), (x + 1, z + 1), (x, z + 1).
    private static readonly (int Dx, int Dz)[] Corners = [(0, 0), (1, 0), (1, 1), (0, 1)];

    /// <summary>The ground of one chunk: top faces of every cell plus the cliff walls it owns.</summary>
    public static MeshData Ground(WorldMap map, int chunkX, int chunkZ, TerrainLook look)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        var mesh = new MeshData();
        foreach (var (x, z) in CellsOf(map, chunkX, chunkZ))
        {
            Cell(map, look, mesh, x, z);
        }

        return mesh;
    }

    /// <summary>
    /// The ground of one chunk at a detail level (B2 R18). Level 0 is <see cref="Ground(WorldMap, int, int, TerrainLook)"/>.
    /// A coarser level merges blocks of plain dry land (and of open sea bed) into a few triangles: a block of
    /// <see cref="TerrainLevel.CellStep"/> cells if it passes <see cref="Mergeable"/> (no water in or next to
    /// dry land, no cliff, within the level's height and colour tolerance), else its four half blocks, down
    /// to blocks of 2. Every other cell is drawn exactly as on level 0, so coasts, banks, streams, lakes and
    /// cliffs look the same at every level and the water (always drawn from the base grid) fits. All
    /// vertices are corners of the base grid with their level-0 height. A merged block's border keeps every
    /// base vertex another piece of the chunk uses and every base vertex on the chunk border, so pieces
    /// meet without T-junctions and chunks of any two levels meet without cracks; no skirts are needed.
    /// </summary>
    public static MeshData Ground(TerrainLevel level, int chunkX, int chunkZ)
    {
        ArgumentNullException.ThrowIfNull(level);
        var (map, look, step) = (level.Base, level.Look, level.CellStep);
        if (step == 1)
        {
            return Ground(map, chunkX, chunkZ, look);
        }

        if (chunkX < 0 || chunkZ < 0 || chunkX >= map.ChunksX || chunkZ >= map.ChunksZ)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkX), $"chunk ({chunkX}, {chunkZ}) is outside the map");
        }

        // Pieces of the chunk: merged blocks (size >= 2 with their shape) and single cells (size 1).
        var pieces = new List<(int X0, int Z0, int Size, Shape Shape)>();
        void Split(int x0, int z0, int size)
        {
            var shape = size >= 2 ? Mergeable(map, look, x0, z0, size, level.MaxErrorM, level.MaxColorDelta, level.SeaFloorMarginM) : Shape.None;
            if (shape != Shape.None)
            {
                pieces.Add((x0, z0, size, shape));
            }
            else if (size >= 4 && size % 2 == 0)
            {
                var half = size / 2;
                Split(x0, z0, half);
                Split(x0 + half, z0, half);
                Split(x0, z0 + half, half);
                Split(x0 + half, z0 + half, half);
            }
            else
            {
                for (var z = z0; z < z0 + size; z++)
                {
                    for (var x = x0; x < x0 + size; x++)
                    {
                        pieces.Add((x, z, 1, Shape.None));
                    }
                }
            }
        }

        int cx0 = chunkX * map.ChunkCells, cz0 = chunkZ * map.ChunkCells, n = map.ChunkCells;
        for (var bz = 0; bz < n; bz += step)
        {
            for (var bx = 0; bx < n; bx += step)
            {
                Split(cx0 + bx, cz0 + bz, step);
            }
        }

        // Base vertices a merged border must keep: every piece's corners and the whole chunk border.
        var used = new HashSet<(int X, int Z)>();
        foreach (var (x0, z0, size, _) in pieces)
        {
            used.UnionWith([(x0, z0), (x0 + size, z0), (x0 + size, z0 + size), (x0, z0 + size)]);
        }

        for (var e = 0; e <= n; e++)
        {
            used.UnionWith([(cx0 + e, cz0), (cx0 + e, cz0 + n), (cx0, cz0 + e), (cx0 + n, cz0 + e)]);
        }

        // A merged sea floor must never rise above a base corner. The base vertices its border keeps can
        // lift its fan, so it is checked as drawn; a block that would rise is drawn cell by cell instead,
        // which adds its corners to the kept vertices, so the other sea blocks are checked again.
        for (var again = true; again;)
        {
            again = false;
            for (var i = 0; i < pieces.Count; i++)
            {
                var (x0, z0, size, shape) = pieces[i];
                if (!shape.HasFlag(Shape.Sea) || FanStaysBelowCorners(map, look, x0, z0, size, shape, used))
                {
                    continue;
                }

                pieces.RemoveAt(i--);
                for (var z = z0; z < z0 + size; z++)
                {
                    for (var x = x0; x < x0 + size; x++)
                    {
                        pieces.Add((x, z, 1, Shape.None));
                        used.UnionWith([(x, z), (x + 1, z), (x + 1, z + 1), (x, z + 1)]);
                    }
                }

                again = true;
            }
        }

        var mesh = new MeshData();
        foreach (var (x0, z0, size, shape) in pieces)
        {
            if (size == 1)
            {
                Cell(map, look, mesh, x0, z0);
            }
            else
            {
                Fan(map, look, mesh, x0, z0, size, shape, used);
            }
        }

        return mesh;
    }

    /// <summary>The top faces and owned cliff walls of one base grid cell.</summary>
    private static void Cell(WorldMap map, TerrainLook look, MeshData mesh, int x, int z)
    {
        var h = CornerHeights(map, x, z, look);
        var p = new (float X, float Y, float Z)[4];
        for (var k = 0; k < 4; k++)
        {
            p[k] = ((x + Corners[k].Dx) * look.CellM, h[k] * look.UnitM, (z + Corners[k].Dz) * look.CellM);
        }

        // Corner colours blend the ground colours around each corner, so biome borders are soft lines
        // instead of cell stairs.
        var col = CornerColors(map, x, z, look);

        // A running stream cell is a trough (TL #19755): four triangles down to its centre at the cut bed,
        // so the stream lies below its banks instead of on the slope.
        if (IsTrough(map, x, z))
        {
            var bed = map.Cell(x, z).Height * look.UnitM;
            var centre = (((x + 0.5f) * look.CellM, bed, (z + 0.5f) * look.CellM), look.Bed.Lerp(Mean(col), 0.5f));
            for (var k = 0; k < 4; k++)
            {
                Top(mesh, (p[k], col[k]), centre, (p[(k + 1) % 4], col[(k + 1) % 4]), look);
            }
        }

        // Two triangles, split along the flatter diagonal so ridges stay sharp; seen from above (+y).
        else if (SplitsAlong02(h))
        {
            Top(mesh, (p[0], col[0]), (p[2], col[2]), (p[1], col[1]), look);
            Top(mesh, (p[0], col[0]), (p[3], col[3]), (p[2], col[2]), look);
        }
        else
        {
            Top(mesh, (p[0], col[0]), (p[3], col[3]), (p[1], col[1]), look);
            Top(mesh, (p[1], col[1]), (p[3], col[3]), (p[2], col[2]), look);
        }

        // Cliff walls: this cell owns the wall on each edge where its edge lies above the neighbour's.
        Wall(map, look, mesh, x, z, h, dx: 1, dz: 0);
        Wall(map, look, mesh, x, z, h, dx: -1, dz: 0);
        Wall(map, look, mesh, x, z, h, dx: 0, dz: 1);
        Wall(map, look, mesh, x, z, h, dx: 0, dz: -1);
    }

    /// <summary>
    /// How the block of <paramref name="step"/> × <paramref name="step"/> cells at (x0, z0) may be drawn
    /// (<see cref="Shape.None"/> = not as one piece). A block merges only if every cell is dry with no water
    /// among its eight neighbours (so coasts and banks stay exact) or all of them are open sea (its bed is
    /// hidden under the opaque sea, see <see cref="SeaFloor"/>), every grid corner of the block has no
    /// cliff around it (one height and colour per corner, no walls), and every corner's height and colour
    /// lies within half the tolerance of the fan over the block's four corners and its centre. The two
    /// triangles over one diagonal are allowed where they meet the same test. Base vertices added on
    /// detailed edges change the surface by at most that half again, so the drawn block stays within the
    /// tolerance.
    /// </summary>
    private static Shape Mergeable(WorldMap map, TerrainLook look, int x0, int z0, int step, float maxErrorM, float maxColorDelta, float seaFloorMarginM)
    {
        int dry = 0, salt = 0, all = 0, surface = int.MaxValue;
        for (var z = z0 - 1; z <= z0 + step; z++)
        {
            for (var x = x0 - 1; x <= x0 + step; x++)
            {
                if (map.Contains(x, z))
                {
                    var cell = map.Cell(x, z);
                    (dry, salt, all) = (dry + (cell.Water == WaterKind.None ? 1 : 0), salt + (cell.Water == WaterKind.Salt ? 1 : 0), all + 1);
                    surface = cell.Water == WaterKind.Salt ? Math.Min(surface, cell.Height + cell.WaterDepth) : surface;
                }
            }
        }

        // Open sea bed: hidden under the (unchanged) opaque sea, if it lies clearly below the surface.
        var underSea = salt == all;
        if (!underSea && dry != all)
        {
            return Shape.None;
        }

        var corners = new (int H, Rgb C)[step + 1, step + 1];
        for (var j = 0; j <= step; j++)
        {
            for (var i = 0; i <= step; i++)
            {
                if (!PlainCorner(map, look, x0 + i, z0 + j, out corners[i, j].H, out corners[i, j].C))
                {
                    return Shape.None;
                }
            }
        }

        bool Within(Func<int, int, (float H, Rgb C)> surface)
        {
            for (var j = 0; j <= step; j++)
            {
                for (var i = 0; i <= step; i++)
                {
                    var (h, c) = surface(i, j);
                    var (gh, gc) = corners[i, j];
                    if (MathF.Abs(gh - h) * look.UnitM > maxErrorM / 2
                        || MathF.Abs(gc.R - c.R) > maxColorDelta / 2
                        || MathF.Abs(gc.G - c.G) > maxColorDelta / 2
                        || MathF.Abs(gc.B - c.B) > maxColorDelta / 2)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        if (underSea)
        {
            return SeaFloor(corners, step, look, (surface * look.UnitM) - seaFloorMarginM, maxErrorM);
        }

        var centre = Centre(corners, step);
        if (!Within((i, j) => FanAt(corners, centre, step, i, j)))
        {
            return Shape.None;
        }

        var shape = Shape.Fan;
        if (Within((i, j) => DiagonalAt(corners, step, i, j, along02: true)))
        {
            shape |= Shape.Diagonal02;
        }

        if (Within((i, j) => DiagonalAt(corners, step, i, j, along02: false)))
        {
            shape |= Shape.Diagonal13;
        }

        return shape;
    }

    /// <summary>
    /// How a block of open sea bed may be drawn: only if every corner lies at least the margin below the
    /// sea surface (<paramref name="topM"/> = surface minus margin), so the merged floor never comes near the
    /// water and cannot show through it, and only in shapes that never raise the floor above any corner of
    /// the base grid and lower it by at most half the height tolerance: the sea shader colours its bands by
    /// the depth below the surface, so a deeper floor would change the sea's colour at the level switch.
    /// </summary>
    private static Shape SeaFloor((int H, Rgb C)[,] corners, int step, TerrainLook look, float topM, float maxErrorM)
    {
        for (var j = 0; j <= step; j++)
        {
            for (var i = 0; i <= step; i++)
            {
                if (corners[i, j].H * look.UnitM > topM)
                {
                    return Shape.None;
                }
            }
        }

        bool Below(Func<int, int, (float H, Rgb C)> surface)
        {
            for (var j = 0; j <= step; j++)
            {
                for (var i = 0; i <= step; i++)
                {
                    var h = surface(i, j).H;
                    if (h > corners[i, j].H + 1e-3f || (corners[i, j].H - h) * look.UnitM > maxErrorM / 2)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        var centre = Centre(corners, step);
        if (!Below((i, j) => FanAt(corners, centre, step, i, j)))
        {
            return Shape.None;
        }

        var shape = Shape.Fan | Shape.Sea;
        shape |= Below((i, j) => DiagonalAt(corners, step, i, j, along02: true)) ? Shape.Diagonal02 : Shape.None;
        shape |= Below((i, j) => DiagonalAt(corners, step, i, j, along02: false)) ? Shape.Diagonal13 : Shape.None;
        return shape;
    }

    /// <summary>
    /// Height and colour of the grid corner (gx, gz) if all cells around it form one group (no rock wall
    /// meets there): then every cell around it gives the corner the same height (<see cref="CornerHeights(WorldMap, int, int, TerrainLook)"/>)
    /// and colour (<see cref="CornerColors"/>).
    /// </summary>
    private static bool PlainCorner(WorldMap map, TerrainLook look, int gx, int gz, out int height, out Rgb color)
    {
        var (members, all) = CornerGroup(map, look, gx, gz, -1);
        height = GroupHeight(map, gx, gz, members);
        color = GroupColor(map, look, gx, gz, members);
        return members == all;
    }

    /// <summary>The fan's centre: the grid corner in the middle of the block (even steps) or the mean of its four corners.</summary>
    private static (float H, Rgb C) Centre((int H, Rgb C)[,] k, int step)
    {
        if (step % 2 == 0)
        {
            return k[step / 2, step / 2];
        }

        var color = k[0, 0].C.Lerp(k[step, 0].C, 0.5f).Lerp(k[0, step].C.Lerp(k[step, step].C, 0.5f), 0.5f);
        return ((k[0, 0].H + k[step, 0].H + k[0, step].H + k[step, step].H) / 4f, color);
    }

    /// <summary>Height and colour of the four-triangle fan (corners and centre) at the corner (i, j) of the block.</summary>
    private static (float H, Rgb C) FanAt((int H, Rgb C)[,] k, (float H, Rgb C) centre, int step, int i, int j)
    {
        float m = step / 2f, u = i - m, v = j - m;

        // The triangle of the edge the point faces: left (i = 0), right (i = step), front (j = 0) or back (j = step).
        var ((ai, aj), (bi, bj)) = MathF.Abs(u) >= MathF.Abs(v)
            ? (u <= 0 ? ((0, 0), (0, step)) : ((step, 0), (step, step)))
            : (v <= 0 ? ((0, 0), (step, 0)) : ((0, step), (step, step)));
        return Blend((m, m, centre.H, centre.C), (ai, aj, k[ai, aj].H, k[ai, aj].C), (bi, bj, k[bi, bj].H, k[bi, bj].C), i, j);
    }

    /// <summary>Height and colour of the two triangles over one diagonal (0–2 or 1–3) at the corner (i, j) of the block.</summary>
    private static (float H, Rgb C) DiagonalAt((int H, Rgb C)[,] k, int step, int i, int j, bool along02)
    {
        (float, float, float, Rgb) P(int a, int b) => (a, b, k[a, b].H, k[a, b].C);
        var s = step;
        return along02
            ? (i >= j ? Blend(P(0, 0), P(s, 0), P(s, s), i, j) : Blend(P(0, 0), P(0, s), P(s, s), i, j))
            : (i + j <= s ? Blend(P(0, 0), P(s, 0), P(0, s), i, j) : Blend(P(s, s), P(0, s), P(s, 0), i, j));
    }

    /// <summary>Barycentric blend of height and colour of a triangle (given in block corner coordinates) at (i, j).</summary>
    private static (float H, Rgb C) Blend((float I, float J, float H, Rgb C) a, (float I, float J, float H, Rgb C) b, (float I, float J, float H, Rgb C) c, float i, float j)
    {
        var det = ((b.J - c.J) * (a.I - c.I)) + ((c.I - b.I) * (a.J - c.J));
        var wa = (((b.J - c.J) * (i - c.I)) + ((c.I - b.I) * (j - c.J))) / det;
        var wb = (((c.J - a.J) * (i - c.I)) + ((a.I - c.I) * (j - c.J))) / det;
        var wc = 1 - wa - wb;
        return (
            (wa * a.H) + (wb * b.H) + (wc * c.H),
            new Rgb((wa * a.C.R) + (wb * b.C.R) + (wc * c.C.R), (wa * a.C.G) + (wb * b.C.G) + (wc * c.C.G), (wa * a.C.B) + (wb * b.C.B) + (wc * c.C.B)));
    }

    /// <summary>
    /// A merged block. Its border runs counter-clockwise seen from above through the four corners and every
    /// base vertex on its edges that <paramref name="used"/> holds (left x = x0, back z = z0 + step, right,
    /// front). Where a
    /// diagonal is allowed and both edges at one of its corners are plain, the border is fanned from that
    /// corner (two triangles plus one per extra vertex on the far edges); otherwise from the centre.
    /// </summary>
    private static void Fan(WorldMap map, TerrainLook look, MeshData mesh, int x0, int z0, int step, Shape shape, HashSet<(int X, int Z)> used)
    {
        var k = new (int H, Rgb C)[step + 1, step + 1];
        for (var j = 0; j <= step; j++)
        {
            for (var i = 0; i <= step; i++)
            {
                PlainCorner(map, look, x0 + i, z0 + j, out k[i, j].H, out k[i, j].C);
            }
        }

        var ring = new List<(int I, int J)>();
        bool Edge(Func<int, (int I, int J)> at)
        {
            var detailed = false;
            for (var e = 0; e < step; e++)
            {
                var (i, j) = at(e);
                if (e == 0 || used.Contains((x0 + i, z0 + j)))
                {
                    ring.Add((i, j));
                    detailed |= e > 0;
                }
            }

            return detailed;
        }

        // Corners in the ring: (0, 0) first, then (0, step), (step, step), (step, 0) after each edge.
        var detailed = (Left: Edge(e => (0, e)), Back: Edge(e => (e, step)), Right: Edge(e => (step, step - e)), Front: Edge(e => (step - e, 0)));

        ((float X, float Y, float Z) P, Rgb C) Vertex((int I, int J) v) =>
            (((x0 + v.I) * look.CellM, k[v.I, v.J].H * look.UnitM, (z0 + v.J) * look.CellM), k[v.I, v.J].C);
        (int, int)? apex =
            shape.HasFlag(Shape.Diagonal02) && !detailed.Left && !detailed.Front ? (0, 0)
            : shape.HasFlag(Shape.Diagonal02) && !detailed.Back && !detailed.Right ? (step, step)
            : shape.HasFlag(Shape.Diagonal13) && !detailed.Left && !detailed.Back ? (0, step)
            : shape.HasFlag(Shape.Diagonal13) && !detailed.Right && !detailed.Front ? (step, 0)
            : null;
        if (apex is { } a)
        {
            // Both ring neighbours of the apex are corners (its edges are plain), so every other pair of the
            // ring with the apex is a proper triangle.
            var at = ring.IndexOf(a);
            for (var e = 1; e < ring.Count - 1; e++)
            {
                Top(mesh, Vertex(a), Vertex(ring[(at + e) % ring.Count]), Vertex(ring[(at + e + 1) % ring.Count]), look);
            }

            return;
        }

        var (centreH, centreC) = Centre(k, step);
        var centre = (((x0 + (step / 2f)) * look.CellM, centreH * look.UnitM, (z0 + (step / 2f)) * look.CellM), centreC);
        for (var e = 0; e < ring.Count; e++)
        {
            Top(mesh, centre, Vertex(ring[e]), Vertex(ring[(e + 1) % ring.Count]), look);
        }
    }

    /// <summary>Whether the merged block, drawn with the border vertices <paramref name="used"/> keeps, lies at or below every base corner it covers.</summary>
    private static bool FanStaysBelowCorners(WorldMap map, TerrainLook look, int x0, int z0, int step, Shape shape, HashSet<(int X, int Z)> used)
    {
        var drawn = new MeshData();
        Fan(map, look, drawn, x0, z0, step, shape, used);
        var p = drawn.Positions;
        for (var j = 0; j <= step; j++)
        {
            for (var i = 0; i <= step; i++)
            {
                PlainCorner(map, look, x0 + i, z0 + j, out var h, out _);
                float x = (x0 + i) * look.CellM, z = (z0 + j) * look.CellM, top = (h * look.UnitM) + 1e-4f;
                for (var t = 0; t < drawn.TriangleCount; t++)
                {
                    int a = t * 9, b = a + 3, c = a + 6;
                    var det = ((p[b + 2] - p[c + 2]) * (p[a] - p[c])) + ((p[c] - p[b]) * (p[a + 2] - p[c + 2]));
                    var wa = (((p[b + 2] - p[c + 2]) * (x - p[c])) + ((p[c] - p[b]) * (z - p[c + 2]))) / det;
                    var wb = (((p[c + 2] - p[a + 2]) * (x - p[c])) + ((p[a] - p[c]) * (z - p[c + 2]))) / det;
                    if (wa >= -1e-4f && wb >= -1e-4f && wa + wb <= 1 + 1e-4f && (wa * p[a + 1]) + (wb * p[b + 1]) + ((1 - wa - wb) * p[c + 1]) > top)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The water surfaces of one chunk (sea, lakes, swamp), optionally of one kind only. The sea is flat
    /// per cell. Fresh water is drawn corner by corner (see <see cref="FreshCorner"/>), so lakes, swamp and
    /// their banks share one surface at every grid corner and close against dry ground. Running water is
    /// not drawn here but as smooth bands along the stream lines (<see cref="Streams"/>, A1456).
    /// </summary>
    public static MeshData Water(WorldMap map, int chunkX, int chunkZ, TerrainLook look, WaterKind? only = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        var mesh = new MeshData();
        foreach (var (x, z) in CellsOf(map, chunkX, chunkZ))
        {
            var cell = map.Cell(x, z);
            if (cell.Water == WaterKind.None)
            {
                // Banks: a dry cell next to still fresh water carries that water's surface too, where the
                // shore line crosses it. The ground above the surface hides it, so the visible shore
                // follows the slope (smooth, like the coast) instead of the cell edges.
                if (only is null or WaterKind.Fresh && Carry(map, x, z, look) is { Kind: Carrier.Bank } bank)
                {
                    Fresh(map, mesh, x, z, bank.Depth <= look.SwampMaxUnits ? look.Swamp : look.Fresh, default, look);
                }

                continue;
            }

            if (only is { } kind && cell.Water != kind)
            {
                continue;
            }

            if (cell.Water == WaterKind.Salt)
            {
                AddQuad(mesh, x, z, (cell.Height + cell.WaterDepth) * look.UnitM, cell.WaterDepth >= look.DeepFromUnits ? look.SeaDeep : look.SeaShallow, default, look);
                continue;
            }

            if (cell.Flow >= 0)
            {
                // A stream: drawn as a band along its line (Streams), not as square cell plates.
                continue;
            }

            Fresh(map, mesh, x, z, cell.WaterDepth <= look.SwampMaxUnits ? look.Swamp : look.Fresh, default, look);
        }

        return mesh;
    }

    /// <summary>
    /// Shore test of a dry cell next to still water at <paramref name="surface"/>: the shore line crosses
    /// the cell only where the surface lies between its lowest and highest ground corner. A bank wholly
    /// above the water stays dry, and one wholly below would look flooded, so neither carries a plate.
    /// </summary>
    public static bool IsShore(int[] ground, int surface)
    {
        ArgumentNullException.ThrowIfNull(ground);
        return ground.Min() < surface && ground.Max() > surface;
    }

    /// <summary>
    /// Height in metres of the ground mesh at (x, z) in metres: the cell's top triangles interpolated
    /// exactly as <see cref="Ground(WorldMap, int, int, TerrainLook)"/> splits them (same corner heights, same diagonal). Positions outside
    /// the map are clamped to its edge. For placing figures and objects and for the camera target.
    /// </summary>
    public static float GroundHeight(WorldMap map, TerrainLook look, double x, double z)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        var fx = Math.Clamp(x / look.CellM, 0, map.WidthCells);
        var fz = Math.Clamp(z / look.CellM, 0, map.DepthCells);
        var cx = Math.Min((int)Math.Floor(fx), map.WidthCells - 1);
        var cz = Math.Min((int)Math.Floor(fz), map.DepthCells - 1);
        double u = fx - cx, v = fz - cz;
        // Allocation free: called for every figure, object and camera sample.
        Span<int> h = stackalloc int[4];
        CornerHeights(map, cx, cz, look, h);
        double height;
        if (IsTrough(map, cx, cz))
        {
            height = TroughHeight(h, map.Cell(cx, cz).Height, u, v);
        }
        else if (SplitsAlong02(h))
        {
            // Triangles (0, 2, 1) where u >= v and (0, 3, 2) where v >= u.
            height = u >= v
                ? h[0] + (u * (h[1] - h[0])) + (v * (h[2] - h[1]))
                : h[0] + (v * (h[3] - h[0])) + (u * (h[2] - h[3]));
        }
        else
        {
            // Triangles (0, 3, 1) where u + v <= 1 and (1, 3, 2) where u + v >= 1.
            height = u + v <= 1
                ? h[0] + (u * (h[1] - h[0])) + (v * (h[3] - h[0]))
                : h[2] + ((1 - u) * (h[3] - h[2])) + ((1 - v) * (h[1] - h[2]));
        }

        return (float)(height * look.UnitM);
    }

    /// <summary>
    /// Whether a cell is drawn as a trough: running fresh water whose bed the core cut into the ground, with
    /// running water coming in. A source (a spring) is no trough but a soft seep in the ground, so a
    /// stream does not start with a pit (TL #19722 polish).
    /// </summary>
    public static bool IsTrough(WorldMap map, int x, int z)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Cell(x, z) is not { Water: WaterKind.Fresh, Flow: >= 0 })
        {
            return false;
        }

        for (var k = 0; k < FlowDirections.All.Count; k++)
        {
            var (dx, dz) = FlowDirections.All[k];
            if (map.Contains(x - dx, z - dz) && map.Cell(x - dx, z - dz) is { Water: WaterKind.Fresh, Flow: >= 0 } up && up.Flow == k)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Height (units) in a trough cell at (u, v) within the cell: the fan of four triangles from the cell's
    /// corners (in <see cref="Corners"/> order) to its centre at the bed, as <see cref="Ground(WorldMap, int, int, TerrainLook)"/> draws it.
    /// </summary>
    private static double TroughHeight(ReadOnlySpan<int> h, int bed, double u, double v)
    {
        // The triangle with the cell edge k: south (v small), east, north, west; centre at (0.5, 0.5).
        var k = v <= u && v <= 1 - u ? 0 : u >= v && u >= 1 - v ? 1 : v >= u && v >= 1 - u ? 2 : 3;
        var (ax, az) = Corners[k];
        var (bx, bz) = Corners[(k + 1) % 4];
        double ha = h[k], hb = h[(k + 1) % 4];

        // Barycentric weights of (u, v) in the triangle a, b, centre.
        double cx = 0.5, cz = 0.5;
        var det = ((bz - cz) * (ax - cx)) + ((cx - bx) * (az - cz));
        var wa = (((bz - cz) * (u - cx)) + ((cx - bx) * (v - cz))) / det;
        var wb = (((cz - az) * (u - cx)) + ((ax - cx) * (v - cz))) / det;
        return (wa * ha) + (wb * hb) + ((1 - wa - wb) * bed);
    }

    private static Rgb Mean(Rgb[] colors) =>
        new(colors.Average(c => c.R), colors.Average(c => c.G), colors.Average(c => c.B));

    /// <summary>One fresh water cell (lake, stream, swamp or bank), its corners from <see cref="FreshCorner"/>, split like the ground below.</summary>
    private static void Fresh(WorldMap map, MeshData mesh, int x, int z, Rgb color, (float X, float Z) flow, TerrainLook look)
    {
        var h = CornerHeights(map, x, z, look);
        var p = new (float X, float Y, float Z)[4];
        for (var k = 0; k < 4; k++)
        {
            p[k] = ((x + Corners[k].Dx) * look.CellM, FreshCorner(map, x + Corners[k].Dx, z + Corners[k].Dz, h[k], look), (z + Corners[k].Dz) * look.CellM);
        }

        if (SplitsAlong02(h))
        {
            mesh.Triangle(p[0], p[2], p[1], color, flow);
            mesh.Triangle(p[0], p[3], p[2], color, flow);
        }
        else
        {
            mesh.Triangle(p[0], p[3], p[1], color, flow);
            mesh.Triangle(p[1], p[3], p[2], color, flow);
        }
    }

    /// <summary>
    /// Height in metres of fresh water at the grid corner (cx, cz) over the ground corner height
    /// <paramref name="ground"/> (units), the same for every water cell around the corner, so surfaces
    /// meet without steps. Still water (lake, swamp, sea, the plate of a bank) gives its surface; running
    /// water follows the slope at ground plus depth, so a stream is a ribbon down the hill instead of a
    /// stair of flat plates; the higher of the two wins. Where the corner touches bare dry ground the
    /// water comes down to that ground plus the shore lift, so its edge closes against the bank.
    /// </summary>
    private static float FreshCorner(WorldMap map, int cx, int cz, int ground, TerrainLook look)
    {
        var surface = int.MinValue;
        var bare = false;
        for (var oz = -1; oz <= 0; oz++)
        {
            for (var ox = -1; ox <= 0; ox++)
            {
                int nx = cx + ox, nz = cz + oz;
                if (!map.Contains(nx, nz))
                {
                    continue;
                }

                var carry = Carry(map, nx, nz, look);
                switch (carry.Kind)
                {
                    case Carrier.Bare:
                        bare = true;
                        break;
                    case Carrier.Running:
                        surface = Math.Max(surface, ground + carry.Depth);
                        break;
                    default:
                        surface = Math.Max(surface, carry.Surface);
                        break;
                }
            }
        }

        var y = surface * look.UnitM;
        return bare ? MathF.Min(y, (ground * look.UnitM) + look.ShoreLiftM) : y;
    }

    /// <summary>What a cell carries for the fresh water surface: nothing, a bank plate, still or running water.</summary>
    private static (Carrier Kind, int Surface, int Depth) Carry(WorldMap map, int x, int z, TerrainLook look)
    {
        var cell = map.Cell(x, z);
        if (cell.Water != WaterKind.None)
        {
            return (cell.Water == WaterKind.Fresh && cell.Flow >= 0 ? Carrier.Running : Carrier.Still, cell.Height + cell.WaterDepth, cell.WaterDepth);
        }

        return BankWater(map, x, z) is { } bank && IsShore(CornerHeights(map, x, z, look), bank.Surface)
            ? (Carrier.Bank, bank.Surface, bank.Depth)
            : (Carrier.Bare, 0, 0);
    }

    /// <summary>Whether a cell's top is split along the diagonal from corner 0 to corner 2 (the flatter one, so ridges stay sharp).</summary>
    private static bool SplitsAlong02(ReadOnlySpan<int> h) => Math.Abs(h[0] - h[2]) <= Math.Abs(h[1] - h[3]);

    private static void AddQuad(MeshData mesh, int x, int z, float y, Rgb color, (float X, float Z) flow, TerrainLook look)
    {
        float x0 = x * look.CellM, x1 = (x + 1) * look.CellM, z0 = z * look.CellM, z1 = (z + 1) * look.CellM;
        mesh.Triangle((x0, y, z0), (x1, y, z1), (x1, y, z0), color, flow);
        mesh.Triangle((x0, y, z0), (x0, y, z1), (x1, y, z1), color, flow);
    }

    /// <summary>The highest still fresh water surface (and its depth) among the eight neighbours of a dry cell, if any.</summary>
    private static (int Surface, int Depth)? BankWater(WorldMap map, int x, int z)
    {
        (int Surface, int Depth)? best = null;
        foreach (var (dx, dz) in FlowDirections.All)
        {
            int nx = x + dx, nz = z + dz;
            if (!map.Contains(nx, nz))
            {
                continue;
            }

            // Only still water (lakes, swamp) reaches into its banks; a stream runs down a slope and
            // closes against its banks by itself.
            var n = map.Cell(nx, nz);
            if (n.Water == WaterKind.Fresh && n.Flow < 0 && (best is null || n.Height + n.WaterDepth > best.Value.Surface))
            {
                best = (n.Height + n.WaterDepth, n.WaterDepth);
            }
        }

        return best;
    }

    /// <summary>
    /// Corner heights of a cell in units: each corner is the mean of the cells around it that form one
    /// group with this cell (<see cref="CornerGroup"/>). Cells of one group share the corner exactly, so
    /// slopes are continuous; only a rock wall separates groups and stays open as a cliff.
    /// </summary>
    public static int[] CornerHeights(WorldMap map, int x, int z, TerrainLook look)
    {
        var result = new int[4];
        CornerHeights(map, x, z, look, result);
        return result;
    }

    /// <summary>The corner heights of a cell (see <see cref="CornerHeights(WorldMap, int, int, TerrainLook)"/>) into four given slots, without allocating.</summary>
    public static void CornerHeights(WorldMap map, int x, int z, TerrainLook look, Span<int> into)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        for (var k = 0; k < 4; k++)
        {
            int gx = x + Corners[k].Dx, gz = z + Corners[k].Dz;
            into[k] = GroupHeight(map, gx, gz, CornerGroup(map, look, gx, gz, Slot(gx, gz, x, z)).Members);
        }
    }

    /// <summary>
    /// Whether two neighbouring cells meet in a slope (true) or are a rock wall apart (false): a wall needs
    /// a height difference of at least the cliff drop and a higher cell of a cliff biome (D576).
    /// </summary>
    public static bool Joined(MapCell a, MapCell b, TerrainLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        if (Math.Abs(a.Height - b.Height) < look.CliffDrop)
        {
            return true;
        }

        var higher = a.Height >= b.Height ? a : b;
        return look.CliffBiomes is { } rock && !rock[higher.Biome];
    }

    /// <summary>Slot (bit index) of the cell (x, z) among the four cells around the grid corner (gx, gz).</summary>
    private static int Slot(int gx, int gz, int x, int z) => ((z - gz + 1) * 2) + (x - gx + 1);

    /// <summary>
    /// The cells around the grid corner (gx, gz) (bits by <see cref="Slot"/>) that form one group with the
    /// cell in <paramref name="slot"/> (or with the first present cell for -1): cells joined directly or over
    /// other cells of the corner, diagonals included (<see cref="Joined"/>). The group is the same for each
    /// of its cells, so they share the corner height exactly. Also returns every present cell.
    /// </summary>
    private static (int Members, int All) CornerGroup(WorldMap map, TerrainLook look, int gx, int gz, int slot)
    {
        Span<int> parent = [0, 1, 2, 3];
        Span<MapCell> cells = stackalloc MapCell[4];
        var all = 0;
        for (var s = 0; s < 4; s++)
        {
            int x = gx - 1 + (s & 1), z = gz - 1 + (s >> 1);
            if (map.Contains(x, z))
            {
                cells[s] = map.Cell(x, z);
                all |= 1 << s;
            }
        }

        for (var a = 0; a < 4; a++)
        {
            for (var b = a + 1; b < 4; b++)
            {
                if ((all & (1 << a)) != 0 && (all & (1 << b)) != 0 && Joined(cells[a], cells[b], look))
                {
                    parent[Root(parent, b)] = Root(parent, a);
                }
            }
        }

        var root = Root(parent, slot < 0 ? System.Numerics.BitOperations.TrailingZeroCount(all) : slot);
        var members = 0;
        for (var s = 0; s < 4; s++)
        {
            if ((all & (1 << s)) != 0 && Root(parent, s) == root)
            {
                members |= 1 << s;
            }
        }

        return (members, all);

        static int Root(Span<int> parent, int i)
        {
            while (parent[i] != i)
            {
                i = parent[i];
            }

            return i;
        }
    }

    /// <summary>Rounded mean height (units, exact integers on every platform) of the group's cells around the grid corner.</summary>
    private static int GroupHeight(WorldMap map, int gx, int gz, int members)
    {
        int sum = 0, count = 0;
        for (var s = 0; s < 4; s++)
        {
            if ((members & (1 << s)) != 0)
            {
                sum += map.Cell(gx - 1 + (s & 1), gz - 1 + (s >> 1)).Height;
                count++;
            }
        }

        return (int)Math.Round((double)sum / count, MidpointRounding.AwayFromZero);
    }

    /// <summary>Mean ground colour of the group's cells around the grid corner, so biome borders blend across slopes.</summary>
    private static Rgb GroupColor(WorldMap map, TerrainLook look, int gx, int gz, int members)
    {
        float r = 0, g = 0, b = 0;
        var count = 0;
        for (var s = 0; s < 4; s++)
        {
            if ((members & (1 << s)) != 0)
            {
                var c = GroundColor(map.Cell(gx - 1 + (s & 1), gz - 1 + (s >> 1)), look);
                (r, g, b, count) = (r + c.R, g + c.G, b + c.B, count + 1);
            }
        }

        return new Rgb(r / count, g / count, b / count);
    }

    /// <summary>The water surfaces of one chunk at a detail level: always those of the base grid, so they fit the ground of every level.</summary>
    public static MeshData Water(TerrainLevel level, int chunkX, int chunkZ, WaterKind? only = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        return Water(level.Base, chunkX, chunkZ, level.Look, only);
    }

    private static IEnumerable<(int X, int Z)> CellsOf(WorldMap map, int chunkX, int chunkZ)
    {
        if (chunkX < 0 || chunkZ < 0 || chunkX >= map.ChunksX || chunkZ >= map.ChunksZ)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkX), $"chunk ({chunkX}, {chunkZ}) is outside the map");
        }

        for (var z = chunkZ * map.ChunkCells; z < (chunkZ + 1) * map.ChunkCells; z++)
        {
            for (var x = chunkX * map.ChunkCells; x < (chunkX + 1) * map.ChunkCells; x++)
            {
                yield return (x, z);
            }
        }
    }

    private static Rgb GroundColor(MapCell cell, TerrainLook look) => cell.Water == WaterKind.None ? look.BiomeColors[cell.Biome] : look.Bed;

    private static Rgb[] CornerColors(WorldMap map, int x, int z, TerrainLook look)
    {
        var result = new Rgb[4];
        for (var k = 0; k < 4; k++)
        {
            int gx = x + Corners[k].Dx, gz = z + Corners[k].Dz;
            result[k] = GroupColor(map, look, gx, gz, CornerGroup(map, look, gx, gz, Slot(gx, gz, x, z)).Members);
        }

        return result;
    }

    private static void Top(MeshData mesh, ((float X, float Y, float Z) P, Rgb C) va, ((float X, float Y, float Z) P, Rgb C) vb, ((float X, float Y, float Z) P, Rgb C) vc, TerrainLook look)
    {
        var (a, b, c) = (va.P, vb.P, vc.P);
        // Steeper faces lean towards the cliff colour (normal.y = 1 flat, 0 vertical).
        float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        float nx = (uy * vz) - (uz * vy), ny = (uz * vx) - (ux * vz), nz = (ux * vy) - (uy * vx);
        var up = ny / MathF.Max(1e-6f, MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz)));
        var steep = Math.Clamp((1f - up) * look.SlopeShadeGain, 0f, 1f) * look.SlopeShade;
        mesh.Triangle(a, b, c, va.C.Lerp(look.Cliff, steep), vb.C.Lerp(look.Cliff, steep), vc.C.Lerp(look.Cliff, steep));
    }

    private static void Wall(WorldMap map, TerrainLook look, MeshData mesh, int x, int z, int[] h, int dx, int dz)
    {
        int nx = x + dx, nz = z + dz;
        if (!map.Contains(nx, nz))
        {
            return;
        }

        var other = CornerHeights(map, nx, nz, look);

        // Shared edge corners: (index in this cell, index in the neighbour) for both ends.
        var (a, b, oa, ob) = (dx, dz) switch
        {
            (1, 0) => (1, 2, 0, 3),
            (-1, 0) => (3, 0, 2, 1),
            (0, 1) => (2, 3, 1, 0),
            _ => (0, 1, 3, 2),
        };
        if (h[a] <= other[oa] && h[b] <= other[ob])
        {
            return;
        }

        float ax = (x + Corners[a].Dx) * look.CellM, az = (z + Corners[a].Dz) * look.CellM;
        float bx = (x + Corners[b].Dx) * look.CellM, bz = (z + Corners[b].Dz) * look.CellM;
        (float, float, float) topA = (ax, h[a] * look.UnitM, az), topB = (bx, h[b] * look.UnitM, bz);
        (float, float, float) lowA = (ax, Math.Min(h[a], other[oa]) * look.UnitM, az), lowB = (bx, Math.Min(h[b], other[ob]) * look.UnitM, bz);

        // Facing the neighbour (outwards); the edges a→b run around the cell in one sense for all four sides.
        // Where the wall runs out at one end (a slope meets it there) it is one triangle, never a sliver.
        if (h[a] > other[oa])
        {
            mesh.Triangle(topA, lowB, lowA, look.Cliff);
        }

        if (h[b] > other[ob])
        {
            mesh.Triangle(topA, topB, lowB, look.Cliff);
        }
    }

    /// <summary>How a block of a coarser level may be drawn.</summary>
    [Flags]
    private enum Shape
    {
        None = 0,
        Fan = 1,
        Diagonal02 = 2,
        Diagonal13 = 4,
        Sea = 8,
    }

    private enum Carrier
    {
        Bare,
        Bank,
        Still,
        Running,
    }
}
