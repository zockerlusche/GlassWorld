using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Exploration;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// The cloud cover over what the player has not seen yet (B2-6 R25; island scene and island mode of the
/// main game): a soft, light, slowly drifting layer of clouds instead of black. The reveal map
/// (<see cref="RevealMap"/>, one value per cell) follows the player's view of the core and lives in a
/// small texture (one byte per cell) that is written again only when a value changed; newly seen land
/// clears over <c>exploration.reveal_real_seconds</c> of the preset. One screen-filling quad draws the
/// clouds (<c>cloud_cover.gdshader</c>): it reads the place of every drawn pixel from the depth of the
/// picture and covers it where the map says "unseen", so ground, plants, objects and figures in unseen
/// cells are hidden from every camera angle and distance, with no shift between the hole in the clouds
/// and the land under it. The open sea beyond <c>sea_margin_cells</c> from land is never covered. The
/// colours come from the palette, the look from <c>content/look/nature.json</c> <c>cover</c>; the tint
/// follows the day phase (global <c>water_light</c>). Presentation only: it reads the core, never writes it.
/// </summary>
public partial class CloudCover : MeshInstance3D
{
    /// <summary>Render priority of the cover: after everything see-through of the world (sea, rain, silhouettes).</summary>
    public const int Priority = 1;

    /// <summary>Render priority of what always shows above the cover (symbols and marks above heads).</summary>
    public const int AbovePriority = 2;

    private static readonly Shader CoverShader = GD.Load<Shader>("res://shaders/cloud_cover.gdshader");
    private ShaderMaterial _material = null!;
    private Image _image = null!;
    private ImageTexture _texture = null!;
    private byte[] _texels = [];
    private Vector2 _size;
    private Vector2 _driftPerSecond;
    private float _layerHeight;
    private Vector2 _drift;

    /// <summary>The reveal map.</summary>
    public RevealMap Map { get; private set; } = null!;

    /// <summary>
    /// Creates the cover of an island: <paramref name="isLand"/> says whether a cell is land,
    /// <paramref name="windX"/> and <paramref name="windZ"/> are the direction the pattern drifts to. What the
    /// player has seen by now is clear at once.
    /// </summary>
    public static CloudCover Create(ExplorationSystem exploration, Func<int, int, bool> isLand, NatureCover data, LookPalette palette, float cellM, double clearRealSeconds, double windX, double windZ)
    {
        ArgumentNullException.ThrowIfNull(exploration);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(palette);
        var (width, depth) = (exploration.Width, exploration.Depth);
        Color PaletteColor(string id) => Color.FromHtml((palette.Colors.FirstOrDefault(c => c.Id == id) ?? throw new InvalidDataException($"Unknown palette colour '{id}' in nature.json cover.")).Color);
        var material = new ShaderMaterial { Shader = CoverShader, RenderPriority = Priority };
        var cover = new CloudCover
        {
            Name = "CloudCover",
            Map = new RevealMap(width, depth, clearRealSeconds, data.CellsPerFrame, RevealMap.OpenSea(width, depth, isLand, data.SeaMarginCells)),
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            MaterialOverride = material,
            CastShadow = ShadowCastingSetting.Off,

            // The quad is placed on the screen by its shader: it must never be culled by its place in the world.
            ExtraCullMargin = 16384,
            Position = new Vector3(width * cellM / 2, 0, depth * cellM / 2),
            _material = material,
            _texels = new byte[width * depth],
            _size = new Vector2(width * cellM, depth * cellM),
            _driftPerSecond = new Vector2((float)(windX * data.DriftMPerS), (float)(windZ * data.DriftMPerS)),
            _layerHeight = (float)data.LayerHeightM,
        };
        cover.Map.Follow(exploration.Version, exploration.RevealedSince);
        cover.Map.Advance(0);
        cover.Map.CopyTo(cover._texels);
        cover._image = Image.CreateFromData(width, depth, false, Image.Format.R8, cover._texels);
        cover._texture = ImageTexture.CreateFromImage(cover._image);
        material.SetShaderParameter("reveal_map", cover._texture);
        material.SetShaderParameter("map_size_m", cover._size);
        material.SetShaderParameter("cell_uv", new Vector2(1f / width, 1f / depth));
        material.SetShaderParameter("light_color", PaletteColor(data.Color));
        material.SetShaderParameter("shade_color", PaletteColor(data.ShadeColor));
        material.SetShaderParameter("puff_m", (float)data.PuffM);
        material.SetShaderParameter("softness_cells", (float)data.SoftnessCells);
        material.SetShaderParameter("edge_softness", (float)data.EdgeSoftness);
        material.SetShaderParameter("edge_puff", (float)data.EdgePuff);
        material.SetShaderParameter("band", (float)data.Band);
        material.SetShaderParameter("band_softness", (float)data.BandSoftness);
        material.SetShaderParameter("opacity", (float)data.Opacity);
        material.SetShaderParameter("daylight_share", (float)data.DaylightShare);
        return cover;
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        // The map corner in the world (the island may hang under a moved node).
        var centre = GlobalPosition;
        _material.SetShaderParameter("map_origin", new Vector2(centre.X - (_size.X / 2), centre.Z - (_size.Y / 2)));
        _material.SetShaderParameter("layer_y", centre.Y + _layerHeight);
    }

    /// <summary>
    /// Takes what the core revealed up to the version of a snapshot (asked where the snapshot is taken);
    /// null (no island view) changes nothing.
    /// </summary>
    public void Take(ExplorationSystem exploration, int? exploredVersion)
    {
        ArgumentNullException.ThrowIfNull(exploration);
        if (exploredVersion is { } version)
        {
            Map.Follow(version, exploration.RevealedSince);
        }
    }

    /// <summary>
    /// One frame: newly seen land clears on and the billows drift, both by the real seconds since the last
    /// frame (0 while the game is paused holds them); the texture is written only when a value changed.
    /// </summary>
    public void Advance(double realSeconds)
    {
        if (Map.Advance(realSeconds))
        {
            Upload();
        }

        if (realSeconds > 0 && _driftPerSecond != Vector2.Zero)
        {
            _drift += _driftPerSecond * (float)realSeconds;
            _material.SetShaderParameter("drift_offset", _drift);
        }
    }

    /// <summary>For pictures: shows at least this share of land and coast as seen, settled (see <see cref="RevealMap.ShowShare"/>).</summary>
    public void ShowShare(int permille)
    {
        Map.ShowShare(permille);
        Advance(0);
    }

    private void Upload()
    {
        Map.CopyTo(_texels);
        _image.SetData(Map.Width, Map.Depth, false, Image.Format.R8, _texels);
        _texture.Update(_image);
    }
}
