using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Safety;

namespace GlassWorld.Presentation.World;

/// <summary>What the player chose for a new world (B6 R3); options come from content/player.</summary>
/// <param name="Name">World name (player input, checked).</param>
/// <param name="Seed">World seed (shown, so a world can be made again).</param>
/// <param name="Size">Size option id.</param>
/// <param name="Climate">Climate option id.</param>
/// <param name="StartFavor">Start favour, one of the start options of content/player/favor.json.</param>
public sealed record NewWorldChoice(string Name, ulong Seed, string Size, string Climate, int StartFavor);

/// <summary>
/// Checks of the "new world" form without Godot: the name is a player input with a length limit from
/// the data and goes through the content guard (R13); the seed is a number or empty (then one is drawn
/// and shown).
/// </summary>
public static class NewWorldModel
{
    /// <summary>Text key of the problem with a world name, or null if it is fine.</summary>
    public static string? NameProblem(string? name, WorldOptions options, TextGuard guard)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(guard);
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "ui.new_world.problem.name_empty";
        }

        if (trimmed.Length > options.NameMaxLength)
        {
            return "ui.new_world.problem.name_long";
        }

        if (trimmed.Any(char.IsControl))
        {
            return "ui.new_world.problem.name_characters";
        }

        return guard.Check(trimmed).Allowed ? null : "ui.new_world.problem.name_blocked";
    }

    /// <summary>Reads the seed field: empty = draw one (<c>Ok</c> with null), a whole number, or not ok.</summary>
    public static (bool Ok, ulong? Seed) ParseSeed(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return (true, null);
        }

        return ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var seed) ? (true, seed) : (false, null);
    }

    /// <summary>
    /// A fresh seed from the wall clock when the player leaves the field empty. Presentation only: the
    /// seed is then fixed, shown and stored, so the world itself stays reproducible.
    /// </summary>
    public static ulong DrawSeed(DateTimeOffset now)
    {
        // SplitMix64 finaliser: nearby times give unrelated seeds.
        var z = (ulong)now.ToUnixTimeMilliseconds() + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (z ^ (z >> 31)) % 1_000_000_000UL;
    }
}
