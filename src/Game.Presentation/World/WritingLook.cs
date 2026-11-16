namespace GlassWorld.Presentation.World;

/// <summary>
/// A1356 (B8-11): how a written thing shows - its marks (per carrier, data), how pale they are by legibility, and what it
/// says in the player's language. What is written is the author's belief (the core's knowledge ref), never the truth.
/// </summary>
public static class WritingLook
{
    /// <summary>The words of a knowledge ref <c>belief:type|property|true|false</c>, or null for any other form.</summary>
    public static string? Says(string knowledgeRef, Func<string, string> tr, Func<string, string> thingName)
    {
        ArgumentNullException.ThrowIfNull(knowledgeRef);
        ArgumentNullException.ThrowIfNull(tr);
        ArgumentNullException.ThrowIfNull(thingName);
        const string Prefix = "belief:";
        var parts = knowledgeRef.StartsWith(Prefix, StringComparison.Ordinal) ? knowledgeRef[Prefix.Length..].Split('|') : [];
        if (parts.Length != 3 || !bool.TryParse(parts[2], out var value))
        {
            return null;
        }

        var word = tr(VocabKeys.Property(parts[1]));
        var property = word.Length > 0 ? char.ToLower(word[0], System.Globalization.CultureInfo.InvariantCulture) + word[1..] : word;
        return Ui.TextFill.Fill(tr(value ? "ui.writing.says" : "ui.writing.says_not"), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["thing"] = thingName(parts[0]),
            ["property"] = property,
        });
    }

    /// <summary>The words for how legible a text is: easy, faded or hardly readable (data thresholds).</summary>
    public static string LegibilityKey(double legibility, double faintBelow, double hardBelow) =>
        legibility <= hardBelow ? "ui.writing.hard" : legibility <= faintBelow ? "ui.writing.faint" : "ui.writing.legible";

    /// <summary>How opaque the marks are: full above the faint threshold, fading to a fifth as the text fades.</summary>
    public static double Alpha(double legibility, double faintBelow) =>
        faintBelow <= 0 || legibility >= faintBelow ? 1.0 : 0.2 + (0.8 * Math.Clamp(legibility / faintBelow, 0, 1));
}
