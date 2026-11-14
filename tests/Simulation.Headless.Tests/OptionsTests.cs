using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;

namespace GlassWorld.Simulation.Headless.Tests;

public sealed class OptionsTests
{
    private static HeadlessOptions Parse(params string[] args) => HeadlessOptions.Parse(args, () => "content-dir");

    [Fact]
    public void DefaultsStayAsBefore()
    {
        var options = Parse();
        Assert.Equal(1UL, options.Seed);
        Assert.Equal(30, options.Days);
        Assert.Equal("content-dir", options.ContentDirectory);
        Assert.Null(options.SavePath);
        Assert.False(options.Quiet);
        Assert.Equal(AiMode.Mock, options.Ai);
        Assert.Equal(0, options.TickMs);
        Assert.Null(options.Until);
        Assert.Null(options.Events);
        Assert.Null(options.LogJsonPath);
    }

    [Fact]
    public void EveryOptionIsRead()
    {
        var options = Parse(
            "--seed", "42", "--days", "3", "--content", "c", "--save", "s.db", "--preset", "start_island", "--climate", "warm", "--quiet",
            "--trace", "inh-000001", "--trace-days", "2", "--ai", "live", "--tick-ms", "52", "--until", "thought:5",
            "--events", "thought,invented", "--log-json", "log.jsonl");
        Assert.Equal(42UL, options.Seed);
        Assert.Equal(3, options.Days);
        Assert.Equal("c", options.ContentDirectory);
        Assert.Equal("s.db", options.SavePath);
        Assert.Equal("start_island", options.Preset);
        Assert.Equal("warm", options.Climate);
        Assert.True(options.Quiet);
        Assert.Equal("inh-000001", options.Trace);
        Assert.Equal(2, options.TraceDays);
        Assert.Equal(AiMode.Live, options.Ai);
        Assert.Equal(52, options.TickMs);
        Assert.Equal(new UntilCondition("thought", 5), options.Until);
        Assert.False(options.Events!.All);
        Assert.Equal(["invented", "thought"], options.Events.Kinds.Order(StringComparer.Ordinal));
        Assert.Equal("log.jsonl", options.LogJsonPath);
    }

    [Theory]
    [InlineData("thought", "thought", 1)]
    [InlineData("thought:5", "thought", 5)]
    [InlineData("first_use:1", "first_use", 1)]
    public void UntilTakesAKindAndAnOptionalCount(string value, string kind, int count) =>
        Assert.Equal(new UntilCondition(kind, count), UntilCondition.Parse(value));

    [Theory]
    [InlineData("thought:0")]
    [InlineData("thought:-1")]
    [InlineData("thought:x")]
    [InlineData(":3")]
    [InlineData("")]
    public void BadUntilValuesAreRefused(string value) =>
        Assert.Throws<ArgumentException>(() => UntilCondition.Parse(value));

    [Fact]
    public void EventsAllPrintsEveryKind()
    {
        var all = EventFilter.Parse("all");
        Assert.True(all.All);
        Assert.True(all.Includes("anything"));
        var some = EventFilter.Parse("thought, built");
        Assert.True(some.Includes("built"));
        Assert.False(some.Includes("invented"));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--ai", "cloud")]
    [InlineData("--tick-ms", "-5")]
    [InlineData("--days")]
    [InlineData("--events", ",")]
    public void BadArgumentsAreRefused(params string[] args) =>
        Assert.ThrowsAny<Exception>(() => Parse(args));

    [Fact]
    public void EventLinesHaveFixedKeysAndEscapedValues()
    {
        var e = new WorldEvent("help_needed", 1234, "inh-000001", null, "obj-000007") { Detail = "two words 100%" };
        Assert.Equal("EVENT tick=1234 kind=help_needed inhabitant=inh-000001 action=- target=obj-000007 detail=two%20words%20100%25", EventLines.Event(e));
        Assert.Equal("CHRONICLE tick=77 kind=first_invention id=3", EventLines.Chronicle(new ChronicleRecord(3, 77, "first_invention", "inh-000002", null, null)));
        Assert.Equal("-", EventLines.Escape(""));
        Assert.Equal("a%0Ab%09c", EventLines.Escape("a\nb\tc"));
    }

    [Fact]
    public void ThePacerOnlyWaitsWhenAskedTo()
    {
        var now = TimeSpan.Zero;
        var waits = new List<TimeSpan>();
        var busyOnly = new Pacer(TimeSpan.Zero, TimeSpan.FromMilliseconds(50), () => now, w => { waits.Add(w); now += w; });
        busyOnly.BeforeTick(busy: false);
        busyOnly.BeforeTick(busy: false);
        Assert.Empty(waits);
        busyOnly.BeforeTick(busy: true);
        busyOnly.BeforeTick(busy: true);
        now += TimeSpan.FromMilliseconds(20);
        busyOnly.BeforeTick(busy: true);
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(30)], waits);

        var fixedPace = new Pacer(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50), () => now, w => { waits.Add(w); now += w; });
        Assert.Equal(TimeSpan.FromMilliseconds(10), fixedPace.IntervalFor(busy: false));
        Assert.Equal(TimeSpan.FromMilliseconds(10), fixedPace.IntervalFor(busy: true));
    }

    [Fact]
    public void TheLearningLogFolderIsTaken()
    {
        // A1620: the headless runner writes the learning log's day files where it is told.
        Assert.Equal("learn", Parse("--learning-log", "learn").LearningLogFolder);
        Assert.Null(Parse().LearningLogFolder);
    }
}
