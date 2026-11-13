using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// The learning log per game day (A1620, B2685 part B, Marcel W1947): it reads along the core's events and writes one
/// readable Markdown file per finished day next to the save - what each inhabitant learned and from whom, in the
/// player's language. It is no world state: the hash stays as it was.
/// </summary>
public sealed class LearningLogTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "glassworld-learnlog-" + Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void KnowledgeHeardIsLoggedWithItsValueAndWhoToldIt()
    {
        var (host, mira, joro) = TwoInhabitants();
        mira.Beliefs.Set("conifer_tree", "edible", false, 0.8, KnowledgeSources.Heard, joro.Id, 5);
        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, 5, mira.Id, "listen", "conifer_tree") { Detail = "edible/" + KnowledgeSources.Heard });
        host.Inhabitants.Report(new WorldEvent("action_started", 6, mira.Id, "eat", null)); // not a kind of the log

        var entry = Assert.Single(host.LearningLog!.Entries);
        Assert.Equal(("conifer_tree", "edible", KnowledgeSources.Heard, (bool?)false, joro.Id), (entry.TypeId, entry.Property, entry.Source, entry.Value, entry.From));
    }

    [Fact]
    public void AFinishedDayIsWrittenOnceInThePlayersLanguageAndTheHashStaysTheSame()
    {
        // The same world without the log reading along has the same hash: the log is no world state.
        var (host, mira, joro) = TwoInhabitants();
        var (without, mira2, joro2) = TwoInhabitants();
        without.Inhabitants.Reported -= without.LearningLog!.Observe;
        foreach (var (h, m, j) in new[] { (host, mira, joro), (without, mira2, joro2) })
        {
            m.Beliefs.Set("conifer_tree", "edible", false, 0.8, KnowledgeSources.Heard, j.Id, 5);
            h.Inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, 5, m.Id, "listen", "conifer_tree") { Detail = "edible/" + KnowledgeSources.Heard });
            h.Inhabitants.Report(new WorldEvent("first_meeting", 7, m.Id, null, j.Id));
        }

        Assert.Equal(without.World.StateHash(), host.World.StateHash());

        // Still the first day: nothing is finished, nothing written.
        Assert.Empty(LearningLogWriter.WriteFinishedDays(host, _folder, ContentDirectory));
        host.World.Run(host.World.Calendar.TicksPerDay - host.World.Tick);

        var file = Assert.Single(LearningLogWriter.WriteFinishedDays(host, _folder, ContentDirectory));
        var text = File.ReadAllText(file);
        Assert.Contains("# Lern-Protokoll – Tag 1", text, StringComparison.Ordinal);
        Assert.Contains($"hört von {joro.Name}: Nadelbaum ist nicht essbar", text, StringComparison.Ordinal);
        Assert.Contains($"trifft {joro.Name} zum ersten Mal", text, StringComparison.Ordinal);
        Assert.Contains($"## {joro.Name}", text, StringComparison.Ordinal); // one section per inhabitant

        // A day already written is never overwritten.
        File.WriteAllText(file, "kept");
        host.Inhabitants.Report(new WorldEvent("first_meeting", 8, joro.Id, null, mira.Id));
        Assert.Empty(LearningLogWriter.WriteFinishedDays(host, _folder, ContentDirectory));
        Assert.Equal("kept", File.ReadAllText(file));
    }

    private static (SimulationHost Host, Inhabitant Mira, Inhabitant Joro) TwoInhabitants()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 3, new MockDecisionPort(), "de");
        var model = Content.Value.InhabitantModel;
        Inhabitant Make(string id, string name, double x)
        {
            var inhabitant = new Inhabitant(
                id,
                "female",
                1,
                new Position(x, 0),
                model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
                model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
                model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
                model.WillToLive.Start)
            { Name = name };
            host.Inhabitants.Add(inhabitant);
            return inhabitant;
        }

        return (host, Make("inh-000001", "Mira", 0), Make("inh-000002", "Joro", 3));
    }
}
