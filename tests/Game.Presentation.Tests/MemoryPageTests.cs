using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Player;

namespace GlassWorld.Presentation.Tests;

/// <summary>B8 R16 (B2518): the memory page puts together what the world kept about one inhabitant - nothing more.</summary>
public sealed class MemoryPageTests
{
    private static ChronicleEntry Event(long tick, string who, string? target = null) =>
        new(tick, "time.season.spring", 1, 1, new GlassWorld.Contracts.ChronicleKind { Id = "test" }, who, target, null);

    [Fact]
    public void ThePageHoldsTheInhabitantsEventsAndTalkInTimeOrder()
    {
        var chronicle = new[] { Event(5, "inh-1"), Event(7, "inh-2"), Event(9, "inh-2", "inh-1"), Event(20, "inh-1") };
        var talk = new[] { new ConversationLineState(true, 9, "de", "Hallo"), new ConversationLineState(false, 12, "de", "Hallo du") };

        var items = MemoryPage.Items("inh-1", chronicle, talk);

        Assert.Equal([5L, 9, 9, 12, 20], items.Select(i => i.Tick));
        Assert.NotNull(items[1].Event); // an event at the same tick as talk comes first
        Assert.Equal("Hallo", items[2].Talk!.Text);
        Assert.DoesNotContain(items, i => i.Event is { InhabitantId: "inh-2", TargetId: null }); // others' events stay out
        Assert.Contains(items, i => i.Event is { InhabitantId: "inh-2", TargetId: "inh-1" }); // about it counts
    }

    [Fact]
    public void AnInhabitantWithoutHistoryHasAnEmptyPage()
    {
        Assert.Empty(MemoryPage.Items("inh-3", [Event(5, "inh-1")], []));
        Assert.Equal("Leer\n====\n\n", MemoryPage.Text([], "Leer", t => t / 10, d => "Tag " + d, _ => "x"));
    }

    [Fact]
    public void TheExportGroupsByDayAndEncodesTheHtml()
    {
        var items = MemoryPage.Items("inh-1", [Event(5, "inh-1")], [new ConversationLineState(false, 25, "en", "<hi> & bye")]);

        var text = MemoryPage.Text(items, "Erinnerungen", t => t / 10, d => "Tag " + d, i => i.Talk?.Text ?? "Ereignis");
        Assert.Equal("Erinnerungen\n============\n\nTag 0\n- Ereignis\n\nTag 2\n- <hi> & bye\n\n", text);

        var html = MemoryPage.Html(items, "Erinnerungen", "de", t => t / 10, d => "Tag " + d, i => i.Talk?.Text ?? "Ereignis");
        Assert.Contains("<h2>Tag 2</h2>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;hi&gt; &amp; bye", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }
}
