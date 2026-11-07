using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>What the pause menu can do (B6 R5); the caller stops the world time while it is open.</summary>
/// <param name="Resume">Close the menu and play on.</param>
/// <param name="Save">Save now.</param>
/// <param name="Settings">Open the settings.</param>
/// <param name="MainMenu">Save and go back to the main menu.</param>
/// <param name="Quit">Save and quit.</param>
public sealed record PauseActions(Action Resume, Action Save, Action Settings, Action MainMenu, Action Quit);

/// <summary>Pause menu on Esc (B6 R5): play on, save, settings, main menu, quit.</summary>
public static class PauseMenu
{
    /// <summary>Builds the window; closing it (Esc or the close button) means "play on".</summary>
    public static GwWindow Create(UiStyle style, PauseActions actions)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(actions);
        var window = new GwWindow();
        window.Setup("ui.pause.title", style);
        var leaving = false;
        Add(window, "ui.pause.resume", window.Close);
        Add(window, "ui.pause.save", actions.Save);
        Add(window, "ui.menu.settings", actions.Settings);
        Add(window, "ui.pause.main_menu", () =>
        {
            leaving = true;
            window.Close();
            actions.MainMenu();
        });
        Add(window, "ui.menu.quit", () =>
        {
            leaving = true;
            window.Close();
            actions.Quit();
        });
        window.Closed += () =>
        {
            if (!leaving)
            {
                actions.Resume();
            }
        };
        return window;
    }

    private static void Add(GwWindow window, string key, Action pressed)
    {
        var button = new Godot.Button { Text = key };
        button.Pressed += pressed;
        window.Content.AddChild(button);
    }
}
