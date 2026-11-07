using GlassWorld.Contracts.Validation;

namespace GlassWorld.Contracts.Tests;

public sealed class SchemaBundleTests
{
    private const string Common = """{ "$defs": { "Id": { "type": "string" } } }""";

    [Fact]
    public void KeywordNextToRefIsRejected()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["common.schema.json"] = Common,
            ["x.schema.json"] = """{ "type": "object", "properties": { "id": { "$ref": "common.schema.json#/$defs/Id", "maxLength": 3 } } }""",
        };

        var ex = Assert.Throws<InvalidDataException>(() => SchemaBundle.Bundle(sources, forValidation: true));
        Assert.Contains("maxLength", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnnotationNextToRefIsAllowed()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["common.schema.json"] = Common,
            ["x.schema.json"] = """{ "type": "object", "properties": { "id": { "$ref": "common.schema.json#/$defs/Id", "description": "d" } } }""",
        };

        Assert.Single(SchemaBundle.Bundle(sources, forValidation: true));
    }

    [Fact]
    public void LocalDefinitionMustNotShadowCommon()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["common.schema.json"] = Common,
            ["x.schema.json"] = """{ "type": "object", "$defs": { "Id": { "type": "integer" } } }""",
        };

        Assert.Throws<InvalidDataException>(() => SchemaBundle.Bundle(sources, forValidation: true));
    }

    [Fact]
    public void ConstBecomesEnumOnlyForValidation()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["common.schema.json"] = Common,
            ["x.schema.json"] = """{ "type": "object", "properties": { "v": { "type": "string", "const": "0.1" } } }""",
        };

        Assert.Contains("\"enum\":[\"0.1\"]", SchemaBundle.Bundle(sources, forValidation: true)[0].Value, StringComparison.Ordinal);
        Assert.Contains("\"const\":\"0.1\"", SchemaBundle.Bundle(sources, forValidation: false)[0].Value, StringComparison.Ordinal);
    }
}
