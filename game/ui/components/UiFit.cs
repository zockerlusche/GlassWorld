using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// Widths that follow the text (D705 A5/A6, review GW1 #226): a fixed number of pixels holds for one language
/// and one text size only. Measured with the control's own font each time the control enters the tree or its
/// theme changes (the text size is a player setting), from the translated text.
/// </summary>
public static class UiFit
{
    private const string MostMeta = "ui_fit_most";
    private const string LeastMeta = "ui_fit_least";
    private const string LimitMeta = "ui_fit_limit";

    /// <summary>The share of the picture's width a form field may take at most.</summary>
    public const float FieldShare = 0.27f;

    /// <summary>
    /// A text field is at least as wide as its hint text plus its frame, never narrower than
    /// <paramref name="least"/> and never wider than <paramref name="most"/> of the visible width (a share, 0..1).
    /// </summary>
    public static void HintFits(LineEdit field, float least, float most)
    {
        ArgumentNullException.ThrowIfNull(field);
        field.SetMeta(LeastMeta, least);
        field.SetMeta(MostMeta, most);
        Follow(field, () => FitHint(field, field.GetViewport()?.GetVisibleRect().Size.X));
    }

    /// <summary>
    /// Fits the fields below <paramref name="root"/> for a picture of the given width. A window calls this
    /// when it is placed: only then the picture's real size is certain (it can change after the field was built).
    /// </summary>
    public static void Refit(Control root, float pictureWidth)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var field in root.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Where(f => f.HasMeta(MostMeta)))
        {
            FitHint(field, pictureWidth);
        }
    }

    private static void FitHint(LineEdit field, float? pictureWidth)
    {
        var least = field.GetMeta(LeastMeta).AsSingle();
        var frame = field.GetThemeStylebox("normal")?.GetMinimumSize().X ?? 0;
        var wanted = Width(field, field.Tr(field.PlaceholderText)) + frame;
        var limit = pictureWidth is { } width ? width * field.GetMeta(MostMeta).AsSingle() : wanted;
        field.SetMeta(LimitMeta, limit);
        field.CustomMinimumSize = new Vector2(Math.Max(least, Math.Min(wanted, limit)), field.CustomMinimumSize.Y);
    }

    /// <summary>
    /// A wrapping label is at least as wide as its longest word, so no word breaks inside, and never
    /// narrower than <paramref name="least"/>.
    /// </summary>
    public static void LongestWordFits(Label label, float least)
    {
        ArgumentNullException.ThrowIfNull(label);
        void Fit()
        {
            var words = label.Tr(label.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var widest = words.Length == 0 ? 0 : words.Max(w => Width(label, w));
            label.CustomMinimumSize = new Vector2(Math.Max(least, widest), label.CustomMinimumSize.Y);
        }

        Follow(label, Fit);
    }

    /// <summary>
    /// Layout check: the texts below <paramref name="root"/> that are cut or broken inside a word - a hint
    /// text wider than its field, a wrapping label narrower than its longest word. Empty if all fit.
    /// </summary>
    public static IEnumerable<string> CutTexts(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var node in root.FindChildren("*", "Control", true, false).OfType<Control>().Where(c => c.IsVisibleInTree()))
        {
            if (node is LineEdit { Text.Length: 0, PlaceholderText.Length: > 0 } field)
            {
                var wanted = Width(field, field.Tr(field.PlaceholderText)) + (field.GetThemeStylebox("normal")?.GetMinimumSize().X ?? 0);
                // A field at its share of the picture may cut its hint: that is the limit of a small screen
                // with large text, not a layout fault.
                var atLimit = field.HasMeta(LimitMeta) && field.Size.X + 0.5f >= field.GetMeta(LimitMeta).AsSingle();
                if (field.Size.X + 0.5f < wanted && !atLimit)
                {
                    yield return $"hint '{field.PlaceholderText}' needs {wanted} px, its field has {field.Size.X}";
                }
            }
            else if (node is Label { AutowrapMode: not TextServer.AutowrapMode.Off, Text.Length: > 0 } label)
            {
                var widest = label.Tr(label.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => Width(label, w)).DefaultIfEmpty(0).Max();
                if (label.Size.X + 0.5f < widest)
                {
                    yield return $"label '{label.Text}' breaks inside a word ({widest} px needed, {label.Size.X} there)";
                }
            }
        }
    }

    // Measured anew when the control enters the tree, when its theme changes (text size) and when the
    // picture changes its size (window, UI scale): the share of the picture is only known then.
    private static void Follow(Control control, Action fit)
    {
        Viewport? view = null;
        control.Ready += fit;
        control.ThemeChanged += fit;
        control.TreeEntered += () =>
        {
            view = control.GetViewport();
            view.SizeChanged += fit;
        };
        control.TreeExiting += () =>
        {
            if (view is not null)
            {
                view.SizeChanged -= fit;
                view = null;
            }
        };
    }

    private static float Width(Control control, string text) =>
        (float)Math.Ceiling(control.GetThemeFont("font").GetStringSize(text, HorizontalAlignment.Left, -1, control.GetThemeFontSize("font_size")).X);
}
