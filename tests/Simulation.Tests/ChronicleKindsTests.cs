using System.Text.Json.Nodes;
using GlassWorld.Simulation.Content;

namespace GlassWorld.Simulation.Tests;

/// <summary>Chronicle kinds are checked against the event vocabulary on load (A1354, review GW1 #124).</summary>
public sealed class ChronicleKindsTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-chronicle-kinds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void EveryKindOfTheShippedDataNamesAKnownEvent()
    {
        var content = ContentSet.Load(ContentDirectory);
        Assert.All(content.Chronicle.Kinds, k => Assert.True(content.EventKinds.Contains(k.Event), k.Event));
    }

    [Fact]
    public void AKindForAnEventNobodyCanReportIsRefusedOnLoad()
    {
        var copy = CopyContent();
        var file = Path.Combine(copy, "chronicle", "kinds.json");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["kinds"]![0]!["event"] = "danced_in_the_rain";
        File.WriteAllText(file, json.ToJsonString());
        var error = Assert.Throws<InvalidDataException>(() => ContentSet.Load(copy));
        Assert.Contains("names unknown event 'danced_in_the_rain'", error.Message, StringComparison.Ordinal);
    }

    private string CopyContent()
    {
        var target = Path.Combine(_directory, "content");
        foreach (var file in Directory.GetFiles(ContentDirectory, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(ContentDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        return target;
    }
}
