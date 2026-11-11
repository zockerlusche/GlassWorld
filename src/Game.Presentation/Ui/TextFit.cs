namespace GlassWorld.Presentation.Ui;

/// <summary>
/// The rules of widths that follow their text (D705 A5/A6, review GW1 #226); the measuring itself (font,
/// text size) is the game's and is passed in. A form field is as wide as its hint text, between a least
/// width and a share of the picture; a wrapping label is as wide as its longest word.
/// </summary>
public static class TextFit
{
    private const double Tolerance = 0.5;

    /// <summary>
    /// The most a field may take: the share of the picture, but never less than the least width (on a
    /// small picture the least width wins). Null while the picture's width is not known.
    /// </summary>
    public static double? FieldLimit(double least, double? pictureWidth, double maxShare) =>
        pictureWidth is { } width ? Math.Max(least, width * maxShare) : null;

    /// <summary>The width of a field whose hint needs <paramref name="wanted"/> (text plus frame).</summary>
    public static double FieldWidth(double least, double wanted, double? pictureWidth, double maxShare) =>
        Math.Max(least, Math.Min(wanted, FieldLimit(least, pictureWidth, maxShare) ?? wanted));

    /// <summary>
    /// Whether a hint is cut by a layout fault: the field is narrower than the hint needs although it has
    /// not reached its limit. A field at its limit may cut - that is the end of the room, not a fault.
    /// </summary>
    public static bool HintCut(double fieldWidth, double wanted, double? limit) =>
        fieldWidth + Tolerance < wanted && !(limit is { } most && fieldWidth + Tolerance >= most);

    /// <summary>The width of the longest word of a text (0 for an empty one), measured by <paramref name="measure"/>.</summary>
    public static double LongestWord(string text, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measure);
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(measure).DefaultIfEmpty(0).Max();
    }

    /// <summary>Whether a wrapping label of the given width breaks a word inside.</summary>
    public static bool WordBroken(double labelWidth, double longestWord) => labelWidth + Tolerance < longestWord;
}
