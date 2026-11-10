using GlassWorld.Game.World;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>
/// Time keys of the game screen (B2-6 R31): one InputMap action per time button (pause, normal, the
/// fast-forward factors of the data; <see cref="ScreenModel.TimeAction"/>). A key presses its button, so
/// keys and buttons do the same; the pause key toggles. Defaults are added only where the project
/// settings do not define the action (rebindable, like the camera).
/// </summary>
public partial class GameScreen
{
    private int _timeBeforePause = 1;
    private bool _timeKeysReady;

    /// <summary>
    /// Presses the time button of a time key (0 = pause, 1 = normal, 2… = fast forward); pause while paused
    /// goes back to the speed before. Nothing happens while the time buttons are off (pause menu).
    /// </summary>
    /// <returns>Whether a button was pressed.</returns>
    public bool PressTimeKey(int key)
    {
        var current = _timeButtons.FindIndex(b => b.Button.ButtonPressed);
        if (key < 0 || key >= _timeButtons.Count || current < 0 || _timeButtons[key].Button.Disabled)
        {
            return false;
        }

        if (current != 0)
        {
            _timeBeforePause = current;
        }

        var button = _timeButtons[Math.Min(ScreenModel.TimeKeyTarget(key, current, _timeBeforePause), _timeButtons.Count - 1)].Button;
        button.ButtonPressed = true;
        button.EmitSignal(BaseButton.SignalName.Pressed);
        return true;
    }

    /// <summary>
    /// Handles a time key. Keys belong to a focused text field, slider or list first (the camera's rule,
    /// <see cref="CameraRig.BlocksCameraKeys"/>). <paramref name="beforeGui"/> is the early pass: only while
    /// a time button has the focus (it was just clicked), so the pause key pauses instead of pressing
    /// that button again; every other focused button keeps its keys.
    /// </summary>
    private bool TimeKey(InputEvent @event, bool beforeGui)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } || _timeButtons.Count == 0)
        {
            return false;
        }

        var focus = GetViewport().GuiGetFocusOwner();
        if (CameraRig.BlocksCameraKeys(focus) || (beforeGui && !_timeButtons.Any(b => b.Button == focus)))
        {
            return false;
        }

        EnsureTimeKeys();
        for (var i = 0; i < _timeButtons.Count; i++)
        {
            if (@event.IsActionPressed(ScreenModel.TimeAction(i)) && PressTimeKey(i))
            {
                GetViewport().SetInputAsHandled();
                return true;
            }
        }

        return false;
    }

    /// <summary>Default keys: Space and P pause, 1 is normal, 2, 3, … the fast-forward factors.</summary>
    private void EnsureTimeKeys()
    {
        if (_timeKeysReady)
        {
            return;
        }

        _timeKeysReady = true;
        for (var i = 0; i < _timeButtons.Count; i++)
        {
            var action = ScreenModel.TimeAction(i);
            if (InputMap.HasAction(action))
            {
                continue;
            }

            InputMap.AddAction(action);
            if (i == 0)
            {
                InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = Key.Space });
                InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = Key.P });
            }
            else if (i <= 9)
            {
                InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = Key.Key0 + i });
            }
        }
    }
}
