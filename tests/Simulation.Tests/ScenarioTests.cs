using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1807 (D1199 section 6, B2837): scenarios of the parameter core, run headless against today's core without AI (a
/// silent decider - the routine alone). A scenario today's core cannot pass stays in as a skipped test whose reason is
/// the finding (like WeatherTests' drought): the list of red scenarios is the finding list, and a fix removes the skip.
/// </summary>
public sealed class ScenarioTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    /// <summary>Buridan's ass: hungry between two equal apples it picks one and eats - no going back and forth.</summary>
    [Fact]
    public void BuridansAssPicksOneOfTwoEqualApplesAndDoesNotSwing()
    {
        var (host, who) = Island();
        Only(who, "hunger", 0.2);
        var left = host.Objects.Spawn("apple", new Position(who.Position.X - 5, who.Position.Z));
        var right = host.Objects.Spawn("apple", new Position(who.Position.X + 5, who.Position.Z));

        var targets = new List<string>();
        var ate = false;
        host.Inhabitants.Reported += e => ate |= e.Kind == InhabitantsSystem.ActionStarted && e.InhabitantId == who.Id && e.Action == "eat";
        for (var t = 0; t < 2 * host.World.Calendar.Settings.TicksPerHour && !ate; t++)
        {
            host.World.Step();
            if (who.Activity?.TargetId is { } target && (targets.Count == 0 || targets[^1] != target))
            {
                targets.Add(target);
            }
        }

        Assert.True(ate, "it eats within two hours");
        var swings = Enumerable.Range(0, Math.Max(0, targets.Count - 2)).Count(i => targets[i] == targets[i + 2] && targets[i] != targets[i + 1]);
        Assert.True(swings == 0, $"no A-B-A between the two apples: {string.Join(" > ", targets)} ({left.Id}, {right.Id})");
    }

    /// <summary>Cold night without knowledge: asleep in the cold it neither wakes again and again nor does nothing - an innate reaction (move, huddle, seek cover).</summary>
    [Fact(Skip = "Finding (A1807 against main a4cb7d17): a sleeper in the cold does nothing - no wake ping-pong, but no innate reaction either (moved 0 m, nearest other 47 m, not sheltered). Waking is only by a need below its neglect line, and warmth's floor 0.3 lies above its line 0.1 (D1220 section 3: wake by stimuli, innate reactions).")]
    public void AColdNightWithoutKnowledgeBringsAnInnateReactionNotAWakePingPong()
    {
        var (host, who) = Island();
        host.Inhabitants.Feels = (inhabitant, sensation) => sensation == "too_cold";
        foreach (var need in who.Needs.Keys.ToList())
        {
            who.Needs[need] = 1.0;
        }

        who.Needs["sleep"] = 0.2;
        var start = who.Position;
        var sleeps = 0;
        host.Inhabitants.Reported += e => sleeps += e.Kind == InhabitantsSystem.ActionStarted && e.InhabitantId == who.Id && e.Action == "sleep" ? 1 : 0;
        host.World.Run(8L * host.World.Calendar.Settings.TicksPerHour);

        Assert.True(sleeps <= 2, $"no wake ping-pong: {sleeps} times lain down in 8 h");
        var moved = Math.Sqrt(Math.Pow(who.Position.X - start.X, 2) + Math.Pow(who.Position.Z - start.Z, 2));
        var others = host.Inhabitants.All.Where(o => o.Id != who.Id).Min(o => Math.Sqrt(Math.Pow(o.Position.X - who.Position.X, 2) + Math.Pow(o.Position.Z - who.Position.Z, 2)));
        Assert.True(moved > 2 || others < 2 || host.Body!.Sheltered(who.Position), $"an innate reaction to the cold: moved {moved:0.0} m, nearest other {others:0.0} m, sheltered {host.Body!.Sheltered(who.Position)}");
    }

    /// <summary>Poison berry: once it has eaten one and felt the harm, it does not eat that kind again (D1199: delayed harm, guess, avoid).</summary>
    [Fact]
    public void APoisonBerryEatenOnceIsAvoidedAfterwards()
    {
        var (host, who) = Island();
        Only(who, "hunger", 0.2);
        var berry = host.Objects.Spawn("red_berry", new Position(who.Position.X + 1, who.Position.Z));
        who.Activity = new Activity("eat", berry.Id, berry.Position, null);
        for (var t = 0; t < 600 && who.Activity is { Action: "eat" }; t++)
        {
            host.World.Step();
        }

        Assert.True(who.Beliefs.TryGet("red_berry", "poisonous", out var belief) && belief.Value, "it learned the berry is poisonous");

        // A day later, hungry, with red berries in reach: none eaten again.
        for (var i = 0; i < 3; i++)
        {
            host.Objects.Spawn("red_berry", new Position(who.Position.X + 1 + i, who.Position.Z + 1));
        }

        var again = 0;
        host.Inhabitants.Reported += e => again += e.Kind == InhabitantsSystem.ActionStarted && e.InhabitantId == who.Id && e.Action == "eat" && e.TargetType == "red_berry" ? 1 : 0;
        for (var t = 0; t < host.World.Calendar.TicksPerDay; t++)
        {
            who.Needs["hunger"] = Math.Min(who.Needs["hunger"], 0.3);
            host.World.Step();
        }

        Assert.Equal(0, again);
    }

    /// <summary>
    /// Unreachable fruit: tried out of reach, the fruit rests (the lock) after at most a few tries. The question it
    /// opens (A1778) is FailureQuestionTests' - here a silent decider keeps the set tries (an answering one replaces
    /// them), and without answers the inhabitant soon has no room for another question.
    /// </summary>
    [Fact]
    public void AnUnreachableFruitRestsAfterAFewTries()
    {
        var (host, who) = Island();
        var high = host.Objects.All.FirstOrDefault(o => o.TypeId == "apple" && host.Map!.AboveCm(o.Id) > host.Content.InhabitantModel.Reach!.HeightCm + 100);
        Assert.NotNull(high);
        var tries = 0;
        host.Inhabitants.Reported += e => tries += e.Kind == InhabitantsSystem.OutOfReach && e.InhabitantId == who.Id && e.TargetId == high!.Id ? 1 : 0;
        for (var i = 0; i < 6 && !who.Resting.Any(r => r.TargetId == high!.Id); i++)
        {
            Only(who, "hunger", 0.2);
            who.Activity = new Activity("eat", high!.Id, high.Position, null);
            for (var t = 0; t < 5000 && who.Activity is { Action: "eat" }; t++)
            {
                host.World.Step();
            }
        }

        Assert.Contains(who.Resting, r => r.TargetId == high!.Id);
        Assert.InRange(tries, 1, 3);
    }

    /// <summary>Unreachable fruit, second half: a known long thing (a branch) is used to get it.</summary>
    [Fact(Skip = "Finding (A1807, today's core): nothing lets an inhabitant use a long thing to reach higher - reach is only the body's height (model reach.height_cm) and climbing (D1105 H3b); a tool for reach is not in the data.")]
    public void AnUnreachableFruitIsReachedWithABranch()
    {
    }

    /// <summary>Coward vs. daredevil: courage filters fear; danger to life overrides it.</summary>
    [Fact(Skip = "Finding (A1807, today's core): courage is a trait without any effect, and fear (near_fire) changes no decision - no utility consideration and no weight reads either, so the scenario cannot be told apart.")]
    public void CourageFiltersFearAndDangerToLifeOverridesIt()
    {
    }

    /// <summary>Player's lie: a tip is a guess of low confidence; failing on it corrects it and passing it on weakens it.</summary>
    [Fact(Skip = "Finding (A1807, today's core): a player tip becomes no belief yet - the chat makes talk only (D1192 #35; tip as a guess with source 'voice' waits for B2830), so there is nothing to fail on or to pass on.")]
    public void APlayersLieIsAWeakGuessThatFailingCorrects()
    {
    }

    private static (SimulationHost Host, Inhabitant Who) Island(IDecisionPort? port = null)
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, port ?? new SilentPort(), "de", "start_island");
        host.World.Run(1);
        return (host, host.Inhabitants.All.First());
    }

    /// <summary>Every need met except one.</summary>
    private static void Only(Inhabitant who, string need, double value)
    {
        foreach (var id in who.Needs.Keys.ToList())
        {
            who.Needs[id] = 1.0;
        }

        who.Needs[need] = value;
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
