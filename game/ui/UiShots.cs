using GlassWorld.Game.Localization;
using GlassWorld.Game.Ui.Screens;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui;

/// <summary>
/// Screenshot run for the self-acceptance (D366 section 2, KR5): shows the game screen with the
/// inhabitants panel and an inhabitant card at UI size 100 % and 175 % in DE and EN and saves one
/// PNG per case. Runs only with a real window through the AMW screen queue, e.g.
/// <c>--resolution 3840x1600 --fullscreen -- --ui-shots=&lt;dir&gt;</c>.
/// </summary>
public static class UiShots
{
    /// <summary>User argument that starts the run.</summary>
    public const string Argument = "--ui-shots=";

    private static readonly double[] Scales = [1.0, 1.75];

    /// <summary>Takes the screenshots and returns the written files.</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(Node host, GameScreen screen, UiStyle style, string folder)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(style);
        DirAccess.MakeDirRecursiveAbsolute(folder);
        var files = new List<string>();
        var size = host.GetWindow().Size;
        screen.ShowForShots();
        foreach (var locale in TextCatalogs.SupportedLocales)
        {
            TextCatalogs.SetLanguage(locale);
            foreach (var scale in Scales)
            {
                UiSetup.Apply(host.GetTree().Root, style.With(style.Settings with { UiScale = scale }));
                for (var i = 0; i < 20; i++)
                {
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                screen.Refresh();
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var path = System.IO.Path.Combine(folder, $"ui_{size.X}x{size.Y}_{locale}_{(int)(scale * 100)}.png");
                var error = host.GetViewport().GetTexture().GetImage().SavePng(path);
                GD.Print($"UI_SHOT {path} {error}");
                files.Add(path);
            }
        }

        // The zones preview of the UI concept (W1717, D642): the bare zones, a theme with tabs, the card in the
        // side window's place - in the first language at 100 %, enough to judge the layout.
        UiSetup.Apply(host.GetTree().Root, style);
        TextCatalogs.SetLanguage(TextCatalogs.SupportedLocales[0]);
        foreach (var (name, theme, card) in new[] { ("zones", (string?)null, false), ("zones_contact", "contact", false), ("zones_card", null, true) })
        {
            screen.ShowZonesForShots(true, theme, card);
            for (var i = 0; i < 20; i++)
            {
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            screen.Refresh();
            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var path = System.IO.Path.Combine(folder, $"ui_{size.X}x{size.Y}_{name}.png");
            var error = host.GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"UI_SHOT {path} {error}");
            files.Add(path);
        }

        screen.ShowZonesForShots(false);
        return files;
    }
}
