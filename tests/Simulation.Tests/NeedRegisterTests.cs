using GlassWorld.Contracts;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Simulation.Tests;

/// <summary>The register of needs inventions bring (A1372, B4b R11; TL B2658: bound to the invention's type).</summary>
public sealed class NeedRegisterTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));
    private static readonly NeedRules Rules = Content.InhabitantModel.NeedRules!;

    [Fact]
    public void ANewNeedIsBoundToItsInventionsType()
    {
        var register = New();
        Assert.False(register.HasState);
        Assert.Null(register.Add(Need("tidiness", 0.2, "use", "combine"), "rt-000001"));
        var added = Assert.Single(register.Added);
        Assert.Equal(("tidiness", "Ordnung", "rt-000001", 0.2), (added.Id, added.NameDe, added.SourceTypeId, added.DecayPerDay));
        Assert.Equal(["combine", "use"], added.Actions);
        Assert.True(register.HasState);
        Assert.Equal(Rules.FillPerUse, register.FillPerUse);
    }

    [Fact]
    public void TakenIdsOddDecaysNoActionsAndTooManyNewNeedsAreRefused()
    {
        var register = New();
        Assert.Equal(NeedRegister.ReasonTaken, register.Add(Need("hunger", 0.2, "use"), "rt-000001"));
        Assert.Equal(NeedRegister.ReasonNoActions, register.Add(Need("tidiness", 0.2), "rt-000001"));
        Assert.Equal(NeedRegister.ReasonDecay, register.Add(Need("tidiness", Rules.RuntimeDecayPerDayMax + 0.1, "use"), "rt-000001"));
        Assert.Equal(NeedRegister.ReasonDecay, register.Add(Need("tidiness", Rules.RuntimeDecayPerDayMin / 2, "use"), "rt-000001"));
        for (var i = 0; i < Rules.MaxRuntimeNeeds; i++)
        {
            Assert.Null(register.Add(Need("need_" + (char)('a' + i), 0.2, "use"), "rt-00001" + i));
        }

        Assert.Equal(NeedRegister.ReasonTaken, register.Add(Need("need_a", 0.2, "use"), "rt-000099"));
        Assert.Equal(NeedRegister.ReasonLimit, register.Add(Need("one_more", 0.2, "use"), "rt-000099"));
        Assert.Equal(Rules.MaxRuntimeNeeds, register.Added.Count);

        // Without the data inventions bring no needs at all.
        Assert.Equal(NeedRegister.ReasonLimit, new NeedRegister(Content.InhabitantModel.Needs, null, 24).Add(Need("tidiness", 0.2, "use"), "rt-000001"));
    }

    [Fact]
    public void NewWorldsHaveNoNewNeedsAndKeepTheirHash()
    {
        var host = SimulationHost.CreatePlaceholderWorld(Path.Combine(TestData.Root, "content"), 1, new MockDecisionPort(), "de");
        Assert.Empty(host.Needs.Added);
        var before = host.World.StateHash();
        Assert.Null(host.Needs.Add(Need("tidiness", 0.2, "use"), "rt-000001"));
        Assert.NotEqual(before, host.World.StateHash());
    }

    [Fact]
    public void ASaveGameWithNeedsTheDataCannotCarryFailsCleanly()
    {
        var register = New();
        Assert.Null(register.Add(Need("tidiness", 0.2, "use"), "rt-000001"));
        var good = register.Capture();
        var copy = New();
        copy.Restore(good);
        Assert.Equal(good.Needs.Single().Id, copy.Added.Single().Id);
        Assert.Throws<InvalidDataException>(() => copy.Restore(new NeedRegisterState([good.Needs[0] with { Id = "hunger" }])));
        Assert.Throws<InvalidDataException>(() => copy.Restore(new NeedRegisterState([good.Needs[0], good.Needs[0]])));
        Assert.Throws<InvalidDataException>(() => copy.Restore(new NeedRegisterState([good.Needs[0] with { SourceTypeId = string.Empty }])));
        Assert.Throws<InvalidDataException>(() => copy.Restore(new NeedRegisterState([good.Needs[0] with { DecayPerDay = 2 }])));
        Assert.Empty(copy.Added);
        copy.Restore(null);
        Assert.False(copy.HasState);
    }

    private static NeedRegister New() => new(Content.InhabitantModel.Needs, Rules, 24 * 60);

    private static NewNeed Need(string id, double decay, params string[] actions) =>
        new() { Id = id, NameDe = id == "tidiness" ? "Ordnung" : id, NameEn = id, DecayPerDay = decay, Actions = actions };
}
