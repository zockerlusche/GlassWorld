using System.Reflection;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui;

/// <summary>
/// Loads the UI standard data (content/ui, embedded at build time like the text catalogs), checks it
/// and applies it to the root window: theme and UI size (<c>ContentScaleFactor</c>, B2513).
/// </summary>
public static class UiSetup
{
    /// <summary>Style currently applied; null if the data was invalid.</summary>
    public static UiStyle? Current { get; private set; }

    /// <summary>Theme currently applied.</summary>
    public static Theme? CurrentTheme { get; private set; }

    /// <summary>Reads and validates content/ui; logs an error and returns null if it is broken.</summary>
    public static UiStyle? Load(UiSettings? settings = null)
    {
        try
        {
            return UiStyle.FromJson(Read("content.ui.tokens.json"), Read("content.ui.frames.json"), settings);
        }
        catch (Exception e) when (e is InvalidDataException or GlassWorld.Contracts.Validation.ContractViolationException)
        {
            GD.PushError($"ui: {e.Message}");
            return null;
        }
    }

    /// <summary>Raised after a new theme was applied (UI roots below CanvasLayers take it over).</summary>
    public static event Action<Theme>? ThemeApplied;

    /// <summary>Applies a style to the root window and to every bound UI root.</summary>
    public static void Apply(Window root, UiStyle style)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(style);
        CurrentTheme = UiTheme.Build(style);
        root.Theme = CurrentTheme;
        root.ContentScaleFactor = (float)style.ContentScaleFactor;
        Current = style;
        ThemeApplied?.Invoke(CurrentTheme);
    }

    /// <summary>
    /// Gives a UI root below a CanvasLayer the current theme and keeps it updated. Godot does not pass a
    /// theme through nodes that are neither Control nor Window (a CanvasLayer breaks the chain), so
    /// without this the screens fall back to the engine's default theme: no frames, fonts, colours or
    /// text size (found with the A1362 frame screenshots).
    /// </summary>
    public static void Bind(Control uiRoot)
    {
        ArgumentNullException.ThrowIfNull(uiRoot);
        uiRoot.Theme = CurrentTheme;

        // The frame pictures are drawn much smaller than their source; use their mipmaps (GW1 #66 F2).
        uiRoot.TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps;
        void Update(Theme theme)
        {
            if (GodotObject.IsInstanceValid(uiRoot))
            {
                uiRoot.Theme = theme;
            }
        }

        // Subscribed while in the tree; a root that is moved (out and in again) gets the current theme
        // and further updates again (GW1 review #65 T2).
        ThemeApplied += Update;
        uiRoot.TreeExiting += () => ThemeApplied -= Update;
        uiRoot.TreeEntered += () =>
        {
            ThemeApplied -= Update;
            ThemeApplied += Update;
            uiRoot.Theme = CurrentTheme;
        };
    }

    /// <summary>
    /// Smoke run only (GW1 review #65 T1): every Control directly below a CanvasLayer carries a theme
    /// itself, since the root window's theme does not reach it.
    /// </summary>
    /// <returns>Paths of controls without a theme.</returns>
    public static IReadOnlyList<string> UnthemedCanvasRoots(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return [.. root.FindChildren("*", nameof(CanvasLayer), recursive: true, owned: false)
            .SelectMany(layer => layer.GetChildren().OfType<Control>())
            .Where(c => c.Theme is null)
            .Select(c => c.GetPath().ToString())];
    }

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"'{name}' is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
