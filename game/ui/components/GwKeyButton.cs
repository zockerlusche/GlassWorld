using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// A1815: a button that shows an action's key; pressed, it waits for the next key and hands it on (Escape cancels). While it
/// waits it takes every key, so nothing else reacts to the key being laid.
/// </summary>
public partial class GwKeyButton : Button
{
    private bool _waiting;

    /// <summary>The physical key pressed while waiting.</summary>
    public event Action<Key>? KeyChosen;

    /// <summary>Text shown while waiting (a text key).</summary>
    public string WaitingText { get; set; } = string.Empty;

    /// <summary>Text shown otherwise (the key as the player sees it, not translated).</summary>
    public string KeyText { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override void _Ready()
    {
        ToggleMode = false;
        Pressed += () =>
        {
            _waiting = true;
            AutoTranslateMode = AutoTranslateModeEnum.Always;
            Text = WaitingText;
        };
        ShowKey();
    }

    /// <summary>Shows the key (after a choice or a cancel).</summary>
    public void ShowKey()
    {
        _waiting = false;
        AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        Text = KeyText;
    }

    /// <inheritdoc/>
    public override void _Input(InputEvent @event)
    {
        if (!_waiting || @event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        GetViewport().SetInputAsHandled();
        if (key.PhysicalKeycode is Key.Escape or Key.None)
        {
            ShowKey();
            return;
        }

        _waiting = false;
        KeyChosen?.Invoke(key.PhysicalKeycode);
        ShowKey();
    }
}
