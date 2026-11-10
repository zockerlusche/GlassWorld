namespace GlassWorld.Presentation.Ui;

/// <summary>
/// The player's copy of a diagnostics report (W1703 rule 4): written into a folder the game can open for the
/// player. The name comes from the AI service and is treated as untrusted: only a plain file name is taken.
/// </summary>
public static class DiagnosticsFile
{
    /// <summary>Writes <paramref name="text"/> as <paramref name="fileName"/> into <paramref name="folder"/>; false if the name is no plain file name or writing fails.</summary>
    public static bool Save(string folder, string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(text);
        if (fileName.Length == 0 || fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.StartsWith('.'))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, fileName), text);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
