using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.State;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// A1338 (B6-6): the question mark over an inhabitant stays while its question is unanswered, several inhabitants show one at
/// the same time, and after saving and loading the same marks are back (the list reads them from the core).
/// </summary>
public sealed class QuestionMarkTests
{
    private static readonly string ContentDirectory = Path.Combine(TestPaths.Root, "content");

    [Fact]
    public void MarksStayWhileUnansweredSeveralAtOnceAndComeBackAfterLoading()
    {
        // A decider that never answers: what puzzles an inhabitant stays open.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 1, new SilentPort(), "de", "start_island");
        var limit = 2 * host.World.Calendar.TicksPerDay;
        static int Marked(SimulationHost h) => new WorldFeed(h).Inhabitants().Count(r => r.PuzzlingQuestions > 0);
        while (host.World.Tick < limit && Marked(host) < 2)
        {
            host.World.Step();
        }

        var before = new WorldFeed(host).Inhabitants().ToDictionary(r => r.Id, r => r.PuzzlingQuestions, StringComparer.Ordinal);
        Assert.True(before.Count(m => m.Value > 0) >= 2, "two inhabitants need a question mark at the same time");

        // Unanswered, it stays: a few more steps keep every mark that was there.
        for (var i = 0; i < 10; i++)
        {
            host.World.Step();
        }

        var later = new WorldFeed(host).Inhabitants().ToDictionary(r => r.Id, r => r.PuzzlingQuestions, StringComparer.Ordinal);
        Assert.All(before.Where(m => m.Value > 0), m => Assert.True(later[m.Key] > 0, $"{m.Key} lost its mark without an answer"));

        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        // Through the save file, as the game loads it (review GW2 #596: the border where load bugs live).
        var path = Path.Combine(Path.GetTempPath(), $"gw-question-marks-{Guid.NewGuid():N}.db");
        SimulationHost restored;
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            restored = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new SilentPort(), "de"), SaveGameStore.Load(path));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }

        var loaded = new WorldFeed(restored).Inhabitants().ToDictionary(r => r.Id, r => r.PuzzlingQuestions, StringComparer.Ordinal);
        Assert.Equal(later, loaded);
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
