using GlassWorld.Contracts;
using GlassWorld.Simulation.Random;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>
/// B9-1a (R1, D1049 v2): inhabitants age. Each one has a tick of birth (state, saved and hashed); its age in life years and
/// its life phase are a pure function of that tick, the clock and the data (<c>content/life/aging.json</c>), cached on the
/// inhabitant and checked once a game day - a change of phase is reported (<c>stage_changed</c>). An inhabitant without a
/// tick of birth (the start inhabitants) gets one drawn from the data's start ages, from its own stream (no world stream
/// is created). Which actions a phase allows is data. The code names no phase.
/// </summary>
public sealed class AgingSystem : IWorldSystem
{
    /// <summary>Event kind: the inhabitant's life phase changed (detail = the new phase).</summary>
    public const string StageChanged = "stage_changed";

    private const string All = "*";
    private readonly LifeAging _data;
    private readonly InhabitantsSystem _inhabitants;
    private readonly ulong _seed;
    private readonly long _ticksPerDay;
    private readonly List<LifePhase> _phases;

    /// <summary>Creates the system for a world with <paramref name="seed"/>.</summary>
    public AgingSystem(LifeAging data, InhabitantsSystem inhabitants, ulong seed, long ticksPerDay)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        _seed = seed;
        _ticksPerDay = Math.Max(1, ticksPerDay);
        _phases = [.. data.Phases.OrderBy(p => p.FromYears)];
    }

    /// <inheritdoc/>
    public string Name => "aging";

    /// <summary>Game ticks per life year.</summary>
    public double TicksPerYear => _data.DaysPerLifeYear * _ticksPerDay;

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var day = world.Tick % _ticksPerDay == 0;
        foreach (var inhabitant in _inhabitants.Ordered)
        {
            if (inhabitant.BornTick is null)
            {
                inhabitant.BornTick = world.Tick - (long)Math.Round(StartYears(inhabitant.Id) * TicksPerYear);
                Refresh(inhabitant, world.Tick);
            }
            else if (day || inhabitant.LifeStage is null)
            {
                var before = inhabitant.LifeStage;
                Refresh(inhabitant, world.Tick);
                if (before is not null && inhabitant.LifeStage != before)
                {
                    _inhabitants.Report(new WorldEvent(StageChanged, world.Tick, inhabitant.Id, null, null) { Detail = inhabitant.LifeStage });
                }
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        // The ticks of birth are hashed with the inhabitants; age and phase follow from them.
    }

    /// <summary>Whether the inhabitant's life phase allows an action (data: the phase's actions, '*' = all); true without a phase.</summary>
    public bool Allows(Inhabitant inhabitant, string action)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return inhabitant.LifeStage is not { } stage
            || _phases.FirstOrDefault(p => p.Id == stage) is not { } phase
            || phase.Actions.Contains(All, StringComparer.Ordinal)
            || phase.Actions.Contains(action, StringComparer.Ordinal);
    }

    /// <summary>The age in whole life years at <paramref name="tick"/> of someone born at <paramref name="bornTick"/>.</summary>
    public int AgeAt(long bornTick, long tick) => (int)Math.Max(0, Math.Floor((tick - bornTick) / TicksPerYear));

    private void Refresh(Inhabitant inhabitant, long tick)
    {
        var age = AgeAt(inhabitant.BornTick!.Value, tick);
        inhabitant.AgeYears = age;
        inhabitant.LifeStage = _phases.LastOrDefault(p => p.FromYears <= age)?.Id ?? _phases[0].Id;
    }

    private double StartYears(string inhabitantId)
    {
        // A private generator per inhabitant: the same world seed always gives the same start age.
        var random = RandomStreams.Create(_seed, $"aging/{inhabitantId}");
        var range = _data.StartAgeYears;
        return range.Min + ((range.Max - range.Min) * random.NextDouble());
    }
}
