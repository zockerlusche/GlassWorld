namespace GlassWorld.Presentation.World;

/// <summary>
/// Text keys of vocabulary and catalog ids (KR4): the game shows names through these, so a key is
/// built in one place and the text check sees the prefixes.
/// </summary>
public static class VocabKeys
{
    /// <summary>Name of a need.</summary>
    public static string Need(string id) => "vocab.need." + id;

    /// <summary>Name of an object type (catalog id).</summary>
    public static string ObjectType(string id) => "vocab.object." + id;

    /// <summary>Name of an action.</summary>
    public static string Action(string id) => "vocab.action." + id;

    /// <summary>Name of a property (the dream texts, A1618).</summary>
    public static string Property(string id) => "vocab.property." + id;

    /// <summary>Name of a weather or weather condition (the weather symbol's tooltip, A1682).</summary>
    public static string Weather(string id) => "vocab.weather." + id;
}
