using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Finds the data folder (<c>content/</c>, ADR-0007). In the editor and in development runs it is the
/// repository folder next to the Godot project (<c>res://../content</c>); in an exported game it lies
/// next to the executable (<c>tools/export-windows.sh</c> copies it there). A test run may name another
/// folder with the environment variable <c>GLASSWORLD_CONTENT</c> - a variant of the data made by a tool
/// (A1558: <c>tools/night/night_test_content.py</c>); a player never sets it.
/// </summary>
public static class ContentLocator
{
    /// <summary>The environment variable that names a test variant of the data folder.</summary>
    public const string Variable = "GLASSWORLD_CONTENT";

    /// <summary>The test variant named by <see cref="Variable"/>, or null when none is named.</summary>
    public static string? Variant => System.Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named ? System.IO.Path.GetFullPath(named) : null;

    /// <summary>Full path of the content folder; throws with both candidates if neither exists.</summary>
    public static string ContentDirectory()
    {
        if (Variant is { } variant)
        {
            // Named on purpose: a missing folder is an error, never a silent fall back to the real data.
            return System.IO.Directory.Exists(System.IO.Path.Combine(variant, "vocabulary"))
                ? variant
                : throw new System.IO.DirectoryNotFoundException($"{Variable} names {variant}, which holds no content folder");
        }

        return RealContentDirectory();
    }

    /// <summary>
    /// The real content folder (repository or next to the executable), whatever <see cref="Variable"/> says:
    /// the AI service starts from the folder above it, also while a test variant of the data is played.
    /// </summary>
    public static string RealContentDirectory()
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
