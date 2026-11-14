using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Nature;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>B4b K4 part 1b: animals as a stock per species and biome that scales what they leave behind.</summary>
public sealed class AnimalStockTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-animals-" + Guid.NewGuid().ToString("N"));

    public AnimalStockTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static NatureAnimals Data(int growthPermille, int winterPermille = 0) => new()
    {
        ContractVersion = "0.2",
        Species =
        [
            new AnimalSpecies
            {
                Id = "birds",
                Groups = ["nesting"],
                Biomes = ["forest", "swamp"],
                CapPerBiome = 40,
                StartPermille = 500,
                GrowthPerDayPermille = growthPermille,
                Seasons = [new AnimalSeasonFactor { Season = "winter", Permille = winterPermille }],
            },
        ],
    };

    [Fact]
    public void AStockGrowsTowardsItsLimitAndNeverPastItOnlyWhereTheIslandHasTheBiome()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 3, new MockDecisionPort(), "de");
        var day = host.World.Calendar.TicksPerDay;
        var stock = new AnimalStockSystem(Data(200), day, new HashSet<string>(["forest"], StringComparer.Ordinal));

        Assert.Equal(20, stock.StockOf("birds", "forest"));
        Assert.Equal(0, stock.StockOf("birds", "swamp")); // the island has no swamp: no stock there
        Assert.Equal(500, stock.FactorOf("nesting", 0));
        Assert.Null(stock.FactorOf("grazing", 0)); // no species feeds it: the group grows by season alone

        var previous = 20L;
        for (var tick = day; tick <= 60 * day; tick += day)
        {
            Step(host, stock, tick);
            var now = stock.StockOf("birds", "forest");
            Assert.InRange(now, previous, 40);
            previous = now;
        }

        Assert.InRange(stock.FactorOf("nesting", 0)!.Value, 900, 1000);
    }

    [Fact]
    public void InASeasonWithoutYoungTheStockStaysAsItIs()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 3, new MockDecisionPort(), "de");
        var calendar = host.World.Calendar;
        var stock = new AnimalStockSystem(Data(500, winterPermille: 0), calendar.TicksPerDay, new HashSet<string>(["forest"], StringComparer.Ordinal));
        var winter = Enumerable.Range(1, 4 * calendar.Settings.DaysPerSeason).Select(d => d * calendar.TicksPerDay)
            .First(t => calendar.At(t).SeasonId == "winter");
        Step(host, stock, winter);
        Assert.Equal(20, stock.StockOf("birds", "forest"));
    }

    [Fact]
    public void AnIslandStartsWithHalfFullStocksAndSavesAndLoadsThemWithTheSameHash()
    {
        var original = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        Assert.NotNull(original.Animals);
        Assert.Equal(500, original.Animals!.FactorOf("nesting", 0));

        original.World.Run((3 * original.World.Calendar.TicksPerDay) + 17);
        var path = Path.Combine(_directory, "world.db");
        SaveGameStore.Save(path, StateCodec.Capture(original));
        var content = ContentSet.Load(ContentDirectory);
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var loaded = StateCodec.Restore(new SimulationHostFactory(content, settings, new ReplayDecisionPort(original.Decisions.Log), "de"), SaveGameStore.Load(path));

        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(original.Animals.FactorOf("grazing", 0), loaded.Animals!.FactorOf("grazing", 0));
    }

    /// <summary>Steps the stock alone at <paramref name="tick"/> (the world's clock moved there first).</summary>
    private static void Step(SimulationHost host, AnimalStockSystem stock, long tick)
    {
        while (host.World.Tick < tick)
        {
            host.World.Step();
        }

        stock.Advance(host.World);
    }
}
