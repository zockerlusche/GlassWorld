using System.Text.Json;
using System.Text.Json.Serialization;
using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Ui;

/// <summary>
/// The player's settings (B6 R4): stored in the user folder (<c>user://settings.json</c>), never in a
/// world save and never sent to the AI. Unknown or broken files fall back to the defaults; values are
/// clamped to the token ranges, so a hand-edited file cannot break the UI.
/// </summary>
public sealed record PlayerSettings
{
    /// <summary>Format version of the file.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Game language: <c>de</c> or <c>en</c>; null = system language on first start.</summary>
    public string? Language { get; init; }

    /// <summary>UI size 0.75..2 (B2513).</summary>
    public double UiScale { get; init; } = 1;

    /// <summary>Text size 0.75..2 (XAG 101).</summary>
    public double TextScale { get; init; } = 1;

    /// <summary>High-contrast palette (XAG 102).</summary>
    public bool HighContrast { get; init; }

    /// <summary>No UI animation, no glass tremor (XAG 117).</summary>
    public bool ReduceMotion { get; init; }

    /// <summary>Hint display time factor 1..max (XAG 116).</summary>
    public int HintDurationFactor { get; init; } = 1;

    /// <summary>Hints stay until closed (XAG 116).</summary>
    public bool HintsUntilDismissed { get; init; }

    /// <summary>Hide everything that is not interactive (XAG 117: watch the world only).</summary>
    public bool HideUi { get; init; }

    /// <summary>Volume per bus in dB (missing = default of the mix data).</summary>
    public Dictionary<string, double> BusVolumesDb { get; init; } = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Reads settings; a missing, unreadable or newer file gives the defaults.</summary>
    public static PlayerSettings Parse(string? json, UiTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PlayerSettings().Clamp(tokens);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<PlayerSettings>(json, Json);
            return settings is null || settings.Version > 1 ? new PlayerSettings().Clamp(tokens) : settings.Clamp(tokens);
        }
        catch (JsonException)
        {
            return new PlayerSettings().Clamp(tokens);
        }
    }

    /// <summary>Writes the settings as JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The UI part for <see cref="UiStyle"/>.</summary>
    public UiSettings ToUiSettings() => new(UiScale, TextScale, HighContrast, ReduceMotion);

    /// <summary>The hint part for <see cref="ToastBoard"/>.</summary>
    public ToastSettings ToToastSettings() => new(HintDurationFactor, HintsUntilDismissed);

    /// <summary>Values forced into the allowed ranges.</summary>
    public PlayerSettings Clamp(UiTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return this with
        {
            Language = Language is "de" or "en" ? Language : null,
            UiScale = UiScaling.Snap(UiScale, tokens.UiScale),
            TextScale = UiScaling.Snap(TextScale, tokens.TextScale),
            HintDurationFactor = Math.Clamp(HintDurationFactor, 1, tokens.Toast.MaxDurationFactor),
            BusVolumesDb = BusVolumesDb
                .Where(v => !string.IsNullOrEmpty(v.Key))
                .ToDictionary(v => v.Key, v => Math.Clamp(v.Value, -60, 6), StringComparer.Ordinal),
        };
    }
}
