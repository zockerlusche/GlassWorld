using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>B5 R1, B2676: the climate chosen for a new world reaches the core and stays with the save game.</summary>
public sealed class ClimateChoiceTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-climate-" + Path.GetRandomFileName());

    public ClimateChoiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static SimulationHost Create(string? climate) =>
        SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, new MockDecisionPort(), "de", "start_island", options: new SimulationHostOptions { Climate = climate });

    [Fact]
    public void TheChosenClimateDecidesTheTemperatureAndWithoutAChoiceThePresetDoes()
    {
        var preset = Create(null);
        var temperate = Create("temperate");
        var warm = Create("warm");
        foreach (var host in new[] { preset, temperate, warm })
        {
            host.World.Run(host.World.Calendar.TicksPerDay + 600);
        }

        int Air(SimulationHost host) => host.WeatherSystem!.Read(host.World).TemperatureTenthC;
        Assert.Equal(Air(temperate), Air(preset));
        Assert.True(Air(warm) > Air(temperate), $"warm {Air(warm)} vs temperate {Air(temperate)}");
        Assert.NotEqual(temperate.World.StateHash(), warm.World.StateHash());
    }

    [Fact]
    public void AnUnknownClimateIsRefused() =>
        Assert.Throws<InvalidDataException>(() => Create("arctic"));

    [Fact]
    public void TheClimateIsInTheSaveGameAndOlderSavesKeepThePresetClimate()
    {
        var original = Create("warm");
        original.World.Run(original.World.Calendar.TicksPerDay + 101);
        var path = Path.Combine(_directory, "warm.db");
        SaveGameStore.Save(path, StateCodec.Capture(original));
        var state = SaveGameStore.Load(path);
        Assert.Equal("warm", state.Climate);

        var loaded = Restore(state, new ReplayDecisionPort(original.Decisions.Log));
        Assert.Equal("warm", loaded.Options.Climate);
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());

        // Further on, a replay from the save with the full log reaches the same state (weather of the warm climate).
        original.World.Run(original.World.Calendar.TicksPerDay);
        var replay = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(original.Decisions.Log));
        replay.World.Run(original.World.Calendar.TicksPerDay);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());

        // A save from before the choice carries no climate: it loads with the preset's climate as before.
        var older = Create(null);
        older.World.Run(500);
        var olderState = StateCodec.Capture(older);
        Assert.Null(olderState.Climate);
        var olderLoaded = Restore(olderState, new ReplayDecisionPort(older.Decisions.Log));
        Assert.Null(olderLoaded.Options.Climate);
        Assert.Equal(older.World.StateHash(), olderLoaded.World.StateHash());
    }

    [Fact]
    public void TheChosenClimateIsInTheHashAndAWorldWithoutAChoiceKeepsItsHash()
    {
        // Review GW1 #318: two worlds that differ only in the choice differ in the hash from the start, and a
        // world without a choice hashes as before (nothing is written for it).
        Assert.NotEqual(Create(null).World.StateHash(), Create("temperate").World.StateHash());
        Assert.Equal(Create(null).World.StateHash(), Create(null).World.StateHash());
    }

    [Fact]
    public void ASaveWithoutAClimateKeepsThePresetClimateEvenIfTheFactoryCarriesOne()
    {
        // Review GW1 #318: null in the save means the preset's climate, whatever the factory's options say.
        var older = Create(null);
        older.World.Run(300);
        var state = StateCodec.Capture(older);
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var factory = new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new ReplayDecisionPort(older.Decisions.Log), "de", options: new SimulationHostOptions { Climate = "warm" });
        var loaded = StateCodec.Restore(factory, state);
        Assert.Null(loaded.Options.Climate);
        Assert.Equal(older.World.StateHash(), loaded.World.StateHash());
    }

    private static SimulationHost Restore(SaveState state, IDecisionPort port)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, port, "de"), state);
    }
}
