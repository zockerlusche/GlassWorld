using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// Chronicle base lines (B8 R9/R10, D471 D8): the window reads the core's chronicle (saved state, GW1
/// #75), kinds from data, parameters, newest first, invention names in DE and EN.
/// </summary>
public sealed class ChronicleBookTests
{
    private static readonly ChronicleKinds Kinds =
        ContractSerializer.Deserialize<ChronicleKinds>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "chronicle", "kinds.json")));

    private static readonly WorldCalendar Calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));

    private static readonly ChronicleNames Names = new(
        id => "Bewohner " + WorldFeed.NumberOf(id),
        id => id == "obj-000007" ? "Steinbeil" : null,
        action => "vocab.action." + action);

    [Fact]
    public void CoreChronicleEntriesBecomeBaseLinesNewestFirst()
    {
        var core = new ChronicleSystem(Kinds);
        var day2 = Calendar.TicksPerDay + 5;
        core.Observe(new WorldEvent("invented", 5, "inh-000002", null, "obj-000007"));
        core.Observe(new WorldEvent(InhabitantsSystem.ActionStarted, 6, "inh-000002", "eat", null));
        core.Observe(new WorldEvent("first_use", day2, "inh-000003", "use", "obj-000007"));

        var book = new ChronicleBook(Kinds, Calendar);
        Assert.Equal(2, book.Sync(core));
        Assert.Equal(1, book.Version);
        var newest = book.Entries[0];
        var (key, args) = ChronicleBook.Line(newest, Names);
        Assert.Equal("msg.chronicle.first_use", key);
        Assert.Equal("Bewohner 3", args["inhabitant"]);
        Assert.Equal("Steinbeil", args["target"]);
        Assert.Equal(2, newest.Day);
        var oldest = book.Entries[1];
        Assert.Equal("msg.chronicle.invented", ChronicleBook.Line(oldest, Names).TextKey);
        Assert.Equal(1, oldest.Day);
        Assert.Equal(1, oldest.Year);
        Assert.StartsWith("time.season.", oldest.SeasonKey, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncTakesOnlyWhatIsNewAndTheHistoryOfALoadedGame()
    {
        // A loaded save game brings its chronicle; the window shows it from the start (not only new events).
        var saved = new ChronicleSystem(Kinds);
        saved.Observe(new WorldEvent("invented", 5, "inh-000001", null, "obj-000007"));
        var loaded = new ChronicleSystem(Kinds);
        loaded.Restore(saved.All);

        var book = new ChronicleBook(Kinds, Calendar);
        Assert.Equal(1, book.Sync(loaded));
        Assert.Equal(0, book.Sync(loaded));
        Assert.Equal(1, book.Version);

        loaded.Observe(new WorldEvent("first_use", 9, "inh-000002", "use", "obj-000007"));
        Assert.Equal(1, book.Sync(loaded));
        Assert.Equal(2, book.Entries.Count);
        Assert.Equal(2, book.Version);
    }

    [Fact]
    public void InventionsAreNamedInTheDisplayLanguageAlsoThroughAPlacedObject()
    {
        var catalog = new Catalog(new InventionInfo("rt-000001", new LocalizedName { De = "Wasserschale", En = "water bowl" }, default, "drink", "inh-000001", 5));
        string? TypeOf(string id) => id == "obj-000042" ? "rt-000001" : null;

        // "invented" names the type, "first_use" a placed object of it.
        Assert.Equal("Wasserschale", ChronicleBook.InventionName("rt-000001", catalog, TypeOf, "de"));
        Assert.Equal("water bowl", ChronicleBook.InventionName("obj-000042", catalog, TypeOf, "en_US"));
        Assert.Null(ChronicleBook.InventionName("obj-000001", catalog, TypeOf, "de"));
    }

    [Fact]
    public void UnknownThingsAndActionsGetANeutralWord()
    {
        var book = new ChronicleBook(Kinds, Calendar);
        book.Add([new ChronicleRecord(1, 1, "invented", "inh-000001", "obj-999999", null), new ChronicleRecord(2, 2, "discovered_process", "inh-000001", null, null)]);
        Assert.Equal("ui.chronicle.something", ChronicleBook.Line(book.Entries[1], Names).Args["target"]);
        Assert.Equal("ui.chronicle.something", ChronicleBook.Line(book.Entries[0], Names).Args["action"]);
    }

    [Fact]
    public void NamesAreResolvedWhenShownNotWhenAdded()
    {
        // GW1 review #64 C2: a name given later (or another language) shows in old entries too.
        var book = new ChronicleBook(Kinds, Calendar);
        book.Add([new ChronicleRecord(1, 1, "invented", "inh-000004", "obj-000007", null)]);
        var later = Names with { Inhabitant = _ => "Mara" };
        Assert.Equal("Mara", ChronicleBook.Line(book.Entries[0], later).Args["inhabitant"]);
    }

    [Fact]
    public void EveryKindHasItsTextWithAllPlaceholdersInBothLanguages()
    {
        foreach (var locale in new[] { "de", "en" })
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "i18n", locale + ".json")));
            var texts = doc.RootElement.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("key").GetString()!, e => e.GetProperty("text").GetString()!, StringComparer.Ordinal);
            foreach (var kind in Kinds.Kinds)
            {
                Assert.True(texts.TryGetValue(kind.TextKey, out var text), $"{locale}: {kind.TextKey} missing");
                foreach (var param in kind.Params)
                {
                    Assert.Contains("{" + param + "}", text, StringComparison.Ordinal);
                }
            }
        }
    }

    private sealed class Catalog(params InventionInfo[] inventions) : IRuntimeCatalog
    {
        public IReadOnlyList<InventionInfo> Inventions => inventions;

        public InventionInfo? Invention(string typeId) => inventions.FirstOrDefault(i => i.TypeId == typeId);
    }
}
