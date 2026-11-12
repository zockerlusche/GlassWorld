using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Player;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B6 R19 (B2518, W1603): the player marks favourites - a logged command, kept in the save game, the player's
/// alone: no request to the AI carries it, and a world without favourites hashes as before.
/// </summary>
public sealed class FavouritesTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public FavouritesTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ThePlayerMarksAndUnmarksAFavourite()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 3, new MockDecisionPort(), "de");

        Assert.True(host.Decisions.Submit(new PlayerCommand(FavouritesSystem.Mark, "inh-000002")).Accepted);
        host.World.Step();
        Assert.True(host.Favourites.IsFavourite("inh-000002"));
        Assert.Equal(["inh-000002"], host.Favourites.All);

        Assert.True(host.Decisions.Submit(new PlayerCommand(FavouritesSystem.Unmark, "inh-000002")).Accepted);
        host.World.Step();
        Assert.False(host.Favourites.IsFavourite("inh-000002"));
    }

    [Fact]
    public void AnInhabitantWhoIsNotThereCannotBeMarked()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 3, new MockDecisionPort(), "de");

        var result = host.Decisions.Submit(new PlayerCommand(FavouritesSystem.Mark, "inh-999999"));
        host.World.Step();

        Assert.False(result.Accepted && host.Favourites.IsFavourite("inh-999999"));
        Assert.Empty(host.Favourites.All);
    }

    [Fact]
    public void AWorldWithoutFavouritesHashesAsBeforeAndAMarkChangesTheHash()
    {
        var a = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 9, new MockDecisionPort(), "de");
        var b = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 9, new MockDecisionPort(), "de");
        a.World.Run(50);
        b.World.Run(50);
        Assert.Equal(a.World.StateHash(), b.World.StateHash());
        Assert.False(a.Favourites.HasState);

        b.Decisions.Submit(new PlayerCommand(FavouritesSystem.Mark, "inh-000001"));
        a.World.Step();
        b.World.Step();
        Assert.NotEqual(a.World.StateHash(), b.World.StateHash());
    }

    [Fact]
    public void FavouritesSurviveSaveAndLoad()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
        host.Decisions.Submit(new PlayerCommand(FavouritesSystem.Mark, "inh-000003"));
        host.World.Run(10);
        var path = Path.Combine(_directory, "world.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));

        var loaded = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(host.Decisions.Log));

        Assert.True(loaded.Favourites.IsFavourite("inh-000003"));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void NoRequestToTheAiCarriesTheFavourites()
    {
        // The mark is the player's: a world with a favourite asks the AI exactly what one without asks.
        var plain = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 11, new MockDecisionPort(), "de");
        var marked = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 11, new MockDecisionPort(), "de");
        foreach (var id in marked.Inhabitants.All.Select(i => i.Id))
        {
            marked.Decisions.Submit(new PlayerCommand(FavouritesSystem.Mark, id));
        }

        var day = plain.World.Calendar.TicksPerDay;
        plain.World.Run(day);
        marked.World.Run(day);

        var asked = plain.Decisions.Log.Entries.Where(e => e.Kind == DecisionLogKind.Request).Select(e => e.Json).ToList();
        Assert.NotEmpty(asked);
        Assert.Equal(asked, marked.Decisions.Log.Entries.Where(e => e.Kind == DecisionLogKind.Request).Select(e => e.Json));
        Assert.All(asked, json => Assert.DoesNotContain("favourite", json, StringComparison.OrdinalIgnoreCase));
    }

    private static SimulationHost Restore(SaveState state, IDecisionPort port)
    {
        var content = ContentSet.Load(ContentDirectory);
        var settings = Contracts.Validation.ContractSerializer.Deserialize<Contracts.DeciderSettings>(
            File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return StateCodec.Restore(new SimulationHostFactory(content, settings, port, "de"), state);
    }
}
