using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Audio;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Tests;

/// <summary>Sound at the object: an invented thing sounds like itself (D471 D7, A1446).</summary>
public sealed class InventionSoundTests
{
    private static readonly string Sound = Path.Combine(TestPaths.Root, "content", "sound");

    private static readonly BaseSounds Library = new(ContractSerializer.Deserialize<BaseSoundLibrary>(File.ReadAllText(Path.Combine(Sound, "library.json"))));

    [Fact]
    public void ItsOwnMadeSoundFirstThenALibrarySoundByTheDescriptionElseNothing()
    {
        var (objects, made, plain) = World();
        var folder = Path.Combine(Path.GetTempPath(), "gw-sounds");
        var withFile = new InventionSounds(Catalog("snd-000001.wav", "wooden door creaks"), objects, Library, folder, _ => true);
        Assert.Equal(new InventionSound(Path.Combine(folder, "snd-000001.wav"), null), withFile.For(made));

        // The file is not there (not made yet, or failed): the base library by the rule guard's description.
        var noFile = new InventionSounds(Catalog("snd-000001.wav", "wooden door creaks"), objects, Library, folder, _ => false);
        var fallback = noFile.For(made);
        Assert.NotNull(fallback?.LibrarySoundId);
        Assert.Null(fallback!.FilePath);

        // No invention, no description: nothing special, the action's own sound plays.
        Assert.Null(withFile.For(plain));
        Assert.Null(withFile.For(null));
        Assert.Null(new InventionSounds(Catalog(null, null), objects, Library, folder, _ => false).For(made));
    }

    [Fact]
    public void UsingAnInventedThingPlaysItsSoundOnTheEffectsBus()
    {
        var (objects, made, _) = World();
        var sounds = new InventionSounds(Catalog("snd-000001.wav", "wooden door creaks"), objects, Library, "C:/made", _ => true);
        var library = SoundLibrary.FromJson(File.ReadAllText(Path.Combine(Sound, "sound_map.json")), File.ReadAllText(Path.Combine(Sound, "synth_recipes.json")));
        var director = new SoundDirector(library, SoundMixer.FromJson(File.ReadAllText(Path.Combine(Sound, "mix.json"))));
        var use = new WorldEvent(InhabitantsSystem.ActionStarted, 5, "inh-000001", "use", made);
        var at = new Dictionary<string, Position>(StringComparer.Ordinal) { ["inh-000001"] = new Position(0, 0) };
        var plan = director.Plan([use], at, new Position(0, 0), 0.5, new Dictionary<MixBusId, int>(), e => e.Action == "use" ? sounds.For(e.TargetId) : null);
        var request = Assert.Single(plan);
        Assert.Equal((MixBusId.Effects, Path.Combine("C:/made", "snd-000001.wav")), (request.Bus, request.File));

        // Without the resolver the action's own sound plays, as before.
        Assert.Null(Assert.Single(director.Plan([use], at, new Position(0, 0), 0.5, new Dictionary<MixBusId, int>())).File);
    }

    [Fact]
    public void TheFolderOfMadeSoundsFollowsTheServiceConfigOrTheEnvironment()
    {
        var content = Path.Combine(TestPaths.Root, "content");
        Assert.Equal(Path.GetFullPath(Path.Combine(TestPaths.Root, ".local", "ai-service", "sounds")), InventionSounds.DefaultFolder(content, null));
        Assert.Equal(Path.GetFullPath("D:/sounds"), InventionSounds.DefaultFolder(content, "D:/sounds"));
    }

    private static (WorldObjects Objects, string Made, string Plain) World()
    {
        var host = SimulationHost.CreateEmpty(Path.Combine(TestPaths.Root, "content"), 1, new MockDecisionPort(), "de");
        var types = host.Content.Objects.TypeIds.Take(2).ToList();
        var made = host.Objects.Spawn(types[0], new Position(1, 0)).Id;
        var plain = host.Objects.Spawn(types[1], new Position(2, 0)).Id;
        InventedType = types[0];
        return (host.Objects, made, plain);
    }

    private static string InventedType { get; set; } = string.Empty;

    private static FakeCatalog Catalog(string? file, string? description) =>
        new(new InventionInfo(InventedType, new LocalizedName { De = "Tür", En = "door" }, default, "use", "inh-000001", 1, file, description));

    private sealed class FakeCatalog(params InventionInfo[] inventions) : IRuntimeCatalog
    {
        public IReadOnlyList<InventionInfo> Inventions => inventions;

        public InventionInfo? Invention(string typeId) => inventions.FirstOrDefault(i => i.TypeId == typeId);
    }
}
