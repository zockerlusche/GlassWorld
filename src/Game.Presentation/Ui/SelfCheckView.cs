using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Ui;

/// <summary>One line of the AI self-check as the settings show it (W1703): text keys and the names for the text.</summary>
/// <param name="AiKey">Name of the AI (<c>ui.selfcheck.ai.*</c>).</param>
/// <param name="StateKey">Short state (<c>ui.selfcheck.state.*</c>).</param>
/// <param name="MessageKey">The clear text from the service (<c>msg.selfcheck.*</c>).</param>
/// <param name="FixKey">What the player can do, or null.</param>
/// <param name="Detail">Identifiers only (never sentences: raw text would bypass DE/EN, review GW1 #176). Names that belong into the text (e.g. a missing ComfyUI extension), comma-separated; empty if none.</param>
/// <param name="Problem">True when the player can fix something (state problem).</param>
public sealed record SelfCheckLine(string AiKey, string StateKey, string MessageKey, string? FixKey, string Detail, bool Problem);

/// <summary>
/// The AI self-check for the settings (W1703 rules 2 and 3): a calm line per AI, problems first so the player
/// sees what to do. Godot-free; the keys of the service are used as they come (checked against the catalogue
/// by <see cref="Line"/>'s caller, unknown keys fall back to the general text).
/// </summary>
public static class SelfCheckView
{
    /// <summary>Text when the service gave no self-check (not running, too old, no answer).</summary>
    public const string Unavailable = "ui.selfcheck.unavailable";

    /// <summary>Text while the self-check runs.</summary>
    public const string Checking = "ui.selfcheck.checking";

    /// <summary>Fallback for a message key the game does not know (a newer service).</summary>
    public const string UnknownMessage = "ui.selfcheck.unknown_message";

    /// <summary>The lines of a self-check: problems first, then the order of the service.</summary>
    public static IReadOnlyList<SelfCheckLine> Lines(SelfCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return [.. check.Checks.Select(Line).Select((line, index) => (line, index)).OrderBy(x => x.line.Problem ? 0 : 1).ThenBy(x => x.index).Select(x => x.line)];
    }

    /// <summary>Whether any AI needs the player (a calm hint at start, W1703 rule 2).</summary>
    public static bool NeedsPlayer(SelfCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Checks.Any(c => c.State == AiCheckState.Problem);
    }

    /// <summary>The line of one AI.</summary>
    public static SelfCheckLine Line(AiCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return new SelfCheckLine(
            "ui.selfcheck.ai." + Id(check.Ai),
            "ui.selfcheck.state." + Id(check.State),
            check.MessageKey,
            check.FixKey,
            string.Join(", ", check.Detail),
            check.State == AiCheckState.Problem);
    }

    private static string Id(AiCheckAi ai) => ai switch
    {
        AiCheckAi.Language => "language",
        AiCheckAi.Image => "image",
        _ => "sound",
    };

    private static string Id(AiCheckState state) => state switch
    {
        AiCheckState.Ok => "ok",
        AiCheckState.Preparing => "preparing",
        AiCheckState.Problem => "problem",
        AiCheckState.Not_offered => "not_offered",
        _ => "not_planned",
    };
}
