using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1684 (B2607): the log's categories as data, hint and chronicle lines, filter and search; identical hints merge.</summary>
public sealed class LogModelTests
{
    private static readonly UiTokens Tokens = ContractSerializer.Deserialize<UiTokens>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "tokens.json")));
    private static readonly IReadOnlyDictionary<string, string> NoArgs = new Dictionary<string, string>();

    private static IEnumerable<string> ChronicleKindIds() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "chronicle", "kinds.json")))!["kinds"]!.AsArray().Select(k => (string)k!["id"]!);

    [Fact]
    public void EveryKindOfLineHasExactlyOneCategory()
    {
        // The conversation's kinds, the hints that became lines and the chronicle's kinds (its 'built' is the log's own line).
        var conversation = Enum.GetValues<LogLineKind>().Where(k => k is not LogLineKind.Hint and not LogLineKind.Chronicle)
            .Select(k => LogModel.KindId(new LogLine(1, "i-1", k, string.Empty, 1, 1)));
        var hints = new[] { LogModel.QuestionHint, LogModel.HelpHint, LogModel.NoteHint };
        var all = conversation.Concat(hints).Concat(ChronicleKindIds()).Distinct().ToList();
        foreach (var kind in all)
        {
            Assert.True(Tokens.Log.Categories.Count(c => c.Kinds.Contains(kind)) == 1, $"kind '{kind}' is in {Tokens.Log.Categories.Count(c => c.Kinds.Contains(kind))} categories");
        }

        // And no category names a kind that does not exist.
        Assert.All(Tokens.Log.Categories.SelectMany(c => c.Kinds), k => Assert.Contains(k, all));
    }

    [Fact]
    public void FailuresAndTheSelfCheckStayInTheHintBox()
    {
        // Review GW3 #463 (B2712): a failed save or a faulted world must be noticed, not be one of five log lines.
        var keys = Tokens.Log.BoxKeys;
        foreach (var key in new[] { "msg.save.failed", "msg.world.faulted", "msg.share.failed", "msg.hint.memory_export_failed", "msg.hint.chronicle_export_failed", "msg.hint.selfcheck" })
        {
            Assert.Contains(key, keys);
        }

        // Every one of them is a real text in both languages.
        foreach (var lang in new[] { "de", "en" })
        {
            var texts = JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "i18n", lang + ".json")))!["entries"]!.AsArray().Select(e => (string)e!["key"]!).ToHashSet();
            Assert.All(keys, k => Assert.Contains(k, texts));
        }
    }

    [Fact]
    public void DiscoveriesIdeasAndBuiltThingsStandOut()
    {
        // B2607: important things (discoveries, ideas such as 'grass + mushroom = container', built things) are easy to find again.
        foreach (var id in new[] { "discovery", "idea", "built", "important" })
        {
            Assert.True(Tokens.Log.Categories.Single(c => c.Id == id).Highlight, id);
        }

        Assert.False(Tokens.Log.Categories.Single(c => c.Id == "thought").Highlight);
    }

    [Fact]
    public void FilterByCategoryAndSearchIgnoringCaseAndAccents()
    {
        var lines = new List<LogLine>
        {
            new(3, "i-1", LogLineKind.Chronicle, "invented", 3, 1, "msg.chronicle.invented", NoArgs),
            new(2, "i-2", LogLineKind.Thought, "Ich schaue mir das Wasser an", 2, 1),
            new(1, "i-1", LogLineKind.Said, "Hallo Jörn", 1, 1),
        };
        string Text(LogLine l) => l.Kind == LogLineKind.Chronicle ? "Elin hat eine Idee: Schieferbecher" : l.Text;
        Assert.Equal([lines[0]], LogModel.Filter(lines, "idea", string.Empty, Tokens.Log, Text));
        Assert.Equal([lines[2]], LogModel.Filter(lines, null, "jorn", Tokens.Log, Text));
        Assert.Equal(lines, LogModel.Filter(lines, null, " ", Tokens.Log, Text));
        Assert.Empty(LogModel.Filter(lines, "talk", "wasser", Tokens.Log, Text));
        Assert.Equal("idea", LogModel.CategoryOf(lines[0], Tokens.Log)!.Id);
        Assert.Equal("ui.log.category.idea", LogModel.CategoryKey("idea"));
    }

    [Fact]
    public void AHintBecomesALineAndTheSameHintAgainCountsUp()
    {
        var log = new ConversationLog(10);
        var args = new Dictionary<string, string> { ["name"] = "Dara" };
        log.AddHint("i-1", LogModel.QuestionHint, "msg.hint.question", args, 5, "i-1");
        log.AddHint("i-1", LogModel.QuestionHint, "msg.hint.question", new Dictionary<string, string> { ["name"] = "Dara" }, 9, "i-1");
        log.AddHint(null, LogModel.NoteHint, "msg.hint.gift_placed", NoArgs, 10);
        var lines = log.Newest();
        Assert.Equal(2, lines.Count);
        Assert.Equal((LogLineKind.Hint, LogModel.NoteHint, string.Empty), (lines[0].Kind, lines[0].Text, lines[0].InhabitantId));
        Assert.Equal((2, 9L, "msg.hint.question"), (lines[1].Count, lines[1].Tick, lines[1].Key));
    }

    [Fact]
    public void NewChronicleEntriesBecomeLinesOldestFirstWithoutBuilt()
    {
        var kinds = ChronicleKindIds().ToDictionary(id => id, id => new ChronicleKind { Id = id, TextKey = "msg.chronicle." + id, Params = ["inhabitant", "target"] });
        ChronicleEntry Entry(string kind, long tick) => new(tick, "time.season.spring", 1, 1, kinds[kind], "i-1", "type-1", null);

        // The book is newest first; two of its three entries are new.
        var book = new List<ChronicleEntry> { Entry("built", 30), Entry("invented", 20), Entry("born", 10) };
        var log = new ConversationLog(10);
        log.AddChronicle(book, 2, e => (e.Kind.TextKey, new Dictionary<string, string> { ["inhabitant"] = "Elin" }));
        var line = Assert.Single(log.Newest());
        Assert.Equal((LogLineKind.Chronicle, "invented", "msg.chronicle.invented", 20L), (line.Kind, line.Text, line.Key, line.Tick));
    }

    [Fact]
    public void TheVerySameHintAgainCountsUpEvenAtNormalSpeed()
    {
        // TL #25508: "Dara knocks" twice is one hint with a count, not two boxes.
        var board = new ToastBoard(Tokens.Toast, new ToastSettings(1, UntilDismissed: false));
        var knock = new ToastMessage("msg.hint.knock", new Dictionary<string, string> { ["name"] = "Dara" }, "knock|i-1", "i-1");
        board.Push(knock, fastForward: false);
        board.Push(knock with { Args = new Dictionary<string, string> { ["name"] = "Dara" } }, fastForward: false);
        board.Push(knock with { Args = new Dictionary<string, string> { ["name"] = "Oda" }, Group = "knock|i-2", LookAt = "i-2" }, fastForward: false);
        Assert.Equal([2, 1], board.Visible.Select(t => t.Count));
        Assert.Equal(2, Tokens.Toast.MaxVisible);
    }
}
