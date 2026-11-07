using System.Text.RegularExpressions;

namespace GlassWorld.Presentation.Ui;

/// <summary>Recognises language-neutral text keys (KR4), e.g. to find raw keys on screen (B6 acceptance 11).</summary>
public static partial class TextKeys
{
    /// <summary>True if the text has the form of a key (<c>ui.menu.quit</c>): lower case, at least two dotted parts.</summary>
    public static bool LooksLikeKey(string? text) => text is not null && KeyPattern().IsMatch(text);

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
