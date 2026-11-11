using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// Widths that follow the text (D705 A5/A6, review GW1 #226): a fixed number of pixels holds for one language
/// and one text size only. This class measures - with the control's own font, from the translated text, anew
/// when the control enters the tree, when its theme changes (the text size is a player setting) and when its
/// window is placed; the rules are <see cref="TextFit"/>.
/// </summary>
public static class UiFit
{
    private const string LeastMeta = "ui_fit_least";
    private const string ShareMeta = "ui_fit_share";
    private const string LimitMeta = "ui_fit_limit";

    /// <summary>
    /// A text field is as wide as its hint text plus its frame, never narrower than <paramref name="least"/>
    /// and never wider than the share <paramref name="maxShare"/> of the picture (data: window.field_max_share).
    /// </summary>
    public static void HintFits(LineEdit field, float least, float maxShare)
    {
        ArgumentNullException.ThrowIfNull(field);
        field.SetMeta(LeastMeta, least);
        field.SetMeta(ShareMeta, maxShare);
        void Fit() => FitHint(field, field.GetViewport()?.GetVisibleRect().Size.X);
        field.Ready += Fit;
        field.ThemeChanged += Fit;
    }

    /// <summary>
    /// Fits the fields below <paramref name="root"/> for a picture of the given width. A window calls this
    /// when it is placed: only then the picture's real size is certain (it can change after the field was built).
    /// </summary>
    public static void Refit(Control root, float pictureWidth)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var field in root.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Where(f => f.HasMeta(ShareMeta)))
        {
            FitHint(field, pictureWidth);
        }
    }

    /// <summary>
    /// A wrapping label is at least as wide as its longest word, so no word breaks inside, and never
    /// narrower than <paramref name="least"/>.
    /// </summary>
    public static void LongestWordFits(Label label, float least)
    {
        ArgumentNullException.ThrowIfNull(label);
        void Fit() => label.CustomMinimumSize = new Vector2(Math.Max(least, LongestWord(label)), label.CustomMinimumSize.Y);
        label.Ready += Fit;
        label.ThemeChanged += Fit;
    }

    /// <summary>
    /// Layout check: the texts below <paramref name="root"/> that are cut or broken inside a word - a hint
    /// text wider than its field (whether or not the field holds text now; a field at its limit may cut),
    /// a wrapping label narrower than its longest word. Empty if all fit.
    /// </summary>
    public static IEnumerable<string> CutTexts(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var node in root.FindChildren("*", "Control", true, false).OfType<Control>().Where(c => c.IsVisibleInTree()))
        {
            if (node is LineEdit { PlaceholderText.Length: > 0 } field)
            {
                var wanted = HintWidth(field);
                double? limit = field.HasMeta(LimitMeta) ? field.GetMeta(LimitMeta).AsDouble() : null;
                if (TextFit.HintCut(field.Size.X, wanted, limit))
                {
                    yield return $"hint '{field.PlaceholderText}' needs {wanted} px, its field has {field.Size.X}";
                }
            }
            else if (node is Label { AutowrapMode: not TextServer.AutowrapMode.Off, Text.Length: > 0 } label && TextFit.WordBroken(label.Size.X, LongestWord(label)))
            {
                yield return $"label '{label.Text}' breaks inside a word ({LongestWord(label)} px needed, {label.Size.X} there)";
            }
        }
    }

    private static void FitHint(LineEdit field, float? pictureWidth)
    {
        var least = field.GetMeta(LeastMeta).AsDouble();
        var share = field.GetMeta(ShareMeta).AsDouble();
        if (TextFit.FieldLimit(least, pictureWidth, share) is { } limit)
        {
            field.SetMeta(LimitMeta, limit);
        }

        field.CustomMinimumSize = new Vector2((float)TextFit.FieldWidth(least, HintWidth(field), pictureWidth, share), field.CustomMinimumSize.Y);
    }

    private static float HintWidth(LineEdit field) =>
        Width(field, field.Tr(field.PlaceholderText)) + (field.GetThemeStylebox("normal")?.GetMinimumSize().X ?? 0);

    private static float LongestWord(Label label) => (float)TextFit.LongestWord(label.Tr(label.Text), word => Width(label, word));

    private static float Width(Control control, string text) =>
        (float)Math.Ceiling(control.GetThemeFont("font").GetStringSize(text, HorizontalAlignment.Left, -1, control.GetThemeFontSize("font_size")).X);
}
