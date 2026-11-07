using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Finds the data folder (<c>content/</c>, ADR-0007). In the editor and in development runs it is the
/// repository folder next to the Godot project (<c>res://../content</c>); in an exported game it lies
/// next to the executable (<c>tools/export-windows.sh</c> copies it there).
/// </summary>
public static class ContentLocator
{
    /// <summary>Full path of the content folder; throws with both candidates if neither exists.</summary>
    public static string ContentDirectory()
    {
        var development = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "content"));
        var exported = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".", "content");
        var candidates = OS.HasFeature("editor") ? new[] { development, exported } : [exported, development];
        foreach (var candidate in candidates)
        {
            if (System.IO.Directory.Exists(System.IO.Path.Combine(candidate, "vocabulary")))
            {
                return candidate;
            }
        }

        throw new System.IO.DirectoryNotFoundException($"content folder not found (looked in {string.Join(" and ", candidates)})");
    }
}
