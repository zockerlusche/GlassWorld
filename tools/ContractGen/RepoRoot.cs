namespace GlassWorld.Tools.ContractGen;

/// <summary>Finds the repository root (the folder that contains <c>GlassWorld.slnx</c>).</summary>
public static class RepoRoot
{
    /// <summary>Searches upwards from the current directory and the application base directory.</summary>
    public static string Find()
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
