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
    public void AKnockAtTheGlassIsALineOfItsOwn()
    {
        // B8 R17 (B2608): "Hano klopft an die Scheibe" in the log; other mail is no line.
        var log = new ConversationLog(10);
        log.Add([
            new WorldEvent(Simulation.Player.MailboxSystem.MailArrived, 4, "i-1", null, "mail-1") { Detail = Simulation.Player.MailboxSystem.KnockKind },
            new WorldEvent(Simulation.Player.MailboxSystem.MailArrived, 4, "i-2", null, "mail-2") { Detail = "gift" },
        ]);

        var line = Assert.Single(log.Newest());
        Assert.Equal(("i-1", LogLineKind.Knock, string.Empty), (line.InhabitantId, line.Kind, line.Text));
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
    public void ADeclineWithoutWordsIsALineOfWhatWasSeen()
    {
        // TL #21712: no fixed sentence in the inhabitant's mouth; the log says it declined without a word.
        var log = new ConversationLog(5);
        log.Add([Event("declined", "i-1", null), Event("declined", "i-2", "Nein, danke.")]);
        var lines = log.Newest();
        Assert.Equal([(LogLineKind.Said, "Nein, danke."), (LogLineKind.Declined, string.Empty)], lines.Select(l => (l.Kind, l.Text)));
    }

    [Fact]
    public void NotUnderstoodKeepsItsTextWhenTheCoreSendsOne()
    {
        var log = new ConversationLog(5);
        log.Add([Event("not_understood", "i-1", "Was meinst du?")]);
        Assert.Equal("Was meinst du?", Assert.Single(log.Newest()).Text);
    }
}
