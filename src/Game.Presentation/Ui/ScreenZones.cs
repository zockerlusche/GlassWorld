using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Ui;

/// <summary>A round button top right and the side windows it holds as tabs.</summary>
/// <param name="Id">Theme id.</param>
/// <param name="TitleKey">Text key of the button's tooltip (and the theme's name).</param>
/// <param name="Panels">Keys of the side windows, in tab order; the first opens by default.</param>
/// <param name="Icon">The drawn symbol of the round button.</param>
public sealed record ScreenTheme(string Id, string TitleKey, IReadOnlyList<string> Panels, UiIconChoiceIcon Icon);

/// <summary>
/// The corners of the UI concept Variante A (D957 v4 section 1, B2643; grew out of the zones preview W1717/D642):
/// portraits top left, time block with round buttons top right, below it on the right the one place for a side
/// window or the card, the log on the left, the middle always free. A round button opens its theme's window in
/// that place; a theme with more than one window shows them as tabs. Godot-free.
/// </summary>
public static class ScreenZones
{
    /// <summary>The side window pinned on the left (the conversation log); it belongs to no theme.</summary>
    public const string Pinned = "log";

    /// <summary>The side window that holds the inhabitant card (a tab of the people theme).</summary>
    public const string Card = "card";

    /// <summary>The side window with the timeline over seasons and years (a tab of the chronicle theme).</summary>
    public const string Timeline = "timeline";

    /// <summary>Help and gifts: opened from the menu, not by a round button (TL #23224, D957 section 7).</summary>
    public const string FromMenu = "help";

    /// <summary>
    /// The round buttons after the menu button, in their order (D957 section 1: questions, mail, chronicle,
    /// inhabitants).
    /// </summary>
    public static IReadOnlyList<ScreenTheme> Themes { get; } =
    [
        new("questions", "ui.zone.questions", ["questions"], UiIconChoiceIcon.Question),
        new("mailbox", "ui.zone.mailbox", ["mailbox"], UiIconChoiceIcon.Letter),
        new("chronicle", "ui.zone.chronicle", ["chronicle", Timeline], UiIconChoiceIcon.Book),
        new("people", "ui.zone.people", ["inhabitants", Card], UiIconChoiceIcon.Person),
    ];

    /// <summary>Text key of a side window's title (also its tab).</summary>
    public static string TitleKey(string panel) => "ui." + panel + ".title";

    /// <summary>Text key of a panel's tab inside its theme (D705 B9: the tab "Bewohner" inside the theme "Bewohner").</summary>
    public static string TabKey(string panel) => "ui.zone.tab." + panel;

    /// <summary>The theme a side window belongs to, or null (the pinned log, help from the menu, unknown keys).</summary>
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
