using System.Text;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui;

/// <summary>Stores the player settings in the user folder (B6 R4), never in a save game.</summary>
public static class SettingsStore
{
    /// <summary>Default place of the settings file.</summary>
    public static string DefaultPath => ProjectSettings.GlobalizePath("user://settings.json");

    /// <summary>Reads the settings; a missing or broken file gives the defaults.</summary>
    public static PlayerSettings Load(string path, SettingsLimits limits)
    {
        try
        {
            return PlayerSettings.Parse(File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null, limits);
        }
        catch (IOException e)
        {
            GD.PushWarning($"settings not read ({e.Message}); defaults used");
            return PlayerSettings.Parse(null, limits);
        }
    }

    /// <summary>Writes the settings (temporary file, then swap); a failure only logs a warning.</summary>
    public static bool Save(string path, PlayerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var temp = path + ".tmp";
            File.WriteAllText(temp, settings.ToJson(), Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"settings not saved: {e.Message}");
            return false;
        }
    }
}
