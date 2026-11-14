using GlassWorld.Contracts;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Random;
using GlassWorld.Simulation.State;
using GlassWorld.Simulation.Writing;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>Practised gain of one inhabitant in one skill (saved).</summary>
/// <param name="InhabitantId">The inhabitant.</param>
/// <param name="SkillId">The skill (register id).</param>
/// <param name="Gain">Level added by practice on top of the start level.</param>
/// <param name="PractisedTick">Tick of the last practice (forgetting starts a game day after it).</param>
/// <param name="Peak">Highest gain ever reached (D1017 G2; 0 without retention data).</param>
/// <param name="Days">Game days on which it was practised (D1017 G2; 0 without retention data).</param>
public sealed record SkillGain(string InhabitantId, string SkillId, double Gain, long PractisedTick = 0, double Peak = 0, int Days = 0);

/// <summary>
/// Skill levels per inhabitant (B5 part, TL #19589; B4b R11, B5 R18, B8 R14). Every inhabitant has, per
/// skill of the register, a start level (from the register's range) and a talent (skills/levels.json):
/// both are drawn once from the world seed, inhabitant and skill (no random stream is created, so worlds
/// keep their hash until someone practises). When an action finishes, every practice rule of that action
/// (skills/practice.json) adds gain x talent, up to the data's maximum; gains not practised for a whole
/// game day fade by forget_per_day, never below the start level. Only the practised gains are state (saved, hashed when
/// there are any). The code knows no skill by name.
/// </summary>
public sealed class SkillLevelsSystem : IWorldSystem, IOptionalWorldState, ISkillLevels
{
    private readonly SkillLevelSettings _settings;
    private readonly SkillRegister _register;
    private readonly ulong _seed;
    private readonly long _ticksPerDay;
    private readonly SortedDictionary<(string Inhabitant, string Skill), Practised> _gains = new(KeyOrder.Instance);
    private readonly Curve? _floorShare;

    /// <summary>Creates the system for a world with <paramref name="worldSeed"/>.</summary>
    public SkillLevelsSystem(SkillLevelSettings settings, SkillRegister register, ulong worldSeed, long ticksPerDay)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _register = register ?? throw new ArgumentNullException(nameof(register));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerDay);
        _seed = worldSeed;
        _ticksPerDay = ticksPerDay;
        _floorShare = settings.Retention is { } retention ? new Curve(retention.FloorShareByDays) : null;
    }

    /// <inheritdoc/>
    public string Name => "skill_levels";

    /// <inheritdoc/>
    public bool HasState => _gains.Count > 0;

    /// <summary>The start level of an inhabitant in a skill (0 for skills the register does not know).</summary>
    public double StartLevel(string inhabitantId, string skillId) => Drawn(inhabitantId, skillId).Start;

    /// <summary>The talent of an inhabitant in a skill (multiplies practice gain).</summary>
    public double Talent(string inhabitantId, string skillId) => Drawn(inhabitantId, skillId).Talent;

    /// <inheritdoc/>
    public double Level(string inhabitantId, string skillId)
    {
        var (start, _) = Drawn(inhabitantId, skillId);
        return Math.Min(_settings.MaxLevel, start + _gains.GetValueOrDefault((inhabitantId, skillId)).Gain);
    }

    /// <summary>
    /// Practice: called for every reported event; a finished action adds gain x talent for each of its
    /// practice rules (wired in the host like the chronicle). It changes state inside the event handler:
    /// that is safe because it reads nothing another subscriber writes, and the core reports events in a
    /// fixed order within the tick, so the result does not depend on the order of subscribers.
    /// </summary>
    public void Observe(WorldEvent worldEvent)
    {
        ArgumentNullException.ThrowIfNull(worldEvent);
        if (worldEvent.Kind != InhabitantsSystem.ActionFinished || worldEvent.Action is not { } action)
        {
            return;
        }

        foreach (var rule in _register.PractisedBy(action))
        {
            var key = (worldEvent.InhabitantId, rule.Skill);
            var (start, talent) = Drawn(key.InhabitantId, key.Skill);
            var room = Math.Max(0, _settings.MaxLevel - start);
            var was = _gains.GetValueOrDefault(key);

            // D1017 G2: below what it once could, practice counts more (relearning is faster) - only for the part of
            // the step that brings it back up to its peak; beyond the peak it counts as usual (review GW2 #407).
            var step = rule.Gain * talent;
            var gap = Math.Max(0, was.Peak - was.Gain);
            var factor = _settings.Retention?.RelearnFactor ?? 1.0;
            var added = gap <= 0 || factor <= 1 ? step
                : step * factor <= gap ? step * factor
                : gap + (step - (gap / factor));
            var gain = Math.Min(room, was.Gain + added);
            if (gain > 0)
            {
                var newDay = was.Days == 0 || (worldEvent.Tick / _ticksPerDay) != (was.PractisedTick / _ticksPerDay);
                _gains[key] = _settings.Retention is null
                    ? new Practised(gain, worldEvent.Tick, 0, 0)
                    : new Practised(gain, worldEvent.Tick, Math.Max(was.Peak, gain), was.Days + (newDay ? 1 : 0));
            }
        }
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_gains.Count == 0 || _settings.ForgetPerDay <= 0 || world.Tick % _ticksPerDay != 0)
        {
            return;
        }

        // Once a game day: what was not practised for a whole day fades a little (never below the start
        // level); a skill practised within the last day keeps its level (review GW1 #123).
        foreach (var key in _gains.Keys.ToList())
        {
            var was = _gains[key];
            if (world.Tick - was.PractisedTick < _ticksPerDay)
            {
                continue;
            }

            // D1017 G2: what was practised long keeps a floor - a share of its peak by the days practised.
            var floor = _floorShare is { } share ? was.Peak * share.At(was.Days) : 0;
            var gain = Math.Max(floor, was.Gain - _settings.ForgetPerDay);
            if (gain <= 0)
            {
                _gains.Remove(key);
            }
            else
            {
                _gains[key] = was with { Gain = gain };
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_gains.Count);
        foreach (var ((inhabitant, skill), p) in _gains)
        {
            hasher.Write(inhabitant).Write(skill).Write(p.Gain).Write(p.PractisedTick);

            // Peak and days only with retention data: worlds without it hash as before.
            if (_settings.Retention is not null)
            {
                hasher.Write(p.Peak).Write(p.Days);
            }
        }
    }

    /// <summary>The state to save (practised gains only).</summary>
    public IReadOnlyList<SkillGain> Capture() => [.. _gains.Select(g => new SkillGain(g.Key.Inhabitant, g.Key.Skill, g.Value.Gain, g.Value.PractisedTick, g.Value.Peak, g.Value.Days))];

    /// <summary>Restores saved gains (null = a save game from before: none).</summary>
    public void Restore(IEnumerable<SkillGain>? gains)
    {
        _gains.Clear();
        foreach (var gain in gains ?? [])
        {
            if (gain.Gain <= 0 || gain.Gain > _settings.MaxLevel || _register.Find(gain.SkillId) is null)
            {
                _gains.Clear();
                throw new InvalidDataException($"Save game skill levels: gain {gain.Gain} for unknown skill or out of range ('{gain.SkillId}').");
            }

            // A save from before retention: the peak is at least the gain, the days unknown (0).
            _gains[(gain.InhabitantId, gain.SkillId)] = _settings.Retention is null
                ? new Practised(gain.Gain, gain.PractisedTick, 0, 0)
                : new Practised(gain.Gain, gain.PractisedTick, Math.Max(gain.Peak, gain.Gain), gain.Days);
        }
    }

    private (double Start, double Talent) Drawn(string inhabitantId, string skillId)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        ArgumentNullException.ThrowIfNull(skillId);
        if (_register.Find(skillId) is not { } skill)
        {
            return (0, 0);
        }

        // A private generator per inhabitant and skill: the same world seed always gives the same values,
        // and no stream enters the world's random state.
        var random = RandomStreams.Create(_seed, $"skill_level/{inhabitantId}/{skillId}");
        var start = skill.StartLevelMin + ((skill.StartLevelMax - skill.StartLevelMin) * random.NextDouble());
        var talent = _settings.TalentMin + ((_settings.TalentMax - _settings.TalentMin) * random.NextDouble());
        return (Math.Min(start, _settings.MaxLevel), talent);
    }

    /// <summary>Practised gain, last practice, peak gain and days practised of one inhabitant in one skill.</summary>
    private readonly record struct Practised(double Gain, long PractisedTick, double Peak, int Days);

    private sealed class KeyOrder : IComparer<(string Inhabitant, string Skill)>
    {
        public static readonly KeyOrder Instance = new();

        public int Compare((string Inhabitant, string Skill) x, (string Inhabitant, string Skill) y)
        {
            var first = string.CompareOrdinal(x.Inhabitant, y.Inhabitant);
            return first != 0 ? first : string.CompareOrdinal(x.Skill, y.Skill);
        }
    }
}
