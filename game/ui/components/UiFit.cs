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
    /// Like <see cref="LongestWordFits(Label, float)"/> for a label that shows one of several texts in turn (the
    /// settings' description): wide enough for the longest word of any of them, so none breaks inside a word.
    /// </summary>
    public static void LongestWordFits(Label label, float least, IReadOnlyCollection<string> texts)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(texts);
        void Fit() => label.CustomMinimumSize = new Vector2(Math.Max(least, texts.Count == 0 ? 0 : texts.Max(t => LongestWord(label, t))), label.CustomMinimumSize.Y);
        label.Ready += Fit;
        label.ThemeChanged += Fit;
    }

    /// <summary>
    /// The height <paramref name="control"/> needs at <paramref name="width"/>, measured now and also while it is hidden
    /// (B2791: the settings hold the room of their highest tab). Its containers are laid out at once, three times, since
    /// a wrapping label is only as high as the width its container gave it; a label's cached size is renewed by hand,
    /// as Godot renews it only when the label is drawn. A control outside a container keeps its own minimum.
    /// A hidden control is shown for the measuring and hidden again at once (no frame is drawn between), so its
    /// visibility_changed handlers run twice (review GW3 #493): such handlers must not start work of their own.
    /// </summary>
    public static float HeightAt(Control control, float width)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (control.GetParent() is not Container parent)
        {
            return control.GetCombinedMinimumSize().Y;
        }

        var shown = control.Visible;
        control.Visible = true;
        for (var pass = 0; pass < 3; pass++)
        {
            parent.FitChildInRect(control, new Rect2(control.Position, new Vector2(width, control.GetCombinedMinimumSize().Y)));
            LayOutNow(control);
        }

        var height = control.GetCombinedMinimumSize().Y;
        control.Visible = shown;
        return height;
    }

    /// <summary>
    /// The height of the highest of <paramref name="texts"/> in <paramref name="label"/> at <paramref name="width"/>,
    /// without the room reserved so far (a label that shows one text after another keeps the room of the longest).
    /// The label keeps its text and its minimum size.
    /// </summary>
    public static float HighestText(Label label, IReadOnlyCollection<string> texts, float width)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(texts);
        var text = label.Text;
        var least = label.CustomMinimumSize;
        label.CustomMinimumSize = new Vector2(least.X, 0);
        var highest = 0f;
        foreach (var candidate in texts)
        {
            label.Text = candidate;
            highest = Math.Max(highest, HeightAt(label, width));
        }

        label.Text = text;
        label.CustomMinimumSize = least;
        HeightAt(label, width);
        return highest;
    }

    private static void LayOutNow(Control control)
    {
        if (control is Label)
        {
            control.UpdateMinimumSize();
        }

        if (control is Container container)
        {
            container.Notification((int)Container.NotificationSortChildren);
        }

        foreach (var child in control.GetChildren().OfType<Control>().Where(c => c.Visible))
        {
            LayOutNow(child);
        }
    }

    /// <summary>
    /// A label whose numbers change (the clock, the date) keeps one width while they do: it takes the width of its text
    /// with every digit as wide as the widest digit of its font, so a minute or a day passing moves nothing beside it
    /// (A1762, Marcel W2183: the time block jittered). Call it after each new text; only a new number of digits or
    /// letters changes the width.
    /// </summary>
    public static void SteadyDigits(Label label, string? widestSample = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        var widest = WidestDigit(label);
        var steady = new string([.. label.Text.Select(c => char.IsAsciiDigit(c) ? widest : c)]);
        var width = Width(label, steady);
        if (widestSample is not null)
        {
            // A1800: a value whose digit count changes (the temperature: 9, 10, -3) keeps the room of its widest form.
            width = Math.Max(width, Width(label, new string([.. widestSample.Select(c => char.IsAsciiDigit(c) ? widest : c)])));
        }

        label.CustomMinimumSize = new Vector2(width, label.CustomMinimumSize.Y);
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

    private static float LongestWord(Label label) => LongestWord(label, label.Text);

    private static float LongestWord(Label label, string text) => (float)TextFit.LongestWord(label.Tr(text), word => Width(label, word));

    // The widest digit per font and size (review GW3 #496: the clock asks four times a second; a new text size is a new key).
    private static readonly Dictionary<(Rid Font, int Size), char> WidestDigits = [];

    private static char WidestDigit(Label label)
    {
        var key = (label.GetThemeFont("font").GetRid(), label.GetThemeFontSize("font_size"));
        if (!WidestDigits.TryGetValue(key, out var widest))
        {
            widest = "0123456789".MaxBy(d => Width(label, d.ToString()));
            WidestDigits[key] = widest;
        }

        return widest;
    }

    private static float Width(Control control, string text) =>
        (float)Math.Ceiling(control.GetThemeFont("font").GetStringSize(text, HorizontalAlignment.Left, -1, control.GetThemeFontSize("font_size")).X);
}
