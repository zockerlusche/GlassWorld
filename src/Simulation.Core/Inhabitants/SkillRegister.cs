using GlassWorld.Contracts;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>A skill the world knows.</summary>
/// <param name="Id">Skill id (skill vocabulary, or a new id of an invention).</param>
/// <param name="StartLevelMin">Lowest start level (start skills: for start inhabitants; new skills: their start level).</param>
/// <param name="StartLevelMax">Highest start level.</param>
/// <param name="NameDe">German name of a new skill; null for vocabulary skills (vocab.skill.*).</param>
/// <param name="NameEn">English name of a new skill; null for vocabulary skills.</param>
/// <param name="SourceTypeId">The invention that brought it; null for vocabulary skills.</param>
public sealed record SkillInfo(string Id, double StartLevelMin, double StartLevelMax, string? NameDe = null, string? NameEn = null, string? SourceTypeId = null)
{
    /// <summary>Actions a new skill grows with (sorted); empty for vocabulary skills (their actions are in the practice data).</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];
}

/// <summary>Which action practises which skill, and by how much per use.</summary>
/// <param name="Action">Action id.</param>
/// <param name="Skill">Skill id.</param>
/// <param name="Gain">Practice gain per use.</param>
public sealed record SkillPracticeRule(string Action, string Skill, double Gain);

/// <summary>New skills of the world (save games).</summary>
/// <param name="Skills">New skills in the order they came.</param>
public sealed record SkillRegisterState(IReadOnlyList<SkillInfo> Skills);

/// <summary>
/// The skill register (B4b R11, B5 R18, A1372): the skills of the vocabulary with their start range
/// from content/inhabitants/model.json, and new skills an approved invention brings (NewAbility). A new
/// skill is refused when its id is taken, when it would grow with exactly the actions of a known skill,
/// when its start level is outside the data's range, or when the world already has as many new skills
/// as the data allow. Which action practises which skill comes from content/skills/practice.json; a new
/// skill adds its actions with the data's gain. New skills are saved and hashed; without any the register
/// stays out of the world hash. Talent, practice and levels per inhabitant come in B5.
/// </summary>
public sealed class SkillRegister : IWorldSystem, IOptionalWorldState
{
    /// <summary>Event: a new skill of an invention was refused (InhabitantId = inventor, TargetId = invention type, Detail = reason).</summary>
    public const string SkillRefused = "skill_refused";

    /// <summary>Refusal: the id is already a skill.</summary>
    public const string ReasonTaken = "skill_id_taken";

    /// <summary>Refusal: a known skill grows with the same actions.</summary>
    public const string ReasonSameActions = "skill_same_actions";

    /// <summary>Refusal: the start level is outside the data's range.</summary>
    public const string ReasonStartLevel = "skill_start_level";

    /// <summary>Refusal: the world has the data's maximum of new skills.</summary>
    public const string ReasonLimit = "skill_limit";

    /// <summary>Refusal: the new skill names no action to grow with.</summary>
    public const string ReasonNoActions = "skill_no_actions";

    private readonly List<SkillInfo> _start;
    private readonly List<SkillPracticeRule> _startPractice;
    private readonly SkillRules _rules;
    private readonly List<SkillInfo> _added = [];

    /// <summary>Creates the register from the data.</summary>
    public SkillRegister(IEnumerable<SkillModel> skills, SkillRules rules, SkillPractice practice)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(practice);
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _start = [.. skills.Select(s => new SkillInfo(s.Id, s.StartLevelMin, s.StartLevelMax))];
        _startPractice = [.. practice.Practice.Select(p => new SkillPracticeRule(p.Action, p.Skill, p.Gain))];
    }

    /// <inheritdoc/>
    public string Name => "skills";

    /// <inheritdoc/>
    public bool HasState => _added.Count > 0;

    /// <summary>All skills: the vocabulary's first, then new ones in the order they came.</summary>
    public IReadOnlyList<SkillInfo> All => [.. _start, .. _added];

    /// <summary>New skills of inventions.</summary>
    public IReadOnlyList<SkillInfo> Added => _added;

    /// <summary>All practice rules (data, then those of new skills).</summary>
    public IReadOnlyList<SkillPracticeRule> Practice =>
        [.. _startPractice, .. _added.SelectMany(s => ActionsOf(s).Select(a => new SkillPracticeRule(a, s.Id, _rules.RuntimeGain)))];

    /// <summary>The skill with an id, or null.</summary>
    public SkillInfo? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>Skills an action practises.</summary>
    public IReadOnlyList<SkillPracticeRule> PractisedBy(string action) => [.. Practice.Where(p => p.Action == action)];

    /// <summary>Checks a new skill without adding it; null if it may be added, otherwise the reason.</summary>
    public string? Check(NewAbility ability)
    {
        ArgumentNullException.ThrowIfNull(ability);
        var actions = ability.Actions.ToHashSet(StringComparer.Ordinal);
        if (All.Any(s => s.Id == ability.Id))
        {
            return ReasonTaken;
        }

        if (actions.Count == 0)
        {
            return ReasonNoActions;
        }

        if (All.Any(s => ActionsOf(s).SetEquals(actions)))
        {
            return ReasonSameActions;
        }

        if (ability.StartLevel < _rules.RuntimeStartLevelMin || ability.StartLevel > _rules.RuntimeStartLevelMax)
        {
            return ReasonStartLevel;
        }

        return _added.Count >= _rules.MaxRuntimeSkills ? ReasonLimit : null;
    }

    /// <summary>Adds a new skill an approved invention brings; returns null on success, else the reason.</summary>
    public string? Add(NewAbility ability, string sourceTypeId)
    {
        ArgumentNullException.ThrowIfNull(ability);
        ArgumentException.ThrowIfNullOrEmpty(sourceTypeId);
        var reason = Check(ability);
        if (reason is null)
        {
            _added.Add(new SkillInfo(ability.Id, ability.StartLevel, ability.StartLevel, ability.NameDe, ability.NameEn, sourceTypeId) { Actions = [.. ability.Actions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] });
        }

        return reason;
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        // The register changes only through approved inventions.
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_added.Count);
        foreach (var s in _added)
        {
            hasher.Write(s.Id).Write(s.StartLevelMin).Write(s.NameDe ?? string.Empty).Write(s.NameEn ?? string.Empty).Write(s.SourceTypeId ?? string.Empty).Write(s.Actions.Count);
            foreach (var action in s.Actions)
            {
                hasher.Write(action);
            }
        }
    }

    /// <summary>The state to save.</summary>
    public SkillRegisterState Capture() => new([.. _added]);

    /// <summary>
    /// Restores new skills (null = a save game from before the register: none). Each must pass the same
    /// checks as when it was added, otherwise the load fails cleanly (review #96).
    /// </summary>
    public void Restore(SkillRegisterState? state)
    {
        _added.Clear();
        foreach (var skill in state?.Skills ?? [])
        {
            var reason = skill.SourceTypeId is null
                ? "no source invention"
                : Check(new NewAbility { Id = skill.Id, NameDe = skill.NameDe ?? string.Empty, NameEn = skill.NameEn ?? string.Empty, StartLevel = skill.StartLevelMin, Actions = [.. skill.Actions] });
            if (reason is not null)
            {
                _added.Clear();
                throw new InvalidDataException($"Save game skill '{skill.Id}': {reason}.");
            }

            _added.Add(skill);
        }
    }

    private HashSet<string> ActionsOf(SkillInfo skill) =>
        skill.SourceTypeId is null
            ? _startPractice.Where(p => p.Skill == skill.Id).Select(p => p.Action).ToHashSet(StringComparer.Ordinal)
            : skill.Actions.ToHashSet(StringComparer.Ordinal);
}
