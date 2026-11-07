using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using Godot;

namespace GlassWorld.Game.Prototype.Look;

/// <summary>
/// Look data for the prototype (A1435): palette, camera and appearance from content/look, read
/// through the contract types so a bad value fails at load (ADR-0007).
/// </summary>
public sealed class LookData
{
    private readonly Dictionary<string, Color> _colors;

    private LookData(LookPalette palette, LookCamera camera, LookAppearance appearance)
    {
        Palette = palette;
        Camera = camera;
        Appearance = appearance;
        _colors = palette.Colors.ToDictionary(c => c.Id, c => new Color(c.Color), StringComparer.Ordinal);
    }

    /// <summary>Palette data.</summary>
    public LookPalette Palette { get; }

    /// <summary>Camera data.</summary>
    public LookCamera Camera { get; }

    /// <summary>Appearance ranges.</summary>
    public LookAppearance Appearance { get; }

    /// <summary>Loads content/look from the repository next to the Godot project.</summary>
    public static LookData Load(string contentDirectory)
    {
        T Read<T>(string file)
            where T : class => ContractSerializer.Deserialize<T>(System.IO.File.ReadAllText(System.IO.Path.Combine(contentDirectory, "look", file)));
        return new LookData(Read<LookPalette>("palette.json"), Read<LookCamera>("camera.json"), Read<LookAppearance>("appearance.json"));
    }

    /// <summary>A palette colour in sRGB (as written in the data).</summary>
    public Color Srgb(string id) =>
        _colors.TryGetValue(id, out var color) ? color : throw new KeyNotFoundException($"palette colour '{id}' missing in content/look/palette.json");

    /// <summary>A palette colour converted to linear for vertex colours (shaders work in linear space).</summary>
    public Color Linear(string id) => Srgb(id).SrgbToLinear();
}
