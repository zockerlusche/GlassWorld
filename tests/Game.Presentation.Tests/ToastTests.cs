using GlassWorld.Contracts;
using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>Hints (D366 section 9, XAG 116), placeholders and table sorting.</summary>
public sealed class ToastTests
{
    private static readonly UiToastTokens Tokens = new() { MaxVisible = 3, MinSeconds = 6, MaxDurationFactor = 10 };
    private static readonly IReadOnlyDictionary<string, string> NoArgs = new Dictionary<string, string>();

    private static ToastMessage Msg(string key, string? group = null) => new(key, NoArgs, group);

    [Fact]
    public void AtMostThreeVisibleTheRestWaitInOrder()
    {
        var queue = new ToastBoard(Tokens, new ToastSettings(1, UntilDismissed: false));
        for (var i = 0; i < 5; i++)
        {
            queue.Push(Msg($"msg.t.n{i}"), fastForward: false);
        }

        Assert.Equal(["msg.t.n0", "msg.t.n1", "msg.t.n2"], queue.Visible.Select(t => t.Message.TextKey));
        Assert.Equal(2, queue.Waiting);
        queue.Advance(6, null);
        Assert.Equal(["msg.t.n3", "msg.t.n4"], queue.Visible.Select(t => t.Message.TextKey));
    }

    [Fact]
    public void DurationFactorStretchesTheDisplayTime()
    {
        var queue = new ToastBoard(Tokens, new ToastSettings(10, UntilDismissed: false));
        queue.Push(Msg("msg.t.a"), fastForward: false);
        queue.Advance(59, null);
        Assert.Single(queue.Visible);
        queue.Advance(1, null);
        Assert.Empty(queue.Visible);
    }

    [Fact]
    public void FactorIsClampedToTheTokens()
    {
        var queue = new ToastBoard(Tokens, new ToastSettings(1000, UntilDismissed: false));
        queue.Push(Msg("msg.t.a"), fastForward: false);
        queue.Advance(60, null);
        Assert.Empty(queue.Visible);
    }

    [Fact]
    public void HoveredHintDoesNotAge()
    {
        var queue = new ToastBoard(Tokens, new ToastSettings(1, UntilDismissed: false));
        queue.Push(Msg("msg.t.a"), fastForward: false);
        var id = queue.Visible[0].Id;
        queue.Advance(100, id);
        Assert.Single(queue.Visible);
        queue.Advance(6, null);
        Assert.Empty(queue.Visible);
    }

    [Fact]
    public void UntilDismissedKeepsHintsUntilClosed()
    {
        var queue = new ToastBoard(Tokens, new ToastSettings(1, UntilDismissed: true));
        queue.Push(Msg("msg.t.a"), fastForward: false);
        queue.Advance(10_000, null);
        Assert.Single(queue.Visible);
        queue.Dismiss(queue.Visible[0].Id);
        Assert.Empty(queue.Visible);
    }

    [Fact]
    public void FastForwardBundlesHintsOfOneGroup()
    {
        var queue = new ToastBoard(Tokens, new ToastSettings(1, UntilDismissed: false));
        for (var i = 0; i < 7; i++)
        {
            queue.Push(Msg("msg.mail.kind.knock", "knock"), fastForward: true);
        }

        queue.Push(Msg("msg.t.other"), fastForward: true);
        Assert.Equal(2, queue.Visible.Count);
        Assert.Equal(7, queue.Visible[0].Count);
    }

    [Fact]
    public void NormalSpeedDoesNotBundle()
    {
        var queue = new ToastBoard(Tokens, new ToastSettings(1, UntilDismissed: false));
        queue.Push(Msg("msg.mail.kind.knock", "knock"), fastForward: false);
        queue.Push(Msg("msg.mail.kind.knock", "knock"), fastForward: false);
        Assert.Equal(2, queue.Visible.Count);
    }

    [Theory]
    [InlineData("Day {day} of {season}", "Day 3 of spring")]
    [InlineData("{season}, Tag {day}", "spring, Tag 3")]
    [InlineData("Unknown {other} stays", "Unknown {other} stays")]
    [InlineData("No braces", "No braces")]
    [InlineData("Open { only", "Open { only")]
    public void PlaceholdersAreFilledByName(string template, string expected) =>
        Assert.Equal(expected, TextFill.Fill(template, new Dictionary<string, string> { ["day"] = "3", ["season"] = "spring" }));

    [Fact]
    public void TableSortIsStable()
    {
        var rows = new[] { ("a", 2), ("b", 1), ("c", 2), ("d", 1) };
        Assert.Equal(["b", "d", "a", "c"], TableSort.By(rows, r => r.Item2, descending: false).Select(r => r.Item1));
        Assert.Equal(["a", "c", "b", "d"], TableSort.By(rows, r => r.Item2, descending: true).Select(r => r.Item1));
    }
}
