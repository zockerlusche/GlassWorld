using GlassWorld.Simulation.Time;

namespace GlassWorld.Simulation.Tests;

/// <summary>Access to the repository's content files for tests.</summary>
internal static class TestData
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GlassWorld.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    });

    public static string Root => RootPath.Value;

    /// <summary>Calendar from the real content/world/time.json (validated against its schema).</summary>
    public static WorldCalendar Calendar() =>
        WorldCalendar.FromJson(File.ReadAllText(Path.Combine(Root, "content", "world", "time.json")));
}
