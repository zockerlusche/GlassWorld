namespace GlassWorld.Presentation.Tests;

/// <summary>Repository locations for tests.</summary>
public static class TestPaths
{
    /// <summary>Folder that contains <c>GlassWorld.slnx</c>.</summary>
    public static readonly string Root = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GlassWorld.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("GlassWorld.slnx not found");
    }
}
