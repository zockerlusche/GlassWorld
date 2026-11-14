using System.Text.Json;
using System.Text.Json.Serialization;
using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Ui;

/// <summary>What a setting may be: UI tokens, the game's languages and the volume range of the mix data.</summary>
/// <param name="Tokens">UI tokens (UI and text size, hint duration).</param>
/// <param name="Locales">Languages with a text catalog.</param>
/// <param name="VolumeDb">Range of the volume sliders (content/sound/mix.json).</param>
/// <param name="PlayerNameMaxLength">Longest player name (content/player/world_options.json).</param>
/// <param name="Guard">Content guard (R13); a blocked name from a hand-edited file is dropped.</param>
/// <param name="MixDefaultsVersion">Version of the default bus volumes (mix.json); saved volumes of another version are reset.</param>
public sealed record SettingsLimits(UiTokens Tokens, IReadOnlyCollection<string> Locales, MixPlayerRange VolumeDb, int PlayerNameMaxLength, GlassWorld.Simulation.Safety.TextGuard Guard, int MixDefaultsVersion = 1);

/// <summary>
/// The player's settings (B6 R4): stored in the user folder (<c>user://settings.json</c>), never in a
/// world save and never sent to the AI. Unknown or broken files fall back to the defaults; values are
/// clamped to the token ranges, so a hand-edited file cannot break the UI.
/// </summary>
public sealed record PlayerSettings
{
    /// <summary>Format version of the file.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Game language (one with a text catalog); null = system language on first start.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// The player's name (B2535): used only outside the aquarium (chronicle, hints, how the game
    /// addresses the player). Never sent to the AI; inhabitants learn it only when the player tells
    /// them. Null = no name.
    /// </summary>
    public string? PlayerName { get; init; }

    /// <summary>UI size 0.75..2 (B2513).</summary>
    public double UiScale { get; init; } = 1;

    /// <summary>Text size 0.75..2 (XAG 101).</summary>
    public double TextScale { get; init; } = 1;

    /// <summary>High-contrast palette (XAG 102).</summary>
    public bool HighContrast { get; init; }

    /// <summary>No UI animation, no glass tremor (XAG 117).</summary>
    public bool ReduceMotion { get; init; }

    /// <summary>Feeling symbols above the heads (B2541; on by default, can be switched off).</summary>
    public bool FeelingSymbols { get; init; } = true;

    /// <summary>Hint display time factor 1..max (XAG 116).</summary>
    public int HintDurationFactor { get; init; } = 1;

    /// <summary>Hints stay until closed (XAG 116).</summary>
    public bool HintsUntilDismissed { get; init; }

    /// <summary>Hide everything that is not interactive (XAG 117: watch the world only).</summary>
    public bool HideUi { get; init; }

    /// <summary>
    /// The old screen layout (side bar on the right, top bar over the full width) as a fallback until the corner
    /// layout is complete (TL #23224, D957 v4 step 3); off = the corners of Variante A, the standard (A1682). A new
    /// name, so a saved "zones preview" from before does not keep anyone on the old layout.
    /// </summary>
    public bool ClassicLayout { get; init; }

    /// <summary>Volume per bus in dB (missing = default of the mix data).</summary>
    public Dictionary<string, double> BusVolumesDb { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Mix defaults version the saved volumes belong to (review #106: new defaults reset old volumes once).</summary>
    public int MixDefaultsVersion { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Reads settings; a missing, unreadable or newer file gives the defaults.</summary>
    public static PlayerSettings Parse(string? json, SettingsLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PlayerSettings().Clamp(limits);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<PlayerSettings>(json, Json);
            return settings is null || settings.Version > 1 ? new PlayerSettings().Clamp(limits) : settings.Clamp(limits);
        }
        catch (JsonException)
        {
            return new PlayerSettings().Clamp(limits);
        }
    }

    /// <summary>Writes the settings as JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The UI part for <see cref="UiStyle"/>.</summary>
    public UiSettings ToUiSettings() => new(UiScale, TextScale, HighContrast, ReduceMotion);

    /// <summary>The hint part for <see cref="ToastBoard"/>.</summary>
    public ToastSettings ToToastSettings() => new(HintDurationFactor, HintsUntilDismissed);

    /// <summary>
    /// A hand-edited name: trimmed, no control characters, cut to the length limit, and through the
    /// content guard like an input in the window (GW1 review #58 C1); empty or blocked = null.
    /// </summary>
    private static string? CleanName(string? name, int maxLength, GlassWorld.Simulation.Safety.TextGuard guard)
    {
        var clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        clean = clean.Length > maxLength ? clean[..maxLength].TrimEnd() : clean;
        return clean.Length == 0 || !guard.Check(clean).Allowed ? null : clean;
    }

    /// <summary>Values forced into the allowed ranges.</summary>
    public PlayerSettings Clamp(SettingsLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var tokens = limits.Tokens;
        return this with
        {
            Language = Language is not null && limits.Locales.Contains(Language, StringComparer.Ordinal) ? Language : null,
            PlayerName = CleanName(PlayerName, limits.PlayerNameMaxLength, limits.Guard),
            UiScale = UiScaling.Snap(UiScale, tokens.UiScale),
            TextScale = UiScaling.Snap(TextScale, tokens.TextScale),
            HintDurationFactor = Math.Clamp(HintDurationFactor, 1, tokens.Toast.MaxDurationFactor),

            // A hand-edited "bus_volumes_db": null must not break the UI either.
            MixDefaultsVersion = limits.MixDefaultsVersion,
            BusVolumesDb = (MixDefaultsVersion == limits.MixDefaultsVersion ? BusVolumesDb ?? [] : [])
                .Where(v => !string.IsNullOrEmpty(v.Key))
                .ToDictionary(v => v.Key, v => Math.Clamp(v.Value, limits.VolumeDb.Min, limits.VolumeDb.Max), StringComparer.Ordinal),
        };
    }
}
