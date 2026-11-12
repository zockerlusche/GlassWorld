using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1557 (D809 M2): an inhabitant gets a name of its sex or one that fits anybody - a woman is not called by a
/// name only for men. Without lists by sex the names are given as before (W1698).
/// </summary>
public sealed class NamesTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));

    private static InhabitantsSystem Named(int count, IReadOnlyDictionary<string, IReadOnlyList<string>> bySex, ulong seed = 3)
    {
        var world = new World(seed, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
            Names = Content.InhabitantNames,
            NamesBySex = bySex,
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [.. Enumerable.Range(0, count).Select(i => new Position(i * 20, 0))], count);
        return inhabitants;
    }

    [Fact]
    public void TheDataHaveNamesForBothSexesAndForAnybody()
    {
        Assert.NotEmpty(Content.InhabitantNamesBySex["female"]);
        Assert.NotEmpty(Content.InhabitantNamesBySex["male"]);
        Assert.Contains(Content.InhabitantNames, n => !Content.InhabitantNamesBySex.Values.Any(list => list.Contains(n)));
        Assert.Equal(Content.InhabitantNames.Count, Content.InhabitantNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryInhabitantGetsANameThatFitsItsSex()
    {
        var bySex = Content.InhabitantNamesBySex;
        var inhabitants = Named(12, bySex);

        Assert.Contains(inhabitants.All, i => i.Sex == "female");
        Assert.Contains(inhabitants.All, i => i.Sex == "male");
        foreach (var inhabitant in inhabitants.All)
        {
            var other = inhabitant.Sex == "female" ? "male" : "female";
            Assert.NotNull(inhabitant.Name);
            Assert.DoesNotContain(inhabitant.Name, bySex[other]);
        }

        // Distinct while the fitting names last.
        Assert.Equal(12, inhabitants.All.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheSameSeedGivesTheSameNames()
    {
        var a = Named(8, Content.InhabitantNamesBySex, seed: 7).All.Select(i => i.Name).ToList();
        var b = Named(8, Content.InhabitantNamesBySex, seed: 7).All.Select(i => i.Name).ToList();

        Assert.Equal(a, b);
    }

    [Fact]
    public void WithoutListsBySexEveryNameFitsAnybody()
    {
        var inhabitants = Named(6, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

        Assert.All(inhabitants.All, i => Assert.Contains(i.Name, Content.InhabitantNames));
        Assert.Equal(6, inhabitants.All.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count());
    }
}
