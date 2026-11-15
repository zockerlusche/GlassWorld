using System.Globalization;

namespace GlassWorld.Simulation.Decisions;

/// <summary>
/// A1587 (B2804, D1161 S1, B7 R23-R26): what the core knows about how fast the AI can keep up - the game's speed,
/// whom the camera follows and how long one AI job takes on this PC. All three reach the core only as logged
/// player commands (<see cref="SetSpeed"/>, <see cref="SetFollowed"/>, <see cref="SetAiRate"/>), so a replay with
/// the seed and the log sees the same values at the same ticks. After loading a save game the last logged command
/// of each kind counts (<see cref="FromLog"/>). They decide what may go to the AI in a game hour (S2, S3); by
/// themselves they change nothing in the world, so they stay out of the state hash like the player's language.
/// </summary>
public sealed class AiPace
{
    /// <summary>Command: the game's speed factor (<see cref="PlayerCommand.Amount"/>: 1 or a fast-forward factor of time.json); a pause is not sent.</summary>
    public const string SetSpeed = "set_speed";

    /// <summary>Command: whom the camera follows (<see cref="PlayerCommand.InhabitantId"/>; null = nobody).</summary>
    public const string SetFollowed = "set_followed";

    /// <summary>Command: how long one AI job takes on this PC in milliseconds (<see cref="PlayerCommand.Amount"/>), as the service measured it (R6).</summary>
    public const string SetAiRate = "set_ai_rate";

    /// <summary>The longest job time a <see cref="SetAiRate"/> may state (10 minutes); anything above is no measurement.</summary>
    public const int MaxJobMs = 600_000;

    private readonly HashSet<int> _speeds;

    /// <summary>Creates the pace with the speeds the game offers.</summary>
    /// <param name="fastForwardFactors">The fast-forward factors of time.json; 1 (normal) is always allowed.</param>
    public AiPace(IEnumerable<int> fastForwardFactors)
    {
        ArgumentNullException.ThrowIfNull(fastForwardFactors);
        _speeds = fastForwardFactors.Append(1).ToHashSet();
    }

    /// <summary>The game's speed factor: 1 = normal (also without any command), 4/16 = fast forward.</summary>
    public int Speed { get; private set; } = 1;

    /// <summary>The inhabitant the camera follows, or null.</summary>
    public string? FollowedId { get; private set; }

    /// <summary>Milliseconds one AI job takes on this PC as last measured, or null (not measured: the data's fallback counts).</summary>
    public int? JobMs { get; private set; }

    /// <summary>Checks a <see cref="SetSpeed"/>: one of the game's speeds.</summary>
    public CommandResult CheckSpeed(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Amount is { } speed && _speeds.Contains(speed)
            ? new CommandResult(true, null)
            : new CommandResult(false, DecisionsSystem.ReasonUnknownCommand);
    }

    /// <summary>Checks a <see cref="SetAiRate"/>: a positive time up to <see cref="MaxJobMs"/>.</summary>
    public static CommandResult CheckAiRate(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Amount is > 0 and <= MaxJobMs
            ? new CommandResult(true, null)
            : new CommandResult(false, DecisionsSystem.ReasonUnknownCommand);
    }

    /// <summary>Applies one of the three commands (checked before).</summary>
    public void Apply(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Kind)
        {
            case SetSpeed:
                Speed = command.Amount ?? 1;
                break;
            case SetFollowed:
                FollowedId = command.InhabitantId;
                break;
            case SetAiRate:
                JobMs = command.Amount;
                break;
        }
    }

    /// <summary>
    /// After a restore: the last logged command of each kind (normal speed, nobody followed and not measured
    /// without one), so the world goes on as it was saved.
    /// </summary>
    public void FromLog(DecisionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        Speed = 1;
        FollowedId = null;
        JobMs = null;
        foreach (var entry in log.Entries)
        {
            if (entry.Kind == DecisionLogKind.Command && Mentions(entry.Json)
                && System.Text.Json.JsonSerializer.Deserialize<PlayerCommand>(entry.Json) is { Kind: SetSpeed or SetFollowed or SetAiRate } command)
            {
                Apply(command);
            }
        }
    }

    /// <summary>For the log and the run report: speed, followed, job time.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"x{Speed} followed={FollowedId ?? "-"} job_ms={(JobMs is { } ms ? ms.ToString(CultureInfo.InvariantCulture) : "-")}");

    private static bool Mentions(string json) =>
        json.Contains(SetSpeed, StringComparison.Ordinal) || json.Contains(SetFollowed, StringComparison.Ordinal) || json.Contains(SetAiRate, StringComparison.Ordinal);
}
