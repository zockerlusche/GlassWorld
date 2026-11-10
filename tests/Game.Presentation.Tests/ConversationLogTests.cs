using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.Tests;

public sealed class ConversationLogTests
{
    private static WorldEvent Event(string kind, string who, string? text, long tick = 1) => new(kind, tick, who, null, null) { Text = text };

    [Fact]
    public void KeepsThoughtsSentencesAndThePlayerNewestFirst()
    {
        var log = new ConversationLog(10);
        log.Add([Event("conversation_started", "i-1", "Hallo"), Event("said", "i-1", "Hallo du"), Event("thought", "i-2", "Wasser"), Event("moved", "i-2", null)]);
        var lines = log.Newest();
        Assert.Equal([LogLineKind.Thought, LogLineKind.Said, LogLineKind.Player], lines.Select(l => l.Kind));
        Assert.Equal("i-2", lines[0].InhabitantId);
    }

    [Fact]
    public void TheSameLineAgainCountsUpAndMovesToTheTop()
    {
        var log = new ConversationLog(10);
        log.Add([Event("thought", "i-1", "Ich gehe zum Menschen", 1), Event("thought", "i-2", "Wasser", 2), Event("thought", "i-1", "Ich gehe zum Menschen", 3)]);
        var lines = log.Newest();
        Assert.Equal(2, lines.Count);
        Assert.Equal(("i-1", 2, 3L), (lines[0].InhabitantId, lines[0].Count, lines[0].Tick));
    }

    [Fact]
    public void KeepsOnlyTheNewestLines()
    {
        var log = new ConversationLog(2);
        log.Add([Event("thought", "i-1", "a"), Event("thought", "i-1", "b"), Event("thought", "i-1", "c")]);
        Assert.Equal(["c", "b"], log.Newest().Select(l => l.Text));
    }

    [Fact]
    public void EmptyTextsAreNoLines()
    {
        var log = new ConversationLog(5);
        log.Add([Event("thought", "i-1", ""), Event("said", "i-1", null), Event("not_understood", "i-1", null)]);
        Assert.Equal([LogLineKind.NotUnderstood], log.Newest().Select(l => l.Kind));
    }

    [Fact]
    public void NoAnswerIsALineWithoutTheKeyAsWords()
    {
        var log = new ConversationLog(5);
        log.Add([Event("no_answer", "i-1", "msg.converse.no_answer")]);
        var line = Assert.Single(log.Newest());
        Assert.Equal((LogLineKind.NoAnswer, string.Empty), (line.Kind, line.Text));
    }

    [Fact]
    public void NotUnderstoodKeepsItsTextWhenTheCoreSendsOne()
    {
        var log = new ConversationLog(5);
        log.Add([Event("not_understood", "i-1", "Was meinst du?")]);
        Assert.Equal("Was meinst du?", Assert.Single(log.Newest()).Text);
    }
}
