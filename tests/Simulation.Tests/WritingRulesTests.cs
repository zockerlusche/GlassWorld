using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;
using GlassWorld.Simulation.Writing;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// The checks behind write and read, and finding writing again (A1357 independent parts, TL #19589;
/// B8 R13-R15, spec 33 §9a). Skill levels and the held tool are stand-ins until B5 / B2540.
/// </summary>
public sealed class WritingRulesTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-writing-rules-" + Guid.NewGuid().ToString("N"));

    public WritingRulesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void WithoutTheSkillNobodyWritesOrReads()
    {
        // R14: nobody starts with reading or writing; the stand-in for skill levels says 0 for everyone.
        var (host, writer, _) = Placeholder();
        var stone = host.Objects.Spawn("fieldstone", writer.Position);
        Assert.Equal(WritingSystem.NoWritingSkill, host.Writing.CanWriteOn(writer, stone, "rock_painting", NoSkills.Instance, NothingHeld.Instance));

        host.Writing.Place(new WrittenText(stone.Id, "rock_painting", "belief:water|drinkable|true", "inh-000002", 0, 0.7));
        Assert.Equal(WritingSystem.NoReadingSkill, host.Writing.CanRead(writer, host.Writing.On(stone.Id)!, NoSkills.Instance));
    }

    [Fact]
    public void TheCarrierDecidesByTrueProperties()
    {
        var (host, writer, _) = Placeholder();
        var skilled = new Skills(("writing", 0.5));
        var wood = host.Objects.Spawn("dry_wood", writer.Position);
        var stone = host.Objects.Spawn("fieldstone", writer.Position);
        var apple = host.Objects.Spawn("apple", writer.Position);

        // Wood (building material + burnable) carries carvings in wood, not in stone; an apple carries nothing.
        Assert.Equal(WritingSystem.NoTool, host.Writing.CanWriteOn(writer, wood, "carving_wood", skilled, NothingHeld.Instance));
        Assert.Equal(WritingSystem.WrongCarrier, host.Writing.CanWriteOn(writer, wood, "carving_stone", skilled, NothingHeld.Instance));
        Assert.Equal(WritingSystem.WrongCarrier, host.Writing.CanWriteOn(writer, apple, "rock_painting", skilled, NothingHeld.Instance));
        Assert.Equal(WritingSystem.WrongCarrier, host.Writing.CanWriteOn(writer, stone, "sand_marks", skilled, NothingHeld.Instance));
        Assert.Equal(WritingSystem.UnknownMedium, host.Writing.CanWriteOn(writer, stone, "parchment", skilled, NothingHeld.Instance));

        // Painting needs no tool; carving stone needs a hard, sharp tool (flint is both, an apple neither).
        Assert.Null(host.Writing.CanWriteOn(writer, stone, "rock_painting", skilled, NothingHeld.Instance));
        Assert.Equal(WritingSystem.NoTool, host.Writing.CanWriteOn(writer, stone, "carving_stone", skilled, new Holding("apple")));
        Assert.Null(host.Writing.CanWriteOn(writer, stone, "carving_stone", skilled, new Holding("apple", "flint")));

        host.Writing.Place(new WrittenText(stone.Id, "rock_painting", "belief:water|drinkable|true", writer.Id, 0, 0.5));
        Assert.Equal(WritingSystem.CarrierTaken, host.Writing.CanWriteOn(writer, stone, "carving_stone", skilled, new Holding("flint")));
    }

    [Fact]
    public void MarksInTheSandNeedTheBeachAndAMap()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        var map = host.Map!;
        var writer = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
        var skilled = new Skills(("writing", 0.5));
        var beach = Spot(map, "beach_sea");
        var forest = Spot(map, "forest");

        Assert.Null(host.Writing.CanWriteOnGround(writer, beach, "sand_marks", map, skilled, NothingHeld.Instance));
        Assert.Equal(WritingSystem.WrongCarrier, host.Writing.CanWriteOnGround(writer, forest, "sand_marks", map, skilled, NothingHeld.Instance));
        Assert.Equal(WritingSystem.WrongCarrier, host.Writing.CanWriteOnGround(writer, beach, "rock_painting", map, skilled, NothingHeld.Instance));
        Assert.Equal(WritingSystem.WrongCarrier, host.Writing.CanWriteOnGround(writer, beach, "sand_marks", null, skilled, NothingHeld.Instance));
        Assert.Equal(WritingSystem.NoWritingSkill, host.Writing.CanWriteOnGround(writer, beach, "sand_marks", map, NoSkills.Instance, NothingHeld.Instance));
    }

    [Fact]
    public void LegibilityIsTheCarriersBestTimesTheWritersSkill()
    {
        var (host, _, _) = Placeholder();
        var best = host.Content.Writing.Media.Single(m => m.Id == "carving_stone").Legibility;
        Assert.Equal(best * 0.5, host.Writing.LegibilityFor("carving_stone", 0.5), 9);
        Assert.Equal(best, host.Writing.LegibilityFor("carving_stone", 2), 9);
        Assert.Equal(0, host.Writing.LegibilityFor("parchment", 1));
    }

    [Fact]
    public void ReadingInPassingNeedsSkillLegibilityAndCuriosity()
    {
        var (host, reader, _) = Placeholder();
        var rules = host.Content.Writing.Read;
        var stone = host.Objects.Spawn("fieldstone", reader.Position);
        host.Writing.Place(new WrittenText(stone.Id, "carving_stone", "belief:water|drinkable|true", "inh-000099", 0, 0.8));
        var text = host.Writing.On(stone.Id)!;
        var reading = new Skills(("reading", rules.MinLevel));

        reader.Traits[rules.CuriosityTrait] = rules.CuriosityMin;
        Assert.Null(host.Writing.WouldReadInPassing(reader, text, reading));
        reader.Traits[rules.CuriosityTrait] = rules.CuriosityMin / 2;
        Assert.Equal(WritingSystem.NotCurious, host.Writing.WouldReadInPassing(reader, text, reading));
        Assert.Null(host.Writing.CanRead(reader, text, reading)); // asked on purpose, curiosity does not matter
        Assert.Equal(WritingSystem.Illegible, host.Writing.CanRead(reader, text with { Legibility = rules.MinLegibility / 2 }, reading));
    }

    [Fact]
    public void AnOldTextIsFoundOnceAYoungOneIsOnlySeen()
    {
        var (host, finder, other) = Placeholder();
        var days = host.Content.Writing.Found.AfterDays;
        var perDay = host.World.Calendar.TicksPerDay;
        host.World.Run((long)(days * perDay) + 2);
        var found = new List<WorldEvent>();
        host.Inhabitants.Reported += e =>
        {
            if (e.Kind == WritingSystem.WritingFound)
            {
                found.Add(e);
            }
        };

        // An old text right where the finder stands, and a young one next to it.
        var old = host.Objects.Spawn("fieldstone", finder.Position);
        var young = host.Objects.Spawn("fieldstone", finder.Position);
        host.Writing.Place(new WrittenText(old.Id, "carving_stone", "belief:water|drinkable|true", "inh-000099", 1, 0.85));
        host.Writing.Place(new WrittenText(young.Id, "carving_stone", "belief:water|drinkable|true", "inh-000099", host.World.Tick, 0.85));
        host.World.Run(3);

        var mine = found.Where(e => e.InhabitantId == finder.Id).ToList();
        Assert.Single(mine);
        Assert.Equal(old.Id, mine[0].TargetId);
        Assert.Equal("inh-000099", mine[0].Detail);
        Assert.Contains($"{finder.Id}|{young.Id}", host.Writing.Seen);

        // Once found is found: walking past again tells nothing new.
        host.World.Run(50);
        Assert.Single(found, e => e.InhabitantId == finder.Id && e.TargetId == old.Id);
        Assert.Contains(host.Chronicle.All, r => r.Kind == "found_writing" && r.InhabitantId == finder.Id);
        _ = other;
    }

    [Fact]
    public void NobodyFindsTheirOwnWritingAndWhatIsGoneIsForgotten()
    {
        var (host, writer, _) = Placeholder();
        host.World.Run((long)(host.Content.Writing.Found.AfterDays * host.World.Calendar.TicksPerDay) + 2);
        var stone = host.Objects.Spawn("fieldstone", writer.Position);
        host.Writing.Place(new WrittenText(stone.Id, "carving_stone", "belief:water|drinkable|true", writer.Id, 1, 0.85));
        host.World.Run(3);
        Assert.DoesNotContain(host.Writing.Seen, s => s.StartsWith(writer.Id + "|", StringComparison.Ordinal));

        host.Objects.Remove(stone.Id);
        host.World.Run(2);
        Assert.Null(host.Writing.On(stone.Id));
        Assert.DoesNotContain(host.Writing.Seen, s => s.EndsWith("|" + stone.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void SeenTextsSurviveSaveAndLoadAndTheStartStaysOutOfTheHash()
    {
        var (host, finder, _) = Placeholder();
        var fresh = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        fresh.World.Run(200);
        Assert.False(fresh.Writing.HasState); // nobody writes, so nothing is seen: golden hashes stay

        var stone = host.Objects.Spawn("fieldstone", finder.Position);
        host.Writing.Place(new WrittenText(stone.Id, "carving_stone", "belief:water|drinkable|true", "inh-000099", 0, 0.85));
        host.World.Run(5);
        Assert.NotEmpty(host.Writing.Seen);

        var path = Path.Combine(_directory, "seen.sqlite");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var settings = Contracts.Validation.ContractSerializer.Deserialize<Contracts.DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new ReplayDecisionPort(host.Decisions.Log), "de"), SaveGameStore.Load(path));
        Assert.Equal(host.Writing.Seen, loaded.Writing.Seen);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    private static (SimulationHost Host, Inhabitant First, Inhabitant Second) Placeholder()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        var all = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
        return (host, all[0], all[1]);
    }

    private static Position Spot(Map.WorldMapSystem map, string biome)
    {
        var index = map.Map.BiomeIds.ToList().IndexOf(biome);
        for (var z = 0; z < map.Map.DepthCells; z++)
        {
            for (var x = 0; x < map.Map.WidthCells; x++)
            {
                if (map.Map.Cell(x, z).Biome == index)
                {
                    var size = map.CellSizeCm / 100.0;
                    return new Position((x + 0.5) * size, (z + 0.5) * size);
                }
            }
        }

        throw new InvalidOperationException($"no {biome} on the map");
    }

    private sealed class Skills(params (string Skill, double Level)[] levels) : ISkillLevels
    {
        public double Level(string inhabitantId, string skillId) => levels.FirstOrDefault(l => l.Skill == skillId).Level;
    }

    private sealed class Holding(params string[] types) : IHeldItems
    {
        public IReadOnlyList<string> HeldTypes(string inhabitantId) => types;
    }
}
