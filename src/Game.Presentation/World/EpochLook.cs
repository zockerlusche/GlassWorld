using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// What a look epoch shows (B2-5 R16, B2 R19): per tint slot (<see cref="SeasonSlots"/>) how far the epoch
/// owns the colour (<see cref="Hold"/>) and its colour times that share (<see cref="Colors"/>), and how
/// far each of its details is there. An epoch changes palette colours and details only, never models or
/// shaders. The epoch palette is the base the season works on: <see cref="Over"/> lays it under the
/// season's colours, and where both name a colour the epoch's wins, as in <see cref="ShotPlan.PaletteFor"/>.
/// Shares instead of plain colours, so two epochs blend linearly whatever the season is. Presentation only.
/// </summary>
/// <param name="Colors">Per tint slot the epoch's colour times its share (black where the epoch leaves the colour alone).</param>
/// <param name="Hold">Per tint slot the share of the epoch's colour (0 = the season's colour, 1 = the epoch's).</param>
/// <param name="Details">The details of the epoch with their weight 0..1, ordered by id.</param>
public sealed record EpochMix(IReadOnlyList<Rgb> Colors, IReadOnlyList<double> Hold, IReadOnlyList<EpochDetail> Details)
{
    /// <summary>
    /// The look of an epoch from <c>content/look/epochs.json</c>; null shows the base palette. An unknown
    /// epoch and a colour without a tint slot (slots made without the epochs) are data errors.
    /// </summary>
    public static EpochMix Of(LookEpochs epochs, SeasonSlots slots, string? epoch)
    {
        ArgumentNullException.ThrowIfNull(epochs);
        ArgumentNullException.ThrowIfNull(slots);
        var colors = new Rgb[slots.Ids.Count];
        var hold = new double[slots.Ids.Count];
        if (epoch is null)
        {
            return new EpochMix(colors, hold, []);
        }

        var data = epochs.Epochs.FirstOrDefault(e => e.Id == epoch) ?? throw new InvalidDataException($"Unknown look epoch '{epoch}' (content/look/epochs.json).");
        foreach (var c in data.Colors)
        {
            var slot = slots.IndexOf(c.Id);
            if (slot < 0)
            {
                throw new InvalidDataException($"Epoch colour '{c.Id}' has no tint slot.");
            }

            (colors[slot], hold[slot]) = (Rgb.Parse(c.Color), 1);
        }

        return new EpochMix(colors, hold, [.. data.Details.Order(StringComparer.Ordinal).Select(id => new EpochDetail(id, 1))]);
    }

    /// <summary>Weight of a detail now (0 = not there, 1 = fully there).</summary>
    public double Detail(string id) => Details.FirstOrDefault(d => d.Id == id)?.Weight ?? 0;

    /// <summary>Blends towards <paramref name="other"/> (0 = this, 1 = the other).</summary>
    public EpochMix Lerp(EpochMix other, double t)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (t <= 0 || t >= 1)
        {
            return t <= 0 ? this : other;
        }

        var ids = Details.Select(d => d.Id).Union(other.Details.Select(d => d.Id), StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return new EpochMix(
            [.. Colors.Select((c, i) => c.Lerp(other.Colors[i], (float)t))],
            [.. Hold.Select((h, i) => h + ((other.Hold[i] - h) * t))],
            [.. ids.Select(id => new EpochDetail(id, Detail(id) + ((other.Detail(id) - Detail(id)) * t)))]);
    }

    /// <summary>
    /// The season on this epoch's palette: per slot the season's colour, moved to the epoch's by the
    /// epoch's share. An epoch that changes no colour gives back the very same season mix.
    /// </summary>
    public SeasonMix Over(SeasonMix season)
    {
        ArgumentNullException.ThrowIfNull(season);
        if (Hold.All(h => h <= 0))
        {
            return season;
        }

        return season with
        {
            Colors = [.. season.Colors.Select((c, i) =>
            {
                var keep = (float)(1 - Math.Clamp(Hold[i], 0, 1));
                return Hold[i] <= 0 ? c : new Rgb((c.R * keep) + Colors[i].R, (c.G * keep) + Colors[i].G, (c.B * keep) + Colors[i].B);
            })],
        };
    }

    /// <inheritdoc/>
    public bool Equals(EpochMix? other) =>
        other is not null && Colors.SequenceEqual(other.Colors) && Hold.SequenceEqual(other.Hold) && Details.SequenceEqual(other.Details);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Colors.Count, Details.Count);
}

/// <summary>A detail of a look epoch (id from <c>content/look/epochs.json</c>) and how far it is there.</summary>
/// <param name="Id">Detail id.</param>
/// <param name="Weight">0 = not there, 1 = fully there.</param>
public sealed record EpochDetail(string Id, double Weight);

/// <summary>Which epochs blend now: <paramref name="T"/> = 0 shows <paramref name="From"/>, 1 <paramref name="To"/>.</summary>
/// <param name="From">Epoch blended from (equal to <paramref name="To"/> outside a blend).</param>
/// <param name="To">Epoch of the snapshot.</param>
/// <param name="T">Blend weight 0..1 (eased).</param>
public readonly record struct EpochBlend(string? From, string? To, double T);

/// <summary>
/// The look epoch follows the snapshot (B2-5 R16): when <c>WorldSnapshot.LookEpoch</c> changes, palette
/// and details blend from what is shown to the new epoch over <c>blend_real_seconds</c> of
/// <c>content/look/epochs.json</c> in real time, with a soft start and end, so the change is calm at every
/// game speed. A second change during a blend starts from what is shown, so nothing ever jumps. The first
/// epoch (start of a world, a load) is shown at once. Presentation only: it reads the epoch id, never core
/// state.
/// </summary>
public sealed class EpochBlender
{
    private readonly LookEpochs _epochs;
    private readonly SeasonSlots _slots;
    private EpochMix _from;
    private EpochMix _to;
    private string? _fromId;
    private string? _toId;
    private double _elapsed;
    private bool _started;

    /// <summary>Creates the blender; <paramref name="slots"/> must be made with the same epochs.</summary>
    public EpochBlender(LookEpochs epochs, SeasonSlots slots)
    {
        ArgumentNullException.ThrowIfNull(epochs);
        ArgumentNullException.ThrowIfNull(slots);
        (_epochs, _slots) = (epochs, slots);
        Shown = _from = _to = EpochMix.Of(epochs, slots, null);
    }

    /// <summary>What is shown now.</summary>
    public EpochMix Shown { get; private set; }

    /// <summary>Which epochs blend now.</summary>
    public EpochBlend Blend => new(_fromId, _toId, Eased);

    private double Duration => Math.Max(_epochs.BlendRealSeconds, 0);

    private double Eased
    {
        get
        {
            var x = Duration <= 0 ? 1 : Math.Clamp(_elapsed / Duration, 0, 1);
            return x * x * (3 - (2 * x));
        }
    }

    /// <summary>Shows an epoch at once (start of a world, a load, the screenshot tool); null = the base palette.</summary>
    public EpochMix Jump(string? epoch)
    {
        _started = true;
        (_fromId, _toId, _elapsed) = (epoch, epoch, Duration);
        Shown = _from = _to = EpochMix.Of(_epochs, _slots, epoch);
        return Shown;
    }

    /// <summary>
    /// One frame: <paramref name="epoch"/> is the look epoch of the snapshot (null = none),
    /// <paramref name="realSeconds"/> the real time since the last frame (0 while the game is paused).
    /// Returns what to show; outside a blend that is the same mix every frame.
    /// </summary>
    public EpochMix Update(string? epoch, double realSeconds)
    {
        if (!_started)
        {
            return Jump(epoch);
        }

        if (epoch != _toId)
        {
            // Blend from what is shown now (also in the middle of a blend), so the look is continuous.
            (_from, _fromId) = (Shown, _toId);
            (_to, _toId) = (EpochMix.Of(_epochs, _slots, epoch), epoch);
            _elapsed = 0;
        }

        _elapsed = Math.Min(_elapsed + Math.Max(realSeconds, 0), Duration);
        if (_elapsed >= Duration)
        {
            (_from, _fromId) = (_to, _toId);
        }

        Shown = _from.Lerp(_to, Eased);
        return Shown;
    }
}
