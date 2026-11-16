namespace GlassWorld.Presentation.Ui;

/// <summary>
/// A1815 (A1749 rest, D957 v4 section 10): the keys the player can lay anew - camera and time. The settings data names one
/// row <c>key.&lt;action&gt;</c> per action. A chosen key replaces the action's keys (mouse buttons and wheel stay); without a
/// choice the defaults hold. Two actions on one key are a conflict, shown in words. Escape stays the menu's key.
/// </summary>
public static class KeyBindings
{
    /// <summary>Row prefix in content/ui/settings.json: <c>key.gw_camera_pan_left</c>.</summary>
    public const string RowPrefix = "key.";

    /// <summary>Keys no action may take (the menu and windows close with Escape).</summary>
    public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.Ordinal) { "Escape" };

    /// <summary>The action of a settings row, or null when the row is no key row.</summary>
    public static string? ActionOfRow(string row) =>
        row is not null && row.StartsWith(RowPrefix, StringComparison.Ordinal) && row.Length > RowPrefix.Length ? row[RowPrefix.Length..] : null;

    /// <summary>Text key of an action's name.</summary>
    public static string LabelKey(string action) => "ui.settings.key." + action;

    /// <summary>
    /// The keys each action answers to: the chosen key alone where the player chose one, else the defaults. Actions in the
    /// order of <paramref name="defaults"/>.
    /// </summary>
    public static IReadOnlyList<(string Action, IReadOnlyList<string> Keys)> Effective(
        IReadOnlyList<(string Action, IReadOnlyList<string> Keys)> defaults, IReadOnlyDictionary<string, string> chosen)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(chosen);
        return [.. defaults.Select(d => (d.Action, chosen.TryGetValue(d.Action, out var key) ? (IReadOnlyList<string>)[key] : d.Keys))];
    }

    /// <summary>The keys more than one action answers to, each with its actions (in order); empty without a conflict.</summary>
    public static IReadOnlyList<(string Key, IReadOnlyList<string> Actions)> Conflicts(IReadOnlyList<(string Action, IReadOnlyList<string> Keys)> effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (action, keys) in effective)
        {
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                if (!byKey.TryGetValue(key, out var actions))
                {
                    byKey[key] = actions = [];
                    order.Add(key);
                }

                actions.Add(action);
            }
        }

        return [.. order.Where(k => byKey[k].Count > 1).Select(k => (k, (IReadOnlyList<string>)byKey[k]))];
    }

    /// <summary>Only known actions with a usable key: not empty, not reserved (a hand-edited file cannot take Escape).</summary>
    public static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string>? chosen, IReadOnlyCollection<string> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return (chosen ?? new Dictionary<string, string>())
            .Where(c => actions.Contains(c.Key) && !string.IsNullOrWhiteSpace(c.Value) && !Reserved.Contains(c.Value))
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
    }
}
