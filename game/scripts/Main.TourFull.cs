using GlassWorld.Game.Localization;
using GlassWorld.Game.Ui.Screens;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// The full UI tour (TL #24631, Marcel: "clicked through to the depth?"): after the normal tour, every card,
/// the conversation, the pointing mode, the help and mailbox actions, the chronicle pages, the pause actions,
/// the settings in English, with large and small UI, high contrast and hidden UI - one picture each. Buttons
/// are pressed by their text key, as a player would click them. Nothing that writes outside the throwaway
/// tour world is pressed (no export, no share, no delete): the tour world has no save place.
/// </summary>
public partial class Main
{
    private const string TourFullArgument = "--ui-tour-full";
    private bool _tourFull;

    /// <summary>Reads <c>--ui-tour-full</c>; true when the argument was it.</summary>
    private bool ReadTourFullArgument(string arg)
    {
        if (!string.Equals(arg, TourFullArgument, StringComparison.Ordinal))
        {
            return false;
        }

        _tourFull = true;
        return true;
    }

    /// <summary>The first button under <paramref name="root"/> whose text key is <paramref name="key"/>, or null.</summary>
    private static BaseButton? ButtonWithKey(Node root, string key)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is Button { Visible: true } button && string.Equals(button.Text, key, StringComparison.Ordinal))
            {
                return button;
            }

            if (child is Control { Visible: false })
            {
                continue;
            }

            if (ButtonWithKey(child, key) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Presses the button with the text key as a click would; false (and a log line) when there is none.</summary>
    private static bool Press(Node root, string key)
    {
        if (ButtonWithKey(root, key) is not { Disabled: false } button)
        {
            GD.Print($"UI_TOUR_FULL no button {key}");
            return false;
        }

        // A click on a toggle flips it (toggled) and then presses it: handlers that listen to Pressed run too.
        if (button.ToggleMode)
        {
            button.ButtonPressed = !button.ButtonPressed;
        }

        button.EmitSignal(BaseButton.SignalName.Pressed);
        return true;
    }

    private async Task FullTourAsync(string first, Func<string, Task> take)
    {
        var screen = _screen!;
        var ids = _world!.Host!.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => i.Id).ToList();
        screen.ShowZonesForShots(false);

        // 30. Every inhabitant's card, the last one unfolded; the conversation and the pointing mode.
        for (var i = 0; i < ids.Count; i++)
        {
            screen.ShowCardForShots(ids[i], expand: false);
            await take($"30_karte_{i + 1}_{ids[i]}");
        }

        screen.ShowCardForShots(ids[^1], expand: true);
        await take("30_karte_letzte_aufgeklappt");
        screen.ShowCardForShots(first, expand: false);
        screen.ShowTalkForShots("Hallo, was machst du gerade?");
        await take("31_gespraech_gesendet");
        if (Press(screen, "ui.card.point"))
        {
            await take("31_zeigen_modus");
            Press(screen, "ui.card.point");
        }

        screen.ShowCardForShots(null, expand: false);

        // 32. Side windows a step deeper: earlier chronicle day, mailbox history, help actions.
        screen.OpenPanelForShots("chronicle");
        if (Press(screen, "ui.chronicle.earlier"))
        {
            await take("32_chronik_frueher");
        }

        if (Press(screen, "ui.chronicle.later"))
        {
            await take("32_chronik_spaeter");
        }

        // The mailbox shows its history below the open messages (a heading, nothing to press).
        screen.OpenPanelForShots("mailbox");
        await take("32_postfach_verlauf");

        // A gift button's text is filled in (thing and cost): it is found by its name. Pressing it only starts
        // placing; nothing is placed without a click into the world.
        screen.OpenPanelForShots("help");
        if (screen.FindChildren($"*{GameScreen.HelpGiveName}*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.IsVisibleInTree() && !b.Disabled) is { } give)
        {
            give.EmitSignal(BaseButton.SignalName.Pressed);
            await take("32_hilfe_gabe");
        }
        else
        {
            GD.Print($"UI_TOUR_FULL no button {GameScreen.HelpGiveName}");
        }

        screen.OpenPanelForShots(null);

        // 33. The pause menu's own steps: "save" in a world without a save place, then back.
        OpenPause();
        if (_pause is not null && Press(_pause, "ui.pause.save"))
        {
            await take("33_pause_speichern");
        }

        _pause?.Close();
        await Frames(2);

        // 34. Display variants: hidden UI, high contrast, large and small UI and text.
        var normal = _settings;
        foreach (var (tag, variant) in new (string, PlayerSettings)[]
        {
            ("ui_ausgeblendet", normal with { HideUi = true }),
            ("hoher_kontrast", normal with { HighContrast = true }),
            ("ui_gross_150", normal with { UiScale = 1.5, TextScale = 1.5 }),
            ("ui_klein_075", normal with { UiScale = 0.75, TextScale = 0.75 }),
        })
        {
            ApplySettings(variant);
            await take($"34_{tag}_spiel");
            if (!variant.HideUi)
            {
                screen.ShowCardForShots(first, expand: true);
                await take($"34_{tag}_karte");
                screen.ShowCardForShots(null, expand: false);
            }
        }

        ApplySettings(normal);

        // 35. English: the game, the card, every side window and every settings tab (B2470).
        ApplySettings(normal with { Language = "en" });
        TextCatalogs.SetLanguage("en");
        await take("35_en_spiel");
        screen.ShowCardForShots(first, expand: true);
        await take("35_en_karte");
        screen.ShowCardForShots(null, expand: false);
        foreach (var key in GameScreen.PanelKeysForShots(false))
        {
            screen.OpenPanelForShots(key);
            await take($"35_en_{key}");
        }

        screen.OpenPanelForShots(null);
        SettingsWindow? settings = null;
        OpenSettings(window =>
        {
            settings = window as SettingsWindow;
            screen.ShowWindow(window, modal: true);
        });
        if (settings is not null)
        {
            foreach (var tab in settings.TabKeys)
            {
                settings.ShowTab(tab);
                await take($"35_en_einstellungen_{tab.Replace("ui.settings.section.", string.Empty, StringComparison.Ordinal)}");
            }

            settings.Close();
        }

        ApplySettings(normal with { Language = "de" });
        TextCatalogs.SetLanguage("de");
        await Frames(2);
    }
}
