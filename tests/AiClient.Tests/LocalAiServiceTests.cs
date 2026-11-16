using System.Diagnostics;
using System.Globalization;

namespace GlassWorld.AiClient.Tests;

public sealed class LocalAiServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gw-lock-" + Guid.NewGuid().ToString("N"));

    public LocalAiServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Lock(string json)
    {
        var path = Path.Combine(_folder, "ai-service.lock");
        File.WriteAllText(path, json);
        return path;
    }

    private static long OwnMarker()
    {
        using var self = Process.GetCurrentProcess();
        return self.StartTime.ToFileTimeUtc();
    }

    [Fact]
    public void ALockOfALivingProcessNamesIt()
    {
        var pid = Environment.ProcessId;
        var path = Lock(string.Create(CultureInfo.InvariantCulture, $$"""{"pid": {{pid}}, "port": 51234, "started_utc": "2026-11-09T10:00:00+00:00", "start_marker": {{OwnMarker()}}}"""));
        Assert.Equal(pid, LocalAiService.RunningService(path));
    }

    [Fact]
    public void AReusedPidDoesNotCountOnWindows()
    {
        var path = Lock(string.Create(CultureInfo.InvariantCulture, $$"""{"pid": {{Environment.ProcessId}}, "port": 1, "started_utc": "x", "start_marker": 12345}"""));
        Assert.Equal(OperatingSystem.IsWindows() ? null : Environment.ProcessId, LocalAiService.RunningService(path));
    }

    [Theory]
    [InlineData("""{"pid": 2147483000, "port": 1, "started_utc": "x", "start_marker": null}""")]
    [InlineData("""{"port": 1}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void NoLivingServiceGivesNull(string json) =>
        Assert.Null(LocalAiService.RunningService(Lock(json)));

    [Fact]
    public void AMissingLockGivesNull() =>
        Assert.Null(LocalAiService.RunningService(Path.Combine(_folder, "missing.lock")));

    [Fact]
    public void TheGameLockLiesInGodotsUserDataOfTheProject() =>
        Assert.EndsWith(Path.Combine("app_userdata", "GlassWorld", "ai-service", "lock", "ai-service.lock"), LocalAiService.GameLockFile(), StringComparison.Ordinal);

    [Fact]
    public void TheRepositoryRootIsTheConfiguredOneOrTheContentParent()
    {
        Assert.Equal(Path.GetFullPath(_folder), LocalAiService.RepositoryRoot(Path.Combine(_folder, "content"), null));
        Assert.Equal(Path.GetFullPath("elsewhere"), LocalAiService.RepositoryRoot(Path.Combine(_folder, "content"), "elsewhere"));
    }

    [Fact]
    public void APackageNextToTheGameIsFoundOnlyWithPythonAndConfig()
    {
        // A1421 (B10-S2): <game>/ai-service/python/python.exe and config.toml (tools/package_ai_service.py).
        Assert.Null(LocalAiService.PackagedService(_folder));
        var package = Path.Combine(_folder, LocalAiService.PackageFolder);
        Directory.CreateDirectory(Path.Combine(package, "python"));
        File.WriteAllText(Path.Combine(package, "python", "python.exe"), "x");
        Assert.Null(LocalAiService.PackagedService(_folder));
        File.WriteAllText(Path.Combine(package, "config.toml"), "x");
        Assert.Equal(Path.GetFullPath(package), LocalAiService.PackagedService(_folder));
    }

    [Fact]
    public void ThePackagedServiceWritesOnlyIntoTheDataFolder()
    {
        var folders = LocalAiService.PackagedFolders(_folder);
        Assert.Contains("GW_AI_SOUND_OUTPUT_DIR", folders.Keys);
        Assert.Contains("GW_AI_IMAGE_OUTPUT_DIR", folders.Keys);
        Assert.All(folders.Values, f => Assert.StartsWith(_folder, f, StringComparison.Ordinal));
    }
}
