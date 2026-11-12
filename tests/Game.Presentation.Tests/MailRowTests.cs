using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

public sealed class MailRowTests
{
    [Fact]
    public void ABuildersKnockNamesPropertiesAndANeedKnockDoesNot()
    {
        // A1602: a stuck builder's detail is properties (comma-separated) - each becomes its property word; a knock
        // about a need keeps its need detail and has no property words.
        var builder = new MailRow("m-000001", "inh-1", 1, "knock", "msg.mail.build_missing", "building_material,burnable", 0) { AboutBuilding = true };
        Assert.Equal(["vocab.property.building_material", "vocab.property.burnable"], builder.PropertyKeys);
        var need = new MailRow("m-000002", "inh-1", 1, "knock", "msg.mail.knock", "hunger", 0);
        Assert.Empty(need.PropertyKeys);
    }
}
