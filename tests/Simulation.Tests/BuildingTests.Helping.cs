using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
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
        var decider = new EscalationPort(r => r.Options.FirstOrDefault(o => o.Action == Escalation.HelpAction)?.OptionId ?? "o99");
        var (host, _, typeId) = Stuck(decider);
        var builder = host.Inhabitants.All.Single();
        var helper = Helper(host, builder, new Position(builder.Position.X + 2, builder.Position.Z));

        // The helper knows a piece of the missing material far beyond both their sight.
        var far = host.Inhabitants.SightRadiusM * 3;
        var piece = host.Objects.Spawn(NearbyTypes[^1], new Position(builder.Position.X + far, builder.Position.Z));
        helper.RememberPlace(piece.Id, new PlaceMemory(piece.TypeId, piece.Position, host.World.Tick), 16);
        var events = Record(host);

        Awake(host, helper, () => events.Any(e => e.Kind == InventionSystem.AskedForHelp), (int)(120 * host.World.Calendar.Settings.TicksPerHour));
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
        var decider = new EscalationPort(r => r.Options.FirstOrDefault(o => o.Action == Escalation.HelpAction)?.OptionId ?? "o99");
        var (host, _, _) = Stuck(decider);
        var builder = host.Inhabitants.All.Single();
        var helper = Helper(host, builder, new Position(builder.Position.X + 2, builder.Position.Z));
        var events = Record(host);
        Awake(host, helper, () => host.Inventions.Builds.Single().Helper is not null, (int)(120 * host.World.Calendar.Settings.TicksPerHour));
        (double Builder, double Helper)? atStart = null, atEnd = null;
        (double, double) Fondness() => (builder.Relationships.Get(helper.Id, new Relation(0, 0, 0)).Affection, helper.Relationships.Get(builder.Id, new Relation(0, 0, 0)).Affection);
        host.Inhabitants.Reported += e =>
        {
            if (e.InhabitantId == helper.Id && e.Action == Build.StepAction)
            {
                if (e.Kind == InhabitantsSystem.ActionStarted)
                {
                    atStart = Fondness();
                }
                else if (e.Kind == InhabitantsSystem.ActionFinished)
                {
                    atEnd = Fondness();
                }
            }
        };

        // The missing material lies by the helper, out of the builder's sight: only the helper finds it, fetches it and
        // works it in.
        helper.Position = new Position(builder.Position.X + (host.Inhabitants.SightRadiusM * 2), builder.Position.Z);
        host.Objects.Spawn(NearbyTypes[^1], new Position(helper.Position.X + 1, helper.Position.Z));
        Awake(host, helper, () => host.Inventions.Builds.Single().BuiltObjectId is not null, (int)(48 * host.World.Calendar.Settings.TicksPerHour));
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionFinished && e.Action == Build.StepAction && e.InhabitantId == helper.Id);
        var step = Escalation.HelpAffectionPerStep!.Value;
        Assert.NotNull(atStart);
        Assert.NotNull(atEnd);
        Assert.True(atEnd.Value.Builder >= atStart.Value.Builder + (step / 2), $"builder {atStart.Value.Builder} -> {atEnd.Value.Builder}");
        Assert.True(atEnd.Value.Helper >= atStart.Value.Helper + (step / 2), $"helper {atStart.Value.Helper} -> {atEnd.Value.Helper}");
    }

    [Fact]
    public void HelpEndsAfterTheDataHours()
    {
        // Review GW3 #312: after help_hours the helper no longer fetches material for the building.
        var decider = new EscalationPort(r => r.Options.FirstOrDefault(o => o.Action == Escalation.HelpAction)?.OptionId ?? "o99");
        var (host, _, _) = Stuck(decider);
        var builder = host.Inhabitants.All.Single();
        var helper = Helper(host, builder, new Position(builder.Position.X + 2, builder.Position.Z));
        Awake(host, helper, () => host.Inventions.Builds.Single().Helper is not null, (int)(120 * host.World.Calendar.Settings.TicksPerHour));

        // Past the hours, material right by the helper (out of the builder's sight) stays where it is.
        host.World.Run((long)(Escalation.HelpHours!.Value * host.World.Calendar.Settings.TicksPerHour) + 1);
        helper.Position = new Position(builder.Position.X + (host.Inhabitants.SightRadiusM * 2), builder.Position.Z);
        var piece = host.Objects.Spawn(NearbyTypes[^1], new Position(helper.Position.X + 1, helper.Position.Z));
        var events = Record(host);
        for (var i = 0; i < 6 * host.World.Calendar.Settings.TicksPerHour; i++)
        {
            helper.Needs["sleep"] = 1.0;
            host.World.Step();
        }

        Assert.DoesNotContain(events, e => e.InhabitantId == helper.Id && e.Action == Build.StepAction && e.Kind == InhabitantsSystem.ActionStarted);
        Assert.True(host.Objects.TryGet(piece.Id, out _));
    }

    private static BuildSettings BuildData => ContractSerializer.Deserialize<BuildSettings>(File.ReadAllText(Path.Combine(EagerContent.Value, "build", "settings.json")));

    private static BuildSettings Build => BuildData;

    /// <summary>
    /// Runs until <paramref name="done"/>, keeping the helper rested (a helper asleep is rightly not asked, and the
    /// questions of a stuck building come every few hours, also at night).
    /// </summary>
    private static void Awake(SimulationHost host, Inhabitant helper, Func<bool> done, int maxTicks, Func<string>? diag = null)
    {
        for (var i = 0; i < maxTicks && !done(); i++)
        {
            helper.Needs["sleep"] = 1.0;
            host.World.Step();
        }

        Assert.True(done(), diag?.Invoke() ?? "condition not reached");
    }

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
