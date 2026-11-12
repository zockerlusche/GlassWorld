using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Player;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>A1602 (D850, A1373): a builder who looked long enough by itself knocks at the glass.</summary>
public sealed partial class BuildingTests
{
    [Fact]
    public void AStuckBuilderKnocksWithTheMissingPropertiesAndMayHopeForAGift()
    {
        // Nothing like the missing material anywhere on the island (B2502): the knock says a gift may help. It is
        // about the invention, the detail names properties - never a type name - and it is saved and hashed.
        var (host, port, typeId) = Invented(23, decisions: new EscalationPort(_ => null));
        var settings = ContractSerializer.Deserialize<BuildSettings>(File.ReadAllText(Path.Combine(EagerContent.Value, "build", "settings.json")));
        var perHour = host.World.Calendar.Settings.TicksPerHour;
        host.Objects.Remove(host.Objects.All.Single(o => o.TypeId == NearbyTypes[^1]).Id);
        var events = Record(host);
        AnswerPlan(port, Plan());
        RunUntil(host, () => events.Any(e => e.Kind == InventionSystem.NeedsHelpEvent), 3000 + (int)(settings.HelpAfterHours!.Value * perHour * 3));

        var knock = Assert.Single(host.Mailbox.Open, m => m.Subject is not null);
        var help = Assert.Single(events, e => e.Kind == InventionSystem.NeedsHelpEvent);
        Assert.Equal((MailboxSystem.KnockKind, MailboxSystem.BuildMissingText, typeId, help.Detail), (knock.Kind, knock.TextKey, knock.Subject, knock.Detail));
        Assert.Equal(host.Inhabitants.All.Single().Id, knock.InhabitantId);
        Assert.DoesNotContain(NearbyTypes[^1], knock.Detail!, StringComparison.Ordinal);
        Assert.Contains(events, e => e.Kind == MailboxSystem.MailArrived && e.Detail == MailboxSystem.KnockKind);

        // Saved and loaded with the knock open: the same hash, the same message.
        var path = Path.Combine(_directory, "knock.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(path, host);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(typeId, Assert.Single(loaded.Mailbox.Open, m => m.Subject is not null).Subject);

        // Once the builder is no longer stuck (here: it gave up), the knock calms down and never repeats.
        RunUntil(host, () => host.Inventions.Builds.Single().GaveUp, (int)(settings.GiveUpAfterHours!.Value * perHour * 3));
        host.World.Run(perHour);
        var calm = Assert.Single(host.Mailbox.Open, m => m.Subject is not null);
        Assert.True(calm.Settled);
        Assert.Equal(0, calm.Urgency);
    }

    [Fact]
    public void AStuckBuilderWhoseMaterialLiesElsewhereIsEncouragedToLookFurther()
    {
        // B2502: the material is on the island, out of sight - the knock encourages looking on, no gift.
        var (host, port, _) = Invented(23);
        host.Objects.Remove(host.Objects.All.Single(o => o.TypeId == NearbyTypes[^1]).Id);
        host.Objects.Spawn(NearbyTypes[^1], new Position(5000, 5000));
        var events = Record(host);
        AnswerPlan(port, Plan());
        RunUntil(host, () => events.Any(e => e.Kind == InventionSystem.NeedsHelpEvent), 3000 + (int)(24 * host.World.Calendar.Settings.TicksPerHour));
        Assert.Equal(MailboxSystem.BuildStuckText, Assert.Single(host.Mailbox.Open, m => m.Subject is not null).TextKey);
    }
}
