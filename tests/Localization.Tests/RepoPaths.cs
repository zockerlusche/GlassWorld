namespace GlassWorld.Localization.Tests;

/// <summary>Locations in the repository used by the localization checks.</summary>
public static class RepoPaths
{
    /// <summary>Repository root (the folder that contains <c>GlassWorld.slnx</c>).</summary>
    public static readonly string Root = FindRoot();

    /// <summary>Folder with one catalog per language.</summary>
    public static string I18n => Path.Combine(Root, "content", "i18n");

    /// <summary>Folder with the vocabulary data files.</summary>
    public static string Vocabulary => Path.Combine(Root, "content", "vocabulary");

    /// <summary>Folders whose scenes and C# code may show text to the player.</summary>
    public static IEnumerable<string> PlayerFacingSourceFolders =>
        new[] { Path.Combine(Root, "game"), Path.Combine(Root, "src", "Game.Presentation") }.Where(Directory.Exists);

    /// <summary>Folders whose code may hand translation keys to the UI (e.g. reasons of rejected commands).</summary>
    public static IEnumerable<string> KeySourceFolders =>
        PlayerFacingSourceFolders.Concat(new[] { Path.Combine(Root, "src", "Simulation.Core") }.Where(Directory.Exists));

    /// <summary>Files below <paramref name="folder"/> with the given extension, skipping build and editor output.</summary>
    public static IEnumerable<string> FilesBelow(string folder, string extension) =>
        Directory.EnumerateFiles(folder, "*" + extension, SearchOption.AllDirectories)
            .Where(f => !IsIgnored(Path.GetRelativePath(Root, f)))
            .Order(StringComparer.Ordinal);

    /// <summary>Repository-relative path with forward slashes.</summary>
    public static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static bool IsIgnored(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        return parts.Any(p => p is ".godot" or "bin" or "obj" or ".import");
    }

    private static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "GlassWorld.slnx")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Repository root with GlassWorld.slnx not found.");
    }
}
