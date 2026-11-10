namespace GlassWorld.Presentation.Ui;

/// <summary>A theme of the side panel in the zones layout: its key, its title key and the side windows it holds as tabs.</summary>
/// <param name="Id">Theme id.</param>
/// <param name="TitleKey">Text key of the theme button.</param>
/// <param name="Panels">Keys of the side windows, in tab order; the first opens by default.</param>
public sealed record ScreenTheme(string Id, string TitleKey, IReadOnlyList<string> Panels);

/// <summary>
/// The fixed screen zones of the UI concept (W1717, D642), preview of step 1: top bar over the full width, one
/// side panel on the right whose content is chosen by theme (inhabitants, world, contact) and tab, the
/// conversation log pinned on the left, hints at the bottom centre, the middle always free. The side windows
/// themselves are unchanged. Godot-free. The grouping moves into the UI data once Marcel has approved it.
/// </summary>
public static class ScreenZones
{
    /// <summary>The side window pinned on the left (the conversation log); it belongs to no theme.</summary>
    public const string Pinned = "log";

    /// <summary>The side window that holds the inhabitant card in the zones layout (a tab of the people theme).</summary>
    public const string Card = "card";

    /// <summary>The themes in the order of their buttons.</summary>
    public static IReadOnlyList<ScreenTheme> Themes { get; } =
    [
        new("people", "ui.zone.people", ["inhabitants", Card]),
        new("world", "ui.zone.world", ["chronicle"]),
        new("contact", "ui.zone.contact", ["questions", "help", "mailbox"]),
    ];

    /// <summary>Text key of a side window's title (also its tab).</summary>
    public static string TitleKey(string panel) => "ui." + panel + ".title";

    /// <summary>The theme a side window belongs to, or null (the pinned log, unknown keys).</summary>
    public static ScreenTheme? ThemeOf(string? panel) =>
        panel is null ? null : Themes.FirstOrDefault(t => t.Panels.Contains(panel, StringComparer.Ordinal));

    /// <summary>
    /// The side window a theme button opens: the one last shown of that theme, else its first; null closes
    /// the panel (the theme's window is open already – the button toggles).
    /// </summary>
    public static string? Open(ScreenTheme theme, string? openPanel, string? lastOfTheme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (ThemeOf(openPanel) == theme)
        {
            return null;
        }

        return lastOfTheme is not null && theme.Panels.Contains(lastOfTheme, StringComparer.Ordinal) ? lastOfTheme : theme.Panels[0];
    }
}
