using GlassWorld.Game.World;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// A1815 (A1749 rest): the keys of the camera and the time buttons. They are made at the start (so the settings know them in
/// the main menu too), their defaults are remembered once, and the player's choice replaces an action's keys - mouse buttons
/// and the wheel stay. Keys are kept by their physical place (the same key on every keyboard layout).
/// </summary>
public static class InputDefaults
{
    private static Dictionary<string, IReadOnlyList<string>>? _defaults;

    /// <summary>Makes the camera and time actions with their default keys (once) and remembers those defaults.</summary>
    /// <param name="timeChoices">How many time buttons there are (pause, normal, the fast factors of time.json).</param>
    public static void Ensure(int timeChoices)
    {
        CameraRig.EnsureDefaultInput();
        for (var i = 0; i < timeChoices; i++)
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

        _defaults ??= InputMap.GetActions().Select(a => a.ToString()).Where(a => a.StartsWith("gw_", StringComparison.Ordinal))
            .ToDictionary(a => a, a => (IReadOnlyList<string>)[.. KeysOf(a)], StringComparer.Ordinal);
    }

    /// <summary>The default keys of these actions (in their order); an action the game does not know has none.</summary>
    public static IReadOnlyList<(string Action, IReadOnlyList<string> Keys)> Defaults(IEnumerable<string> actions) =>
        [.. actions.Select(a => (a, _defaults?.GetValueOrDefault(a) ?? (IReadOnlyList<string>)[]))];

    /// <summary>Puts the player's keys into effect: a chosen key replaces the action's keys, else its defaults come back.</summary>
    public static void Apply(IEnumerable<string> actions, IReadOnlyDictionary<string, string> chosen)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(chosen);
        foreach (var action in actions)
        {
            if (!InputMap.HasAction(action))
            {
                continue;
            }

            foreach (var key in InputMap.ActionGetEvents(action).OfType<InputEventKey>().ToList())
            {
                InputMap.ActionEraseEvent(action, key);
            }

            IReadOnlyList<string> keys = chosen.TryGetValue(action, out var mine) ? [mine] : _defaults?.GetValueOrDefault(action) ?? [];
            foreach (var name in keys)
            {
                var code = OS.FindKeycodeFromString(name);
                if (code != Key.None)
                {
                    InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = code });
                }
            }
        }
    }

    /// <summary>The name a key is kept by (its physical place, e.g. 'W', 'Left', 'Space').</summary>
    public static string NameOf(Key physical) => OS.GetKeycodeString(physical);

    /// <summary>What the player sees for a kept key: the letter of the key on this keyboard layout.</summary>
    public static string Shown(string name)
    {
        // Without a screen (headless check runs) the display server has no keyboard layout: the kept name is shown.
        var physical = OS.FindKeycodeFromString(name);
        return physical == Key.None || DisplayServer.GetName() == "headless"
            ? name
            : OS.GetKeycodeString(DisplayServer.KeyboardGetKeycodeFromPhysical(physical));
    }

    private static IEnumerable<string> KeysOf(string action) =>
        InputMap.ActionGetEvents(action).OfType<InputEventKey>()
            .Select(k => k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode)
            .Where(k => k != Key.None)
            .Select(NameOf);
}
