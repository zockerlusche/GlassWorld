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
/// <param name="CellM">Edge length of a cell in metres.</param>
/// <param name="UnitM">One height unit in metres.</param>
/// <param name="CliffDrop">Height difference (units) from which neighbours form a cliff instead of a slope.</param>
/// <param name="ShoreLiftM">How far a stream's edge lies above the dry bank (metres), so it never sinks into it.</param>
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
    float CellM,
    float UnitM,
    int CliffDrop,
    float ShoreLiftM)
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
            preset.Grid.CellSizeCm / 100f,
            preset.Grid.HeightUnitMm / 1000f,
            preset.Terrain.CliffMinDrop,
            terrain.Water.StreamShoreLiftMm / 1000f);
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
/// Builds low-poly meshes of the island grid per chunk (B2 R18, R20), without Godot. Slopes between
/// neighbours closer than the cliff drop are smooth (shared corner heights); larger steps become vertical
/// cliff walls. Water surfaces are flat quads at bed height plus depth, coloured by kind and depth, with
/// the flow direction the core gave the stream. Deterministic: same map, same meshes.
/// </summary>
public static class TerrainMesher
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
            var cell = map.Cell(x, z);
            var h = CornerHeights(map, x, z, look.CliffDrop);
            var p = new (float X, float Y, float Z)[4];
            for (var k = 0; k < 4; k++)
            {
                p[k] = ((x + Corners[k].Dx) * look.CellM, h[k] * look.UnitM, (z + Corners[k].Dz) * look.CellM);
            }

            // Corner colours blend the ground colours around each corner, so biome borders are soft lines
            // instead of cell stairs.
            var col = CornerColors(map, x, z, look);

            // Two triangles, split along the flatter diagonal so ridges stay sharp; seen from above (+y).
            if (SplitsAlong02(h))
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

        return mesh;
    }

    /// <summary>
    /// The water surfaces of one chunk (sea, lakes, streams, swamp), optionally of one kind only. The sea
    /// is flat per cell. Fresh water is drawn corner by corner (see <see cref="FreshCorner"/>), so lakes,
    /// streams, swamp and their banks share one surface at every grid corner and close against dry ground.
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
                if (only is null or WaterKind.Fresh && Carry(map, x, z, look.CliffDrop) is { Kind: Carrier.Bank } bank)
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

            var flow = cell.Flow >= 0 && cell.Flow < FlowDirections.All.Count
                ? Normalised(FlowDirections.All[cell.Flow])
                : default;
            Fresh(map, mesh, x, z, cell.WaterDepth <= look.SwampMaxUnits ? look.Swamp : look.Fresh, flow, look);
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
    /// exactly as <see cref="Ground"/> splits them (same corner heights, same diagonal). Positions outside
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
        var h = CornerHeights(map, cx, cz, look.CliffDrop);
        double height;
        if (SplitsAlong02(h))
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

    /// <summary>One fresh water cell (lake, stream, swamp or bank), its corners from <see cref="FreshCorner"/>, split like the ground below.</summary>
    private static void Fresh(WorldMap map, MeshData mesh, int x, int z, Rgb color, (float X, float Z) flow, TerrainLook look)
    {
        var h = CornerHeights(map, x, z, look.CliffDrop);
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

                var carry = Carry(map, nx, nz, look.CliffDrop);
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
    private static (Carrier Kind, int Surface, int Depth) Carry(WorldMap map, int x, int z, int cliffDrop)
    {
        var cell = map.Cell(x, z);
        if (cell.Water != WaterKind.None)
        {
            return (cell.Water == WaterKind.Fresh && cell.Flow >= 0 ? Carrier.Running : Carrier.Still, cell.Height + cell.WaterDepth, cell.WaterDepth);
        }

        return BankWater(map, x, z) is { } bank && IsShore(CornerHeights(map, x, z, cliffDrop), bank.Surface)
            ? (Carrier.Bank, bank.Surface, bank.Depth)
            : (Carrier.Bare, 0, 0);
    }

    /// <summary>Whether a cell's top is split along the diagonal from corner 0 to corner 2 (the flatter one, so ridges stay sharp).</summary>
    private static bool SplitsAlong02(int[] h) => Math.Abs(h[0] - h[2]) <= Math.Abs(h[1] - h[3]);

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
    /// Corner heights of a cell in units: each corner is the mean of the cells around it that lie within
    /// the cliff drop of this cell (the same plateau), so slopes are shared and cliffs stay open.
    /// </summary>
    public static int[] CornerHeights(WorldMap map, int x, int z, int cliffDrop)
    {
        ArgumentNullException.ThrowIfNull(map);
        var own = map.Cell(x, z).Height;
        var result = new int[4];
        for (var k = 0; k < 4; k++)
        {
            int sum = 0, count = 0;
            for (var oz = -1; oz <= 0; oz++)
            {
                for (var ox = -1; ox <= 0; ox++)
                {
                    int nx = x + Corners[k].Dx + ox, nz = z + Corners[k].Dz + oz;
                    if (!map.Contains(nx, nz))
                    {
                        continue;
                    }

                    var h = map.Cell(nx, nz).Height;
                    if (Math.Abs(h - own) < cliffDrop)
                    {
                        sum += h;
                        count++;
                    }
                }
            }

            // Rounded mean in integers (exact on every platform).
            result[k] = count == 0 ? own : (int)Math.Round((double)sum / count, MidpointRounding.AwayFromZero);
        }

        return result;
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
        var own = map.Cell(x, z);
        var result = new Rgb[4];
        for (var k = 0; k < 4; k++)
        {
            float r = 0, g = 0, b = 0;
            var count = 0;
            for (var oz = -1; oz <= 0; oz++)
            {
                for (var ox = -1; ox <= 0; ox++)
                {
                    int nx = x + Corners[k].Dx + ox, nz = z + Corners[k].Dz + oz;
                    if (!map.Contains(nx, nz) || Math.Abs(map.Cell(nx, nz).Height - own.Height) >= look.CliffDrop)
                    {
                        continue;
                    }

                    var c = GroundColor(map.Cell(nx, nz), look);
                    r += c.R;
                    g += c.G;
                    b += c.B;
                    count++;
                }
            }

            result[k] = count == 0 ? GroundColor(own, look) : new Rgb(r / count, g / count, b / count);
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
        var steep = Math.Clamp((1f - up) * 3f, 0f, 1f) * look.SlopeShade;
        mesh.Triangle(a, b, c, va.C.Lerp(look.Cliff, steep), vb.C.Lerp(look.Cliff, steep), vc.C.Lerp(look.Cliff, steep));
    }

    private static void Wall(WorldMap map, TerrainLook look, MeshData mesh, int x, int z, int[] h, int dx, int dz)
    {
        int nx = x + dx, nz = z + dz;
        if (!map.Contains(nx, nz))
        {
            return;
        }

        var other = CornerHeights(map, nx, nz, look.CliffDrop);

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
        mesh.Triangle(topA, lowB, lowA, look.Cliff);
        mesh.Triangle(topA, topB, lowB, look.Cliff);
    }

    private static (float X, float Z) Normalised((int Dx, int Dz) d)
    {
        var length = MathF.Sqrt((d.Dx * d.Dx) + (d.Dz * d.Dz));
        return (d.Dx / length, d.Dz / length);
    }

    private enum Carrier
    {
        Bare,
        Bank,
        Still,
        Running,
    }
}
