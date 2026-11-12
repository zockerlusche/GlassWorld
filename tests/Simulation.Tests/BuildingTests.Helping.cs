using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1376 (B2663 (3), D827 R19): a stuck builder may ask another for help - the helper shows where it knows the missing
/// material and builds along; with every material it works in the two grow fonder of each other.
/// </summary>
public sealed partial class BuildingTests
{
    [Fact]
    public void AskedForHelpTheHelperShowsWhereItKnowsTheMaterialAndTheBuildingGoesOn()
    {
        var decider = new EscalationPort(r => r.Options.FirstOrDefault(o => o.Action == Escalation.HelpAction)?.OptionId);
        var (host, _, typeId) = Stuck(decider);
        var builder = host.Inhabitants.All.Single();
        var helper = Helper(host, builder, new Position(builder.Position.X + 2, builder.Position.Z));

        // The helper knows a piece of the missing material far beyond both their sight.
        var far = host.Inhabitants.SightRadiusM * 3;
        var piece = host.Objects.Spawn(NearbyTypes[^1], new Position(builder.Position.X + far, builder.Position.Z));
        helper.RememberPlace(piece.Id, new PlaceMemory(piece.TypeId, piece.Position, host.World.Tick), 16);
        var events = Record(host);

        RunUntil(host, () => events.Any(e => e.Kind == InventionSystem.AskedForHelp), (int)(120 * host.World.Calendar.Settings.TicksPerHour));
        var asked = Assert.Single(events, e => e.Kind == InventionSystem.AskedForHelp);
        Assert.Equal((builder.Id, helper.Id), (asked.InhabitantId, asked.TargetId));
        Assert.Contains(events, e => e.Kind == InventionSystem.MaterialShown && e.TargetId == piece.Id);
        Assert.True(builder.Places.ContainsKey(piece.Id));
        Assert.Equal(helper.Id, host.Inventions.Builds.Single().Helper);

        // Now the material is known, the building goes on and is finished.
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, (int)(48 * host.World.Calendar.Settings.TicksPerHour));
        Assert.False(host.Objects.TryGet(piece.Id, out _));
        Assert.Equal(typeId, host.Objects.All.Single(o => o.Id == host.Inventions.Builds.Single().BuiltObjectId).TypeId);
    }

    [Fact]
    public void TheHelperBuildsAlongAndBothGrowFonder()
    {
        var decider = new EscalationPort(r => r.Options.FirstOrDefault(o => o.Action == Escalation.HelpAction)?.OptionId);
        var (host, _, _) = Stuck(decider);
        var builder = host.Inhabitants.All.Single();
        var helper = Helper(host, builder, new Position(builder.Position.X + 2, builder.Position.Z));
        var events = Record(host);
        RunUntil(host, () => host.Inventions.Builds.Single().Helper is not null, (int)(120 * host.World.Calendar.Settings.TicksPerHour));
        var before = (builder.Relationships.Get(helper.Id, new Relation(0, 0, 0)).Affection, helper.Relationships.Get(builder.Id, new Relation(0, 0, 0)).Affection);

        // The builder is busy for long; the missing material lies right by the helper: the helper fetches and works it in.
        builder.Planned = new Activity(Content.Value.InhabitantModel.Togetherness!.SleepAction, null, builder.Position, 100_000);
        host.Objects.Spawn(NearbyTypes[^1], new Position(helper.Position.X + 1, helper.Position.Z));
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, (int)(48 * host.World.Calendar.Settings.TicksPerHour));
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionFinished && e.Action == Build.Settings.StepAction && e.InhabitantId == helper.Id);
        var step = Escalation.HelpAffectionPerStep!.Value;
        Assert.True(builder.Relationships.Get(helper.Id, new Relation(0, 0, 0)).Affection >= before.Item1 + step - 1e-9);
        Assert.True(helper.Relationships.Get(builder.Id, new Relation(0, 0, 0)).Affection >= before.Item2 + step - 1e-9);
    }

    private static BuildSettings BuildData => ContractSerializer.Deserialize<BuildSettings>(File.ReadAllText(Path.Combine(EagerContent.Value, "build", "settings.json")));

    private static BuildSettings Build => BuildData;

    /// <summary>A second inhabitant the builder knows by name, standing at <paramref name="at"/>, every need met.</summary>
    private static Inhabitant Helper(SimulationHost host, Inhabitant builder, Position at)
    {
        var model = Content.Value.InhabitantModel;
        var helper = new Inhabitant(
            "inh-000002",
            "male",
            1,
            at,
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
        host.Inhabitants.Add(helper);
        builder.KnownNames.Add(helper.Id);
        return helper;
    }
}
