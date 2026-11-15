using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Tests;

/// <summary>A1750 (B2768): old requests keep only a fingerprint; answers, commands and recent requests stay whole.</summary>
public sealed class DecisionLogTests
{
    private const string Asked = "{\"occasion\":\"unknown_object\"}";
    private const string Answered = "{\"option_id\":\"a\"}";
    private const string Locale = "{\"Kind\":\"set_display_locale\",\"Text\":\"en\"}";

    [Fact]
    public void ARequestOlderThanTheWindowKeepsOnlyItsFingerprintAndStillAgreesWithTheWholeOne()
    {
        var log = new DecisionLog { FullRequestTicks = 100 };
        var asked = new DecisionLogEntry(DecisionLogKind.Request, "r1", 10, Asked, null);
        log.Add(asked);
        log.Add(new DecisionLogEntry(DecisionLogKind.Response, "r1", 20, Answered, null));
        log.Add(new DecisionLogEntry(DecisionLogKind.JobRequest, "j1", 60, Asked, null));
        Assert.Equal(asked, log.Entries[0]); // still within the window

        log.Add(new DecisionLogEntry(DecisionLogKind.Command, string.Empty, 130, Locale, null));
        var kept = log.Entries[0];
        Assert.True(DecisionLog.IsCompacted(kept.Json));
        Assert.True(kept.Json.Length < 120, kept.Json);
        Assert.True(DecisionLog.Agree(asked, kept) && DecisionLog.Agree(kept, asked));
        Assert.False(DecisionLog.Agree(asked with { Json = Answered }, kept));
        Assert.False(DecisionLog.Agree(asked with { Tick = 11 }, kept));
        Assert.Equal(kept.Json, DecisionLog.Compacted(kept.Json));

        // The answer, the job request within the window and the command stay whole.
        Assert.Equal(Answered, log.Entries[1].Json);
        Assert.Equal(Asked, log.Entries[2].Json);
        Assert.Equal(Locale, log.Entries[3].Json);
        log.Add(new DecisionLogEntry(DecisionLogKind.Response, "r2", 200, Answered, null));
        Assert.True(DecisionLog.IsCompacted(log.Entries[2].Json));
        Assert.Equal(Locale, log.Entries[3].Json);
    }

    [Fact]
    public void WithoutAWindowEveryRequestStaysWhole()
    {
        var log = new DecisionLog();
        log.Add(new DecisionLogEntry(DecisionLogKind.Request, "r1", 10, Asked, null));
        log.Add(new DecisionLogEntry(DecisionLogKind.Response, "r1", 100_000, Answered, null));
        Assert.Equal(Asked, log.Entries[0].Json);
    }
}
