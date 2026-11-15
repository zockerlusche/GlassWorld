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

        var entry = host.LearningLog!.Entries.Single();
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

    [Fact]
    public void ANounOrVerbPropertyReadsAsASentenceNotInLowerCase()
    {
        // D1085: 'Pyrit ist stein' and 'ist schlägt funken' - such labels have their own phrase per truth.
        var (host, mira, joro) = TwoInhabitants();
        mira.Beliefs.Set("pyrite", "stone", true, 0.8, KnowledgeSources.Heard, joro.Id, 5);
        mira.Beliefs.Set("pyrite", "sparks", false, 0.8, KnowledgeSources.Heard, joro.Id, 5);
        mira.Beliefs.Set("pyrite", "edible", false, 0.8, KnowledgeSources.Heard, joro.Id, 5);
        foreach (var property in new[] { "stone", "sparks", "edible" })
        {
            host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, 5, mira.Id, "listen", "pyrite") { Detail = property + "/" + KnowledgeSources.Heard });
        }

        host.World.Run(host.World.Calendar.TicksPerDay - host.World.Tick);
        var text = File.ReadAllText(Assert.Single(LearningLogWriter.WriteFinishedDays(host, _folder, ContentDirectory)));
        // D1112 #9: one sentence - what it is, then what it is not; the verb once while the phrases before use it.
        Assert.Contains($"hört von {joro.Name}: Pyrit ist Stein, aber nicht essbar und schlägt keine Funken", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWordTheVoiceSaidForAThingIsLoggedWithTheWord()
    {
        // #352: word_heard carries the word in its detail and the type as its target.
        var (host, mira, _) = TwoInhabitants();
        host.Inhabitants.Report(new WorldEvent("word_heard", 9, mira.Id, null, "spring_water") { Detail = "Glitzerwasser" });
        var entry = host.LearningLog!.Entries.Single();
        Assert.Equal(("spring_water", "Glitzerwasser"), (entry.TypeId, entry.Detail));
    }

    [Fact]
    public void FactsReadAsOneSentenceWhatItIsThenWhatItIsNot()
    {
        // D1112 #9 (Marcel): 'ist selbst gemacht, ist nicht heiss, ist nicht isolierend' read like a list of forms.
        var (host, mira, _) = TwoInhabitants();
        foreach (var (property, value) in new[] { ("drinkable", false), ("edible", true), ("salty", false), ("fibrous", true) })
        {
            mira.Beliefs.Set("conifer_tree", property, value, 0.9, KnowledgeSources.Experienced, null, 5);
            host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, 5, mira.Id, "eat", "conifer_tree") { Detail = property + "/" + KnowledgeSources.Experienced });
        }

        host.World.Run(host.World.Calendar.TicksPerDay - host.World.Tick);
        var text = File.ReadAllText(Assert.Single(LearningLogWriter.WriteFinishedDays(host, _folder, ContentDirectory)));
        Assert.Contains("erfährt selbst: Nadelbaum ist essbar und faserig, aber nicht trinkbar und nicht salzig", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFinishedDayHasItsFileEvenWithoutALine()
    {
        // D1112 #9: day 24 was missing - nothing had happened that the log keeps, so no file was written.
        var (host, _, _) = TwoInhabitants();
        host.Inhabitants.Reported -= host.LearningLog!.Observe; // a day the log keeps nothing of (they would meet at 00:00)
        host.World.Run(host.World.Calendar.TicksPerDay - host.World.Tick);
        var file = Assert.Single(LearningLogWriter.WriteFinishedDays(host, _folder, ContentDirectory));
        Assert.EndsWith("day-0001.md", file, StringComparison.Ordinal);
        Assert.Contains("(nichts Neues)", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void AProcessedThingIsNamedAfterWhatItWasMadeFrom()
    {
        // D1112 #9: 'reed_v19335574c49d' - a process variant has no name of its own; its root's name, marked as processed.
        var (host, mira, _) = TwoInhabitants();
        Assert.NotNull(host.Processes);
        host.Processes!.Restore(new ProcessState(0, [], [], [new ProcessVariant("reed_v0123456789ab", "reed", ["braidable"])]));
        mira.Beliefs.Set("reed_v0123456789ab", "braidable", true, 0.9, KnowledgeSources.Experienced, null, 5);
        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, 5, mira.Id, "inspect", "reed_v0123456789ab") { Detail = "braidable/" + KnowledgeSources.Experienced });

        host.World.Run(host.World.Calendar.TicksPerDay - host.World.Tick);
        var text = File.ReadAllText(Assert.Single(LearningLogWriter.WriteFinishedDays(host, _folder, ContentDirectory)));
        Assert.Contains("erfährt selbst: Schilf (bearbeitet) ist", text, StringComparison.Ordinal);
        Assert.DoesNotContain("reed_v", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdeaNamesWhatItIsBuiltOn()
    {
        // D1112 #9: 'hat eine Idee' said nothing. The idea's own text is internal English; its roots are names.
        var (host, mira, joro) = TwoInhabitants();
        var idea = mira.Ideas.Take(null, "What if I ask Joro about the tree?", [new IdeaRoot(IdeaRootKinds.Type, "conifer_tree"), new IdeaRoot("inhabitant", joro.Id)], 5, 0.2, 0.1, 5);
        host.Inhabitants.Report(new WorldEvent("idea", 5, mira.Id, null, idea.Id) { Detail = "new" });

        host.World.Run(host.World.Calendar.TicksPerDay - host.World.Tick);
        var text = File.ReadAllText(Assert.Single(LearningLogWriter.WriteFinishedDays(host, _folder, ContentDirectory)));
        Assert.Contains($"hat eine Idee zu: Nadelbaum und {joro.Name}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("What if", text, StringComparison.Ordinal);
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
