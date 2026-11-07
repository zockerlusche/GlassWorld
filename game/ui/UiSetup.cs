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

    /// <summary>Applies a style to the root window.</summary>
    public static void Apply(Window root, UiStyle style)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(style);
        CurrentTheme = UiTheme.Build(style);
        root.Theme = CurrentTheme;
        root.ContentScaleFactor = (float)style.ContentScaleFactor;
        Current = style;
    }

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"'{name}' is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
