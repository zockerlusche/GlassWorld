namespace GlassWorld.Presentation.Ui;

/// <summary>
/// A1749 part 2a (D957 v4 section 10): the game options of the tab 'Spiel' - when the world saves itself, at which speed a
/// world starts, and whether a knock pauses the world.
/// </summary>
public static class GameOptions
{
    /// <summary>No autosave (the player saves by hand).</summary>
    public const string AutoSaveOff = "off";

    /// <summary>Every game day at the day boundary (B2487, as before).</summary>
    public const string AutoSaveDay = "day";

    /// <summary>When the compute night begins (B2680).</summary>
    public const string AutoSaveNight = "night";

    /// <summary>The autosave modes in the order the settings show them.</summary>
    public static IReadOnlyList<string> AutoSaveModes { get; } = [AutoSaveOff, AutoSaveDay, AutoSaveNight];

    /// <summary>Text key of an autosave mode.</summary>
    public static string AutoSaveKey(string mode) => "ui.settings.autosave." + mode;

    /// <summary>
    /// Whether the world saves now: at a new game day in mode 'day', when the compute night has just begun in mode
    /// 'night', never in mode 'off'.
    /// </summary>
    public static bool SavesNow(string mode, bool newDay, bool nightBegan) => mode switch
    {
        AutoSaveDay => newDay,
        AutoSaveNight => nightBegan,
        _ => false,
    };
}
