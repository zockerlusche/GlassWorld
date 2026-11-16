using System.Text.Json.Nodes;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1776 (B2811, D1168 stage 0): the local collection of what the AI made - stable id, origin, kept once.</summary>
public sealed class MadeObjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gw-collection-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheSameThingFromTwoWorldsIsOneObjectWithItsFirstOrigin()
    {
        var collection = new MadeObjectStore(Path.Combine(_root, "collection"));
        var picture = Path.Combine(_root, "icon.png");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(picture, [1, 2, 3, 4]);
        JsonNode Content() => new JsonObject { ["width"] = 64, ["height"] = 64 };

        var (id, added) = collection.Add("icon", Content(), [("icon.png", picture)], Origin("a/1"));
        var (again, addedAgain) = collection.Add("icon", Content(), [("icon.png", picture)], Origin("b/2"));

        Assert.True(added);
        Assert.False(addedAgain);
        Assert.Equal(id, again);
        Assert.Equal(64, id.Length);
        var folder = Path.Combine(collection.Root, id[..2], id);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(folder, "icon.png")));
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "object.json")))!;
        Assert.Equal("a/1", json["origin"]!["world"]!.GetValue<string>());
        Assert.Equal("approved", json["origin"]!["guard"]!.GetValue<string>());
    }

    [Fact]
    public void OtherContentOrAnotherKindIsAnotherObject()
    {
        JsonNode Words(string purpose) => new JsonObject { ["purpose"] = purpose };
        var one = MadeObjectStore.IdOf("invention", Words("carry water"), []);
        Assert.NotEqual(one, MadeObjectStore.IdOf("invention", Words("keep warm"), []));
        Assert.NotEqual(one, MadeObjectStore.IdOf("build_plan", Words("carry water"), []));
        Assert.Equal(one, MadeObjectStore.IdOf("invention", Words("carry water"), []));
    }

    private static CollectedOrigin Origin(string world) =>
        new(world, 3, "inh-000001", "inv_abc", "req-1", "qwen3.5:9b", "invention/invent.v7", MadeObjectStore.Approved);
}
