using GlassWorld.Game.Localization;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui;

/// <summary>
/// Headless layout check of the UI standard (D366 section 14): a sample window in both reference
/// resolutions (KR5), at UI size 75/100/200 % and text size 100/200 %, in DE and EN. It must stay in
/// the safe area, keep its header readable and fit the allowed share of the screen.
/// </summary>
public static class UiLayoutCheck
{
    private static readonly (int Width, int Height)[] Resolutions = [(1920, 1080), (3840, 1600)];
    private static readonly double[] UiScales = [0.75, 1.0, 2.0];
    private static readonly double[] TextScales = [1.0, 2.0];

    /// <summary>Runs all cases below <paramref name="host"/>; returns the problems (empty if fine).</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(Node host, UiStyle baseStyle)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(baseStyle);
        var problems = new List<string>();
        var startLocale = TranslationServer.GetLocale();
        var cases = 0;
        try
        {
            foreach (var locale in TextCatalogs.SupportedLocales)
            {
                TextCatalogs.SetLanguage(locale);
                foreach (var (width, height) in Resolutions)
                {
                    foreach (var uiScale in UiScales)
                    {
                        foreach (var textScale in TextScales)
                        {
                            cases++;
                            var style = baseStyle.With(baseStyle.Settings with { UiScale = uiScale, TextScale = textScale });
                            var (w, h) = UiScaling.LogicalSize(width, height, style.Tokens, style.ContentScaleFactor);
                            var label = $"{locale} {width}x{height} ui {uiScale:P0} text {textScale:P0}";
                            problems.AddRange((await CheckOne(host, style, new Vector2((float)w, (float)h))).Select(p => $"{label}: {p}"));
                        }
                    }
                }
            }
        }
        finally
        {
            TranslationServer.SetLocale(startLocale);
        }

        GD.Print($"UI_CHECK cases={cases} problems={problems.Count}");
        return problems;
    }

    private static async Task<IReadOnlyList<string>> CheckOne(Node host, UiStyle style, Vector2 area)
    {
        var stage = new Control { Size = area, Theme = UiTheme.Build(style) };
        host.AddChild(stage);
        var window = new GwWindow();
        window.Setup("ui.settings.title", style);
        foreach (var key in new[] { "vocab.need.hunger", "vocab.need.thirst", "vocab.need.sleep", "vocab.need.warmth", "vocab.need.safety", "vocab.need.company", "vocab.need.fun", "vocab.need.purpose" })
        {
            window.Content.AddChild(new Label { Text = key });
        }

        window.AddAction("ui.common.close");
        stage.AddChild(window);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        window.FitInto(area);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

        var problems = new List<string>();
        if (!window.IsInsideSafeArea(area))
        {
            problems.Add($"window {window.Position}+{window.Size} leaves the safe area of {area}");
        }

        var header = window.HeaderProblem();
        if (header.Length > 0)
        {
            problems.Add("header: " + header);
        }

        if (window.Size.X > area.X * style.Tokens.Window.MaxScreenFraction + 0.5f || window.Size.Y > area.Y * style.Tokens.Window.MaxScreenFraction + 0.5f)
        {
            problems.Add("window larger than the allowed share of the screen");
        }

        stage.QueueFree();
        return problems;
    }
}
