using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Writing;

/// <summary>
/// An inhabitant's level (0..1) in a skill. The per-inhabitant skill levels (B5 part, GW3 after A1357)
/// implement it; until then <see cref="NoSkills"/> says nobody can do anything (R14: nobody starts with
/// reading or writing).
/// </summary>
public interface ISkillLevels
{
    /// <summary>The level of <paramref name="inhabitantId"/> in <paramref name="skillId"/>, 0 if none.</summary>
    double Level(string inhabitantId, string skillId);
}

/// <summary>Nobody has any skill (until per-inhabitant levels exist).</summary>
public sealed class NoSkills : ISkillLevels
{
    /// <summary>The shared instance.</summary>
    public static readonly NoSkills Instance = new();

    /// <inheritdoc/>
    public double Level(string inhabitantId, string skillId) => 0;
}

/// <summary>
/// What an inhabitant holds (type ids). GW1's hold/carry slot (B2540) implements it; until then
/// <see cref="NothingHeld"/>: nobody holds a tool, so only carriers without tool properties work.
/// </summary>
public interface IHeldItems
{
    /// <summary>Type ids of what <paramref name="inhabitantId"/> holds now.</summary>
    IReadOnlyList<string> HeldTypes(string inhabitantId);
}

/// <summary>Nobody holds anything (until the hold slot exists).</summary>
public sealed class NothingHeld : IHeldItems
{
    /// <summary>The shared instance.</summary>
    public static readonly NothingHeld Instance = new();

    /// <inheritdoc/>
    public IReadOnlyList<string> HeldTypes(string inhabitantId) => [];
}

/// <summary>
/// The checks behind the actions write and read (B8 R13-R15, spec 33 §9a). Pure: they change nothing,
/// they only say why something cannot happen (a reason id) or null if it can. The actions themselves come
/// with the skill levels and the knowledge store (A1400); they call these checks first.
/// </summary>
public sealed partial class WritingSystem
{
    /// <summary>Reason: the carrier id is not in the data.</summary>
    public const string UnknownMedium = "writing_unknown_medium";

    /// <summary>Reason: the writer's level in the writing skill is below the data's minimum.</summary>
    public const string NoWritingSkill = "writing_no_skill";

    /// <summary>Reason: the object or ground cannot carry this kind of writing.</summary>
    public const string WrongCarrier = "writing_wrong_carrier";

    /// <summary>Reason: the writer holds no tool with all the carrier's tool properties.</summary>
    public const string NoTool = "writing_no_tool";

    /// <summary>Reason: the object already carries a text.</summary>
    public const string CarrierTaken = "writing_carrier_taken";

    /// <summary>Reason: the reader's level in the reading skill is below the data's minimum.</summary>
    public const string NoReadingSkill = "reading_no_skill";

    /// <summary>Reason: the text is too worn to read.</summary>
    public const string Illegible = "reading_illegible";

    /// <summary>Reason: not curious enough to read in passing.</summary>
    public const string NotCurious = "reading_not_curious";

    /// <summary>Why <paramref name="writer"/> cannot write on <paramref name="target"/> with this carrier, or null.</summary>
    public string? CanWriteOn(Inhabitant writer, WorldObject target, string mediumId, ISkillLevels skills, IHeldItems held)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_media.TryGetValue(mediumId, out var medium))
        {
            return UnknownMedium;
        }

        var carrier = medium.Carrier;
        if (carrier.Surface != CarrierSurface.Object || !carrier.Properties.All(p => _objects.Catalog.HasProperty(target.TypeId, p)))
        {
            return WrongCarrier;
        }

        return _texts.ContainsKey(target.Id) ? CarrierTaken : WriterProblem(writer, medium, skills, held);
    }

    /// <summary>Why <paramref name="writer"/> cannot make marks on the ground at <paramref name="spot"/>, or null.</summary>
    public string? CanWriteOnGround(Inhabitant writer, Position spot, string mediumId, WorldMapSystem? map, ISkillLevels skills, IHeldItems held)
    {
        if (!_media.TryGetValue(mediumId, out var medium))
        {
            return UnknownMedium;
        }

        var carrier = medium.Carrier;
        var biome = BiomeAt(map, spot);
        if (carrier.Surface != CarrierSurface.Ground || biome is null || !carrier.Biomes.Contains(biome, StringComparer.Ordinal))
        {
            return WrongCarrier;
        }

        return WriterProblem(writer, medium, skills, held);
    }

    /// <summary>
    /// How readable new writing on this carrier is: the carrier's best legibility times the writer's level
    /// (schema writing-media).
    /// </summary>
    public double LegibilityFor(string mediumId, double writerLevel) =>
        _media.TryGetValue(mediumId, out var medium) ? medium.Legibility * Math.Clamp(writerLevel, 0, 1) : 0;

    /// <summary>Why <paramref name="reader"/> cannot read <paramref name="text"/>, or null.</summary>
    public string? CanRead(Inhabitant reader, WrittenText text, ISkillLevels skills)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(skills);
        if (!Readable(text))
        {
            return Illegible;
        }

        return skills.Level(reader.Id, _rules.Read.Ability) < _rules.Read.MinLevel ? NoReadingSkill : null;
    }

    /// <summary>Why <paramref name="reader"/> would not read <paramref name="text"/> in passing, or null.</summary>
    public string? WouldReadInPassing(Inhabitant reader, WrittenText text, ISkillLevels skills)
    {
        if (CanRead(reader, text, skills) is { } reason)
        {
            return reason;
        }

        return reader.Traits.GetValueOrDefault(_rules.Read.CuriosityTrait, 0) < _rules.Read.CuriosityMin ? NotCurious : null;
    }

    private string? WriterProblem(Inhabitant writer, WritingMedium medium, ISkillLevels skills, IHeldItems held)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(held);
        if (skills.Level(writer.Id, _rules.Write.Ability) < _rules.Write.MinLevel)
        {
            return NoWritingSkill;
        }

        if (medium.ToolProperties.Count == 0)
        {
            return null;
        }

        var tools = held.HeldTypes(writer.Id);
        return tools.Any(t => medium.ToolProperties.All(p => _objects.Catalog.HasProperty(t, p))) ? null : NoTool;
    }

    private static string? BiomeAt(WorldMapSystem? map, Position spot)
    {
        if (map?.CellOf(spot) is not { } cell)
        {
            return null;
        }

        var index = map.Map.Cell(cell.X, cell.Z).Biome;
        return index < map.Map.BiomeIds.Count ? map.Map.BiomeIds[index] : null;
    }
}
