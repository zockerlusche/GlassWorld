using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>Who is up in a crown (D1105 H3b): the thing climbed, how high, and until when at most.</summary>
/// <param name="Id">The inhabitant.</param>
/// <param name="CarrierId">The thing it climbed (a tree).</param>
/// <param name="HeightCm">How high it got, in centimetres.</param>
/// <param name="UntilTick">The tick it climbs down at the latest.</param>
public sealed record UpInhabitant(string Id, string CarrierId, int HeightCm, long UntilTick);

/// <summary>
/// D1105 H3b (A1724): climbing what gives grip. The core offers it only where it can mean something (a thing with a
/// hold, or towards a thing up high under one), checks the grip at the start (too smooth: out_of_reach, too_smooth)
/// and computes the height from the data; while the climber stays within the crown its reach grows by that height.
/// Above the reach height it may slip - gently, with a fright, never hurt (B2472). No way up is ever named.
/// </summary>
public sealed partial class BodySystem
{
    private readonly SortedDictionary<string, UpInhabitant> _up = new(StringComparer.Ordinal);
    private readonly List<(string Inhabitant, string Target)> _climbed = [];

    /// <summary>How high a thing hangs above the ground in centimetres (the map); null = everything on the ground.</summary>
    public Func<string, int>? AboveCm { get; set; }

    /// <summary>The plant a thing up high grows on (the map's spawn data); null = none.</summary>
    public Func<WorldObject, WorldObject?>? PlantOf { get; set; }

    /// <summary>How high an inhabitant reaches standing (the inhabitant model's reach), in centimetres.</summary>
    public int ReachCm { get; set; }

    /// <summary>Event of an inhabitant sliding down while climbing.</summary>
    public const string Slipped = "slipped";

    /// <summary>How high an inhabitant is up in a crown, in centimetres (0 = on the ground) - for the view.</summary>
    public int ClimbedCm(string inhabitantId) => _up.TryGetValue(inhabitantId, out var up) ? up.HeightCm : 0;

    /// <summary>
    /// What one climbs when aiming the climb at a thing: the thing itself when it has a hold and a height, else - for a
    /// thing up high - the plant it grows on when that has a hold and a height; null = nothing to climb there.
    /// </summary>
    public WorldObject? CarrierOf(WorldObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_data.Climbing is null)
        {
            return null;
        }

        if (Climbable(target.TypeId))
        {
            return target;
        }

        return AboveCm?.Invoke(target.Id) is > 0 && PlantOf?.Invoke(target) is { } plant && Climbable(plant.TypeId) ? plant : null;
    }

    /// <summary>Why a climb aimed at a thing cannot start (too_smooth, or too_high when there is nothing to climb); null = it can.</summary>
    public string? ClimbRefusal(Inhabitant inhabitant, string action, WorldObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_data.Climbing is not { } climbing || !string.Equals(action, climbing.Action, StringComparison.Ordinal))
        {
            return null;
        }

        if (CarrierOf(target) is not { } carrier)
        {
            return InhabitantsSystem.TooHigh;
        }

        return (_content.Params(carrier.TypeId).GripPermille ?? 0) < climbing.MinGripPermille ? InhabitantsSystem.TooSmooth : null;
    }

    /// <summary>How much higher an inhabitant reaches a thing because it is up in what it climbed - that thing or what grows on it (0 = not).</summary>
    public int UpCm(Inhabitant inhabitant, WorldObject target)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(target);
        if (!_up.TryGetValue(inhabitant.Id, out var up))
        {
            return 0;
        }

        return string.Equals(target.Id, up.CarrierId, StringComparison.Ordinal) || CarrierOf(target)?.Id == up.CarrierId ? up.HeightCm : 0;
    }

    /// <summary>The inhabitants up in a crown for a save game.</summary>
    public IReadOnlyList<UpInhabitant> CaptureUp() => [.. _up.Values];

    /// <summary>Restores who is up in a crown from a save game.</summary>
    public void RestoreUp(IReadOnlyList<UpInhabitant>? up)
    {
        _up.Clear();
        _climbed.Clear();
        foreach (var entry in up ?? [])
        {
            if (entry.HeightCm > 0)
            {
                _up[entry.Id] = entry;
            }
        }
    }

    private bool Climbable(string typeId) =>
        _content.Contains(typeId) && _content.Params(typeId) is { GripPermille: not null, ShadeHeightCm: > 0 };

    private static double Distance(Position a, Position b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    /// <summary>The height a hold of this grip lets one reach (linear between the data's points), at most the thing's height.</summary>
    private int HeightFor(Climbing climbing, string carrierTypeId)
    {
        var p = _content.Params(carrierTypeId);
        var grip = p.GripPermille ?? 0;
        var points = climbing.HeightByGrip.OrderBy(h => h.GripPermille).ToList();
        int height;
        if (grip <= points[0].GripPermille)
        {
            height = points[0].HeightCm;
        }
        else if (grip >= points[^1].GripPermille)
        {
            height = points[^1].HeightCm;
        }
        else
        {
            var i = points.FindIndex(h => h.GripPermille >= grip);
            var (a, b) = (points[i - 1], points[i]);
            height = a.HeightCm + (int)((long)(b.HeightCm - a.HeightCm) * (grip - a.GripPermille) / (b.GripPermille - a.GripPermille));
        }

        return Math.Min(height, p.ShadeHeightCm ?? 0);
    }

    /// <summary>
    /// Once per step: a finished climb takes the climber up (or it slips, gently); who left the crown or stayed up long
    /// enough is down again.
    /// </summary>
    private void Climb(World world)
    {
        if (_data.Climbing is not { } climbing)
        {
            return;
        }

        foreach (var (id, targetId) in _climbed)
        {
            if (!_inhabitants.TryGet(id, out var inhabitant) || !_objects.TryGet(targetId, out var target) || CarrierOf(target) is not { } carrier)
            {
                continue;
            }

            var height = HeightFor(climbing, carrier.TypeId);
            var over = Math.Max(0, height - ReachCm);
            var chance = (long)climbing.SlipPermillePerM * over / 100;
            if (chance > 0 && world.Random.Get("body/" + id).NextInt(0, 1000) < chance)
            {
                // Gently down: a fright, no harm (B2472); the event is the inhabitant's to remember.
                if (climbing.FrightMood is { } mood && climbing.FrightDelta is { } delta && inhabitant.Moods.TryGetValue(mood, out var now))
                {
                    inhabitant.Moods[mood] = Math.Clamp(now + delta, 0, 1);
                }

                _up.Remove(id);
                _inhabitants.Report(new WorldEvent(Slipped, world.Tick, id, climbing.Action, carrier.Id) { TargetType = carrier.TypeId });
                continue;
            }

            _up[id] = new UpInhabitant(id, carrier.Id, height, world.Tick + ((long)climbing.UpMinutes * _ticksPerHour / 60));
        }

        _climbed.Clear();
        foreach (var up in _up.Values.ToList())
        {
            if (world.Tick >= up.UntilTick || !_inhabitants.TryGet(up.Id, out var inhabitant) || !_objects.TryGet(up.CarrierId, out var carrier)
                || Distance(carrier.Position, inhabitant.Position) > climbing.StayWithinM)
            {
                _up.Remove(up.Id);
            }
        }
    }

    private void OnReported(WorldEvent e)
    {
        if (e.Kind == InhabitantsSystem.ActionFinished && e.TargetId is { } target && _data.Climbing is { } climbing
            && string.Equals(e.Action, climbing.Action, StringComparison.Ordinal))
        {
            _climbed.Add((e.InhabitantId, target));
        }
    }
}
