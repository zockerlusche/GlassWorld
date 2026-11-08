using GlassWorld.Contracts;
using GlassWorld.Game.Prototype.Look;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// The simple low-poly shapes of the look prototype (D569) as single meshes with vertex colours, one per
/// object look (trees, bushes, stones, water sources, small finds). Sizes are in metres at size factor 1;
/// the colours come from the palette by role (<see cref="Presentation.World.ObjectLook.Color"/>). No
/// new assets: primitives and the prototype's cone and rock builders. Presentation only.
/// </summary>
public static class ObjectShapes
{
    /// <summary>Builds the mesh of a shape; <paramref name="color"/> gives the linear colour of a role.</summary>
    public static ArrayMesh Build(ObjectLookShape shape, Func<int, Color> color)
    {
        ArgumentNullException.ThrowIfNull(color);
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var parts = new Parts(tool);
        switch (shape)
        {
            case ObjectLookShape.Conifer_tree:
                parts.Add(new CylinderMesh { TopRadius = 0.32f, BottomRadius = 0.38f, Height = 1.9f, RadialSegments = 6 }, At(0, 0.95f, 0), color(1));
                parts.Add(IslandPiece.Cone(2.0f, 2.3f, 7, Colors.White, 1.5f), Transform3D.Identity, color(0));
                parts.Add(IslandPiece.Cone(1.6f, 2.0f, 7, Colors.White, 2.7f), Transform3D.Identity, color(2));
                parts.Add(IslandPiece.Cone(1.1f, 1.7f, 7, Colors.White, 3.8f), Transform3D.Identity, color(0));
                break;
            case ObjectLookShape.Broadleaf_tree:
                parts.Add(new CylinderMesh { TopRadius = 0.24f, BottomRadius = 0.36f, Height = 2.3f, RadialSegments = 6 }, At(0, 1.15f, 0), color(1));
                parts.Add(Ball(1.7f), At(0, 3.3f, 0, 1, 0.85f, 1), color(0));
                parts.Add(Ball(1.15f), At(0.85f, 3.9f, 0.35f), color(2));
                parts.Add(Ball(1.05f), At(-0.8f, 3.7f, -0.5f), color(0));
                break;
            case ObjectLookShape.Fruit_tree:
                parts.Add(new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.27f, Height = 1.7f, RadialSegments = 6 }, At(0, 0.85f, 0), color(1));
                parts.Add(Ball(1.35f), At(0, 2.7f, 0, 1, 0.85f, 1), color(0));
                for (var i = 0; i < 8; i++)
                {
                    var a = i * 2.4f;
                    var r = 1.25f;
                    parts.Add(Ball(0.13f, 6, 3), At(Mathf.Cos(a) * r, 2.45f + (0.45f * Mathf.Sin(a * 1.7f)), Mathf.Sin(a) * r), color(2));
                }

                break;
            case ObjectLookShape.Palm_tree:
                for (var i = 0; i < 5; i++)
                {
                    // A gently bent trunk of five pieces.
                    parts.Add(new CylinderMesh { TopRadius = 0.2f - (0.015f * i), BottomRadius = 0.23f - (0.015f * i), Height = 1.0f, RadialSegments = 6 }, At(0.06f * i * i, 0.5f + (0.95f * i), 0), color(1));
                }

                for (var i = 0; i < 7; i++)
                {
                    var a = i * Mathf.Tau / 7;
                    var leaf = new Transform3D(new Basis(Vector3.Up, a) * new Basis(Vector3.Right, 0.35f) * Basis.FromScale(new Vector3(0.45f, 0.1f, 2.0f)), new Vector3(1.0f + (Mathf.Sin(a) * 1.0f), 4.75f, Mathf.Cos(a) * 1.0f));
                    parts.Add(Ball(0.5f), leaf, color(0));
                }

                break;
            case ObjectLookShape.Bush:
                parts.Add(Ball(0.9f), At(0, 0.45f, 0, 1, 0.7f, 1), color(0));
                parts.Add(Ball(0.55f), At(0.55f, 0.35f, 0.25f), color(0));
                break;
            case ObjectLookShape.Berry_bush:
                parts.Add(Ball(0.9f), At(0, 0.45f, 0, 1, 0.7f, 1), color(0));
                for (var i = 0; i < 9; i++)
                {
                    var a = i * 2.4f;
                    parts.Add(Ball(0.14f, 6, 3), At(Mathf.Cos(a) * 0.75f, 0.55f + (0.25f * Mathf.Sin(a * 1.7f)), Mathf.Sin(a) * 0.6f), color(1));
                }

                break;
            case ObjectLookShape.Rock:
                parts.Add(IslandPiece.Rock(7, new Vector3(1.4f, 0.9f, 1.2f), Colors.White), Transform3D.Identity, color(0));
                break;
            case ObjectLookShape.Stone:
                parts.Add(IslandPiece.Rock(13, new Vector3(0.38f, 0.24f, 0.32f), Colors.White), Transform3D.Identity, color(0));
                break;
            case ObjectLookShape.Tuft:
                Blades(parts, 0.32f, color(0));
                break;
            case ObjectLookShape.Flower:
                Blades(parts, 0.26f, color(0));
                for (var i = 0; i < 3; i++)
                {
                    var a = i * 2.1f;
                    parts.Add(Ball(0.075f, 6, 3), At(Mathf.Cos(a) * 0.08f, 0.32f + (0.04f * i), Mathf.Sin(a) * 0.08f), color(1));
                }

                break;
            case ObjectLookShape.Mushroom:
                parts.Add(new CylinderMesh { TopRadius = 0.045f, BottomRadius = 0.055f, Height = 0.16f, RadialSegments = 6 }, At(0, 0.08f, 0), color(1));
                parts.Add(new SphereMesh { Radius = 0.15f, Height = 0.15f, IsHemisphere = true, RadialSegments = 8, Rings = 3 }, At(0, 0.15f, 0), color(0));
                break;
            case ObjectLookShape.Pool:
                Pool(parts, color(0), color(1), color(2));
                break;
            case ObjectLookShape.Log:
                var lying = new Basis(Vector3.Forward, Mathf.Pi / 2);
                parts.Add(new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.13f, Height = 1.0f, RadialSegments = 7 }, new Transform3D(lying, new Vector3(0, 0.12f, 0)), color(0));
                parts.Add(new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.1f, Height = 0.02f, RadialSegments = 7 }, new Transform3D(lying, new Vector3(0.5f, 0.12f, 0)), color(1));
                parts.Add(new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.1f, Height = 0.02f, RadialSegments = 7 }, new Transform3D(lying, new Vector3(-0.5f, 0.12f, 0)), color(1));
                break;
            case ObjectLookShape.Fruit:
                parts.Add(Ball(0.1f, 7, 4), At(0, 0.1f, 0), color(0));
                break;
            case ObjectLookShape.Shell:
                parts.Add(new SphereMesh { Radius = 0.11f, Height = 0.11f, IsHemisphere = true, RadialSegments = 8, Rings = 3 }, At(0, 0, 0, 1, 0.45f, 1.2f), color(0));
                break;
            case ObjectLookShape.Reeds:
                for (var i = 0; i < 6; i++)
                {
                    var a = i * 2.3f;
                    var tilt = new Basis(new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)), 0.12f);
                    var foot = new Vector3(Mathf.Cos(a) * 0.12f, 0, Mathf.Sin(a) * 0.12f);
                    var height = 1.0f + (0.15f * (i % 3));
                    parts.Add(new CylinderMesh { TopRadius = 0.015f, BottomRadius = 0.02f, Height = height, RadialSegments = 4 }, new Transform3D(tilt, foot + (tilt * new Vector3(0, height / 2, 0))), color(0));
                    parts.Add(new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = 0.18f, RadialSegments = 5 }, new Transform3D(tilt, foot + (tilt * new Vector3(0, height + 0.05f, 0))), color(1));
                }

                break;
            case ObjectLookShape.Marker:
                // Calm and neutral: a low rounded post, nothing that looks like a warning.
                parts.Add(new CylinderMesh { TopRadius = 0.13f, BottomRadius = 0.16f, Height = 0.3f, RadialSegments = 8 }, At(0, 0.15f, 0), color(0));
                parts.Add(Ball(0.13f, 8, 4), At(0, 0.3f, 0), color(0));
                break;
            case ObjectLookShape.None:
                break;
            default:
                throw new InvalidDataException($"No builder for object shape {shape}.");
        }

        tool.Index();
        return tool.Commit();
    }

    /// <summary>Irregular outline of a spring pool: radius factor per corner.</summary>
    private static readonly float[] PoolOutline = [1.0f, 0.86f, 0.95f, 0.8f, 0.92f, 1.0f, 0.84f, 0.9f, 0.97f];

    /// <summary>
    /// A small natural spring pool (D576), drawn on the ground plane y = 0 that the placement tilts to the
    /// slope: an irregular low-poly water patch lying a little below a low muddy rim, and a few uneven
    /// stones half sunk at the uphill side (local +x). Nothing reaches below the ground, no ring, no basin.
    /// </summary>
    private static void Pool(Parts parts, Color water, Color stone, Color bank)
    {
        const float Water = 0.022f, Lip = 0.034f, Rim = 0.012f, Inner = 0.5f, Outer = 0.72f;
        var n = PoolOutline.Length;
        static Vector3 Edge(int i, float radius, float y)
        {
            var n = PoolOutline.Length;
            var a = i % n * Mathf.Tau / n;
            return new Vector3(Mathf.Cos(a) * radius * PoolOutline[i % n] * 0.85f, y, Mathf.Sin(a) * radius * PoolOutline[i % n] * 1.1f);
        }

        for (var i = 0; i < n; i++)
        {
            // Water fan, its edge tucked under the lip; the rim slopes from the lip down to the ground.
            parts.Triangle(new Vector3(0, Water, 0), Edge(i, Inner * 1.02f, Water), Edge(i + 1, Inner * 1.02f, Water), water);
            parts.Triangle(Edge(i, Inner, Lip), Edge(i, Outer, Rim), Edge(i + 1, Outer, Rim), bank);
            parts.Triangle(Edge(i, Inner, Lip), Edge(i + 1, Outer, Rim), Edge(i + 1, Inner, Lip), bank);
            parts.Triangle(Edge(i, Inner, Water), Edge(i, Inner, Lip), Edge(i + 1, Inner, Lip), bank);
            parts.Triangle(Edge(i, Inner, Water), Edge(i + 1, Inner, Lip), Edge(i + 1, Inner, Water), bank);
        }

        // A few uneven stones at the uphill side, half sunk into the ground.
        (float Angle, float Radius, Vector3 Size)[] stones = [(-0.55f, 0.6f, new Vector3(0.2f, 0.12f, 0.16f)), (0.05f, 0.66f, new Vector3(0.26f, 0.16f, 0.2f)), (0.6f, 0.58f, new Vector3(0.15f, 0.1f, 0.13f))];
        for (var i = 0; i < stones.Length; i++)
        {
            var (angle, radius, size) = stones[i];
            var place = new Vector3(Mathf.Cos(angle) * radius, -size.Y * 0.35f, Mathf.Sin(angle) * radius);
            parts.Add(IslandPiece.Rock(31 + i, size, Colors.White), new Transform3D(new Basis(Vector3.Up, angle * 1.7f), place), stone);
        }
    }

    private static SphereMesh Ball(float radius, int segments = 8, int rings = 4) =>
        new() { Radius = radius, Height = radius * 2, RadialSegments = segments, Rings = rings };

    private static Transform3D At(float x, float y, float z, float sx = 1, float sy = 1, float sz = 1) =>
        new(Basis.FromScale(new Vector3(sx, sy, sz)), new Vector3(x, y, z));

    /// <summary>A small tuft of four blades (as the prototype's grass tufts).</summary>
    private static void Blades(Parts parts, float height, Color color)
    {
        for (var i = 0; i < 4; i++)
        {
            var a = (i * 1.3f) - 2f;
            var lean = new Vector3(Mathf.Sin(a) * 0.12f, 0, Mathf.Cos(a) * 0.05f);
            var side = new Vector3(Mathf.Cos(a), 0, -Mathf.Sin(a)) * 0.04f;
            var tip = new Vector3(0, height + (0.08f * (i % 2)), 0) + lean;
            parts.Triangle(-side, side, tip, color);
            parts.Triangle(side, -side, tip, color);
        }
    }

    /// <summary>Appends meshes with one colour each into one surface.</summary>
    private sealed class Parts(SurfaceTool tool)
    {
        public void Add(Mesh mesh, Transform3D transform, Color color)
        {
            var arrays = mesh.SurfaceGetArrays(0);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var indexArray = arrays[(int)Mesh.ArrayType.Index];
            var indices = indexArray.VariantType == Variant.Type.Nil ? [] : indexArray.AsInt32Array();
            var normalBasis = transform.Basis.Inverse().Transposed();
            void Vertex(int i)
            {
                tool.SetColor(color);
                tool.SetNormal(normals.Length > i ? (normalBasis * normals[i]).Normalized() : Vector3.Up);
                tool.AddVertex(transform * vertices[i]);
            }

            if (indices.Length > 0)
            {
                foreach (var i in indices)
                {
                    Vertex(i);
                }
            }
            else
            {
                for (var i = 0; i < vertices.Length; i++)
                {
                    Vertex(i);
                }
            }
        }

        public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color color)
        {
            var normal = (c - a).Cross(b - a).Normalized();
            foreach (var p in new[] { a, b, c })
            {
                tool.SetColor(color);
                tool.SetNormal(normal);
                tool.AddVertex(p);
            }
        }
    }
}
