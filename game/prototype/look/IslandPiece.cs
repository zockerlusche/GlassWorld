using Godot;

namespace GlassWorld.Game.Prototype.Look;

/// <summary>
/// The small island piece of the look prototype (A1435), laid out after D355: grass with conifers
/// and rocks in the upper left, a stream running down to a curved sand beach, the sea with soft
/// depth bands on the right. Shape functions are fixed and deterministic (presentation only; the
/// real island comes from the generator in B2-2).
/// </summary>
public static class IslandPiece
{
    /// <summary>Stream course (x, z) from the hills to the beach.</summary>
    public static readonly Vector2[] StreamCourse =
    [
        new(-30, -13.5f), new(-24, -11.2f), new(-19, -12.4f), new(-14, -10.4f), new(-9.5f, -8.2f), new(-6, -8.9f), new(-2.5f, -7.4f),
        new(0.5f, -6.4f), new(3.5f, -6.6f), new(6.5f, -6.1f), new(9.5f, -5.6f),
    ];

    /// <summary>x of the waterline at depth z.</summary>
    public static float ShoreX(float z) => 7.5f + (3.2f * Mathf.Sin((0.13f * z) + 0.5f)) + (1.1f * Mathf.Sin(0.33f * z));

    /// <summary>x where grass begins (smaller x is grass), with an inlet of sand where the figures stand.</summary>
    public static float GrassX(float z) =>
        ShoreX(z) - (7.5f + (1.8f * Mathf.Sin((0.21f * z) + 1.3f))) - (3.5f * Mathf.Exp(-Mathf.Pow((z - 3f) / 5f, 2)));

    /// <summary>The stream course smoothed with Catmull-Rom (8 points per segment).</summary>
    private static readonly Vector2[] Smoothed = Smooth(StreamCourse, 8);

    private static Vector2[] Smooth(Vector2[] points, int steps)
    {
        var result = new List<Vector2>();
        for (var i = 0; i < points.Length - 1; i++)
        {
            var p0 = points[Math.Max(0, i - 1)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(points.Length - 1, i + 2)];
            for (var s = 0; s < steps; s++)
            {
                var t = s / (float)steps;
                var t2 = t * t;
                var t3 = t2 * t;
                result.Add(0.5f * ((2 * p1) + ((p2 - p0) * t) + (((2 * p0) - (5 * p1) + (4 * p2) - p3) * t2) + ((-p0 + (3 * p1) - (3 * p2) + p3) * t3)));
            }
        }

        result.Add(points[^1]);
        return [.. result];
    }

    /// <summary>Distance to the stream centre line and the position along it (metres).</summary>
    public static (float Distance, float Along) Stream(float x, float z)
    {
        var p = new Vector2(x, z);
        var best = float.MaxValue;
        var along = 0f;
        var walked = 0f;
        for (var i = 0; i < Smoothed.Length - 1; i++)
        {
            var a = Smoothed[i];
            var b = Smoothed[i + 1];
            var ab = b - a;
            var t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
            var d = p.DistanceTo(a + (ab * t));
            if (d < best)
            {
                best = d;
                along = walked + (ab.Length() * t);
            }

            walked += ab.Length();
        }

        return (best, along);
    }

    /// <summary>Ground height in metres (sea level 0).</summary>
    public static float Height(float x, float z)
    {
        var seaward = x - ShoreX(z);
        var h = seaward > 0
            ? -Mathf.Min(2.6f, seaward * 0.2f) - 0.02f
            : Mathf.Min(0.2f, -seaward * 0.045f);
        var inside = GrassX(z) - x;
        h += 0.34f * SmoothStep(-0.35f, 0.35f, inside);
        var (distance, _) = Stream(x, z);
        var carved = h - (0.36f * (1 - SmoothStep(0.55f, 1.5f, distance)));
        // On the beach the bed never dips below the sea, so the sea does not show through the stream.
        return seaward > -1.5f ? Mathf.Min(h, carved) : Mathf.Max(carved, Mathf.Min(h, 0.03f));
    }

    /// <summary>Whether a point lies on the grass (away from the stream).</summary>
    public static bool OnGrass(float x, float z, float margin) =>
        GrassX(z) - x > margin && Stream(x, z).Distance > 1.6f + margin;

    /// <summary>Ground mesh with vertex colours (linear) from the palette.</summary>
    public static ArrayMesh Ground(LookData look, Rect2 area, float step)
    {
        ArgumentNullException.ThrowIfNull(look);
        var grass = look.Linear("grass");
        var edge = look.Linear("grass_edge");
        var sand = look.Linear("sand");
        var wet = look.Linear("sand_wet");
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var nx = (int)(area.Size.X / step);
        var nz = (int)(area.Size.Y / step);
        for (var iz = 0; iz <= nz; iz++)
        {
            for (var ix = 0; ix <= nx; ix++)
            {
                var x = area.Position.X + (ix * step);
                var z = area.Position.Y + (iz * step);
                var inside = GrassX(z) - x;
                var seaward = x - ShoreX(z);
                var stream = Stream(x, z).Distance;
                var beach = sand.Lerp(wet, SmoothStep(-1.8f, -0.2f, seaward));
                var grassAmount = SmoothStep(-0.15f, 0.15f, inside) * SmoothStep(0.95f, 1.25f, stream);
                var lip = SmoothStep(0.1f, 0.0f, Mathf.Abs(inside - 0.1f) - 0.12f) * grassAmount;
                var bank = (1 - SmoothStep(0.95f, 1.35f, stream)) * SmoothStep(-0.2f, 0.2f, inside);
                var color = beach.Lerp(grass, grassAmount).Lerp(edge, Mathf.Max(lip * 0.85f, bank));
                tool.SetColor(color);
                tool.SetUV(new Vector2(x, z));
                tool.AddVertex(new Vector3(x, Height(x, z), z));
            }
        }

        for (var iz = 0; iz < nz; iz++)
        {
            for (var ix = 0; ix < nx; ix++)
            {
                var i = (iz * (nx + 1)) + ix;
                tool.AddIndex(i);
                tool.AddIndex(i + 1);
                tool.AddIndex(i + nx + 1);
                tool.AddIndex(i + 1);
                tool.AddIndex(i + nx + 2);
                tool.AddIndex(i + nx + 1);
            }
        }

        tool.GenerateNormals();
        return tool.Commit();
    }

    /// <summary>Water ribbon along the stream (UV.x = metres along, UV.y = across 0..1).</summary>
    public static ArrayMesh StreamRibbon(float width)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var points = new List<(Vector2 P, Vector2 Side, float Along)>();
        var walked = 0f;
        for (var i = 0; i < Smoothed.Length - 1; i++)
        {
            var a = Smoothed[i];
            var b = Smoothed[i + 1];
            var length = a.DistanceTo(b);
            var dir = (b - a) / length;
            for (var t = 0f; t < length; t += 0.25f)
            {
                points.Add((a + (dir * t), new Vector2(-dir.Y, dir.X), walked + t));
            }

            walked += length;
        }

        foreach (var (p, side, along) in points)
        {
            // The mouth widens and fades into the wet sand.
            var widen = 1 + (0.6f * SmoothStep(walked - 5, walked, along));
            var y = Height(p.X, p.Y) + 0.27f;
            for (var s = 0; s < 2; s++)
            {
                var offset = side * (s == 0 ? -1 : 1) * width * 0.5f * widen;
                tool.SetUV(new Vector2(along, s));
                tool.AddVertex(new Vector3(p.X + offset.X, y, p.Y + offset.Y));
            }
        }

        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = i * 2;
            tool.AddIndex(a);
            tool.AddIndex(a + 2);
            tool.AddIndex(a + 1);
            tool.AddIndex(a + 1);
            tool.AddIndex(a + 2);
            tool.AddIndex(a + 3);
        }

        tool.GenerateNormals();
        return tool.Commit();
    }

    /// <summary>Flat-shaded cone (conifer layer) with one colour.</summary>
    public static ArrayMesh Cone(float radius, float height, int segments, Color color, float yOffset)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        tool.SetSmoothGroup(uint.MaxValue);
        tool.SetColor(color);
        var tip = new Vector3(0, yOffset + height, 0);
        for (var i = 0; i < segments; i++)
        {
            var a0 = Mathf.Tau * i / segments;
            var a1 = Mathf.Tau * (i + 1) / segments;
            var p0 = new Vector3(Mathf.Cos(a0) * radius, yOffset, Mathf.Sin(a0) * radius);
            var p1 = new Vector3(Mathf.Cos(a1) * radius, yOffset, Mathf.Sin(a1) * radius);
            // Godot treats clockwise triangles (seen from outside) as front faces.
            tool.AddVertex(p0);
            tool.AddVertex(p1);
            tool.AddVertex(tip);
            tool.AddVertex(p0);
            tool.AddVertex(new Vector3(0, yOffset, 0));
            tool.AddVertex(p1);
        }

        tool.GenerateNormals();
        return tool.Commit();
    }

    /// <summary>Flat-shaded faceted rock: a low sphere with deterministic jitter.</summary>
    public static ArrayMesh Rock(int seed, Vector3 size, Color color)
    {
        var sphere = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 7, Rings = 4 };
        var arrays = sphere.GetMeshArrays();
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        tool.SetSmoothGroup(uint.MaxValue);
        tool.SetColor(color);
        Vector3 Jitter(Vector3 v)
        {
            // Same jitter for vertices on the same position (the sphere repeats seam vertices).
            var key = (int)(v.X * 1000) * 73856093 ^ (int)(v.Y * 1000) * 19349663 ^ (int)(v.Z * 1000) * 83492791 ^ seed;
            var r = 0.82f + (0.3f * Hash(key));
            var flat = v with { Y = Mathf.Max(v.Y, -0.15f) };
            return flat * r * size;
        }

        foreach (var index in indices)
        {
            tool.AddVertex(Jitter(vertices[index]));
        }

        tool.GenerateNormals();
        return tool.Commit();
    }

    /// <summary>Deterministic hash to 0..1.</summary>
    public static float Hash(int n)
    {
        unchecked
        {
            var x = (uint)n;
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0xFFFFFF) / (float)0x1000000;
        }
    }

    /// <summary>GLSL-style smoothstep.</summary>
    public static float SmoothStep(float from, float to, float x)
    {
        var t = Mathf.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - (2 * t));
    }
}
