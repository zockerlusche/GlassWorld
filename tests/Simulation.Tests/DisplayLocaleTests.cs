using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>#20043: the player's language reaches the requests, and replays stay identical across a switch.</summary>
public sealed class DisplayLocaleTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void AHostCreatedInEnglishAsksInEnglish()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "en");
        host.World.Run(host.World.Calendar.TicksPerDay / 2);

        var locales = RequestLocales(host);
        Assert.NotEmpty(locales);
        Assert.All(locales, l => Assert.Equal(Locale.En, l.Locale));
        Assert.Equal("en", host.DisplayLocale);
    }

    [Fact]
    public void ASwitchMidGameChangesNewRequestsOnlyAndIsReplayed()
    {
        var original = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "en");
        original.World.Run(original.World.Calendar.TicksPerDay / 2);
        var before = RequestLocales(original);
        Assert.True(original.Decisions.Submit(new PlayerCommand(DecisionsSystem.SetDisplayLocale, null) { Text = "de" }).Accepted);
        var switchTick = original.World.Tick;
        original.World.Run(original.World.Calendar.TicksPerDay / 2);

        // Questions asked before stay as they were (no re-run); the new ones are German.
        var all = RequestLocales(original);
        Assert.Equal(before, all.Take(before.Count));
        var after = all.Skip(before.Count).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, l => Assert.True(l.Tick >= switchTick && l.Locale == Locale.De));

        // The replay starts in the default language like every replay and follows the logged commands.
        var replay = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new ReplayDecisionPort(original.Decisions.Log), DecisionsSystem.DefaultDisplayLocale);
        replay.Decisions.LoadReplay(original.Decisions.Log);
        replay.World.Run(original.World.Tick);
        Assert.Equal(original.Decisions.Log.Entries, replay.Decisions.Log.Entries);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void TheDefaultLanguageLogsNothingSoOldLogsStayAsTheyWere()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), DecisionsSystem.DefaultDisplayLocale);
        Assert.DoesNotContain(host.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Command);
    }

    [Theory]
    [InlineData("en", "de", "de")]
    [InlineData("en", null, "en")]
    [InlineData("de", null, "de")]
    public void ARestoredWorldGoesOnInTheLanguageItsLogEndsWith(string start, string? switched, string expected)
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), start);
        if (switched is not null)
        {
            host.Decisions.Submit(new PlayerCommand(DecisionsSystem.SetDisplayLocale, null) { Text = switched });
        }

        host.World.Run(10);
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));

        // The factory's language does not matter: a restore follows the log (old saves: the default).
        var restored = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "en"), StateCodec.Capture(host));
        Assert.Equal(expected, restored.DisplayLocale);
        Assert.Equal(expected, restored.Inventions.DisplayLocale);
        Assert.Equal(expected, restored.Conversation.DisplayLocale);
        Assert.Equal(host.World.StateHash(), restored.World.StateHash());
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("EN")]
    [InlineData("1")]
    [InlineData(null)]
    public void AnUnknownLanguageIsRefused(string? locale)
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
        Assert.False(host.Decisions.Submit(new PlayerCommand(DecisionsSystem.SetDisplayLocale, null) { Text = locale }).Accepted);
        Assert.Throws<ArgumentException>(() => SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "fr"));
    }

    private static List<(long Tick, Locale Locale)> RequestLocales(SimulationHost host) =>
        [.. host.Decisions.Log.Entries
            .Where(e => e.Kind == DecisionLogKind.Request)
            .Select(e => (e.Tick, ContractSerializer.Deserialize<DecisionRequest>(e.Json).DisplayLocale))];
}
