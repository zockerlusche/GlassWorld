using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>What the pause menu can do (B6 R5); the caller stops the world time while it is open.</summary>
/// <param name="Resume">Close the menu and play on.</param>
/// <param name="Save">Save now.</param>
/// <param name="Settings">Open the settings.</param>
/// <param name="MainMenu">Save and go back to the main menu.</param>
/// <param name="Quit">Save and quit.</param>
public sealed record PauseActions(Action Resume, Action Save, Action Settings, Action MainMenu, Action Quit)
{
    /// <summary>Share the world as one local file (F5234 C); null hides the button.</summary>
    public Action? Share { get; init; }

    /// <summary>
    /// Saves now and says whether it worked (W1689b); the menu then confirms inside the window. Null: the
    /// plain <see cref="PauseActions.Save"/> is used.
    /// </summary>
    public Func<bool>? SaveNow { get; init; }

    /// <summary>Leaves to the list of saved worlds (W1689b: loading is found from the pause menu); null hides it.</summary>
    public Action? Load { get; init; }
}

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
        // Saving answers inside the window (W1689b): "saving ..." first, then the time it was saved, or that it
        // failed - the player sees it worked without looking for a hint in the corner.
        var status = new Label
        {
            Visible = false,
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Button? save = null;
        save = Add(window, "ui.pause.save", () =>
        {
            if (actions.SaveNow is not { } saveNow)
            {
                actions.Save();
                return;
            }

            save!.Disabled = true;
            status.Text = window.Tr("ui.pause.saving");
            status.Visible = true;

            // One short moment so "saving ..." is drawn before the save (about half a second) runs.
            window.GetTree().CreateTimer(0.05).Timeout += () =>
            {
                var ok = saveNow();
                status.Text = ok
                    ? TextFill.Fill(window.Tr("ui.pause.saved_at"), new Dictionary<string, string> { ["time"] = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) })
                    : window.Tr("ui.pause.save_failed");
                save.Disabled = false;
            };
        });
        window.Content.AddChild(status);
        if (actions.Load is { } load)
        {
            Add(window, "ui.pause.load", () =>
            {
                leaving = true;
                window.Close();
                load();
            });
        }

        if (actions.Share is { } share)
        {
            Add(window, "ui.pause.share", share);
        }
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

    private static Button Add(GwWindow window, string key, Action pressed)
    {
        var button = new Button { Text = key };
        button.Pressed += pressed;
        window.Content.AddChild(button);
        return button;
    }
}
