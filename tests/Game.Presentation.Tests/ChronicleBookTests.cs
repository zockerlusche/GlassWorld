using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.Tests;

/// <summary>Chronicle base lines (B8 R9/R10, D471 D8): kinds from data, parameters, newest first.</summary>
public sealed class ChronicleBookTests
{
    private static readonly Contracts.ChronicleKinds Kinds =
        ContractSerializer.Deserialize<Contracts.ChronicleKinds>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "chronicle", "kinds.json")));

    private static readonly WorldCalendar Calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));

    private static readonly ChronicleNames Names = new(
        id => "Bewohner " + WorldFeed.NumberOf(id),
        id => id == "obj-000007" ? "Steinbeil" : null,
        action => "vocab.action." + action);

    [Fact]
    public void ChronicleEventsBecomeBaseLinesNewestFirst()
    {
        var book = new ChronicleBook(Kinds, Calendar);
        var day2 = Calendar.TicksPerDay + 5;
        var added = book.Add(
            [
                new WorldEvent("invented", 5, "inh-000002", null, "obj-000007"),
                new WorldEvent(InhabitantsSystem.ActionStarted, 6, "inh-000002", "eat", null),
                new WorldEvent("first_use", day2, "inh-000003", "use", "obj-000007"),
            ]);

        Assert.Equal(2, added);
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
    public void UnknownThingsAndActionsGetANeutralWord()
    {
        var book = new ChronicleBook(Kinds, Calendar);
        book.Add([new WorldEvent("invented", 1, "inh-000001", null, "obj-999999"), new WorldEvent("discovered_process", 2, "inh-000001", null, null)]);
        Assert.Equal("ui.chronicle.something", ChronicleBook.Line(book.Entries[1], Names).Args["target"]);
        Assert.Equal("ui.chronicle.something", ChronicleBook.Line(book.Entries[0], Names).Args["action"]);
    }

    [Fact]
    public void NamesAreResolvedWhenShownNotWhenAdded()
    {
        // GW1 review #64 C2: a name given later (or another language) shows in old entries too.
        var book = new ChronicleBook(Kinds, Calendar);
        book.Add([new WorldEvent("invented", 1, "inh-000004", null, "obj-000007")]);
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
}
