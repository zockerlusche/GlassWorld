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
        void Update(Theme theme)
        {
            if (GodotObject.IsInstanceValid(uiRoot))
            {
                uiRoot.Theme = theme;
            }
        }

        ThemeApplied += Update;
        uiRoot.TreeExiting += () => ThemeApplied -= Update;
    }

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"'{name}' is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
