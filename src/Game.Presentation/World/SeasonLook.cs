using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// The palette colours a season or a look epoch changes (B2-5 R24, R16), each in one tint slot of the
/// shader: the meshes carry per vertex how much of each such colour is in them (<see cref="TintWeights"/>),
/// and the shader moves that part to the colour shown now, so neither a season nor an epoch change needs a
/// mesh rebuild. The slots are the colours overridden by any season in <c>content/look/palette.json</c> or
/// by any epoch in <c>content/look/epochs.json</c>, in palette order; there are at most <see cref="Max"/>
/// (two vertex attributes of four channels).
/// </summary>
public sealed class SeasonSlots
{
    /// <summary>Largest number of movable colours (the four channels of the vertex attributes CUSTOM0 and CUSTOM1).</summary>
    public const int Max = 8;

    private SeasonSlots(IReadOnlyList<string> ids, IReadOnlyList<Rgb> baseColors)
    {
        Ids = ids;
        BaseColors = baseColors;
    }

    /// <summary>Palette colour id per slot.</summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary>Base colour per slot (the colour the meshes are built with).</summary>
    public IReadOnlyList<Rgb> BaseColors { get; }

    /// <summary>
    /// The slots of a palette and, if given, of the look epochs; more than <see cref="Max"/> movable
    /// colours are a data error, and so is an epoch colour the palette does not have. Meshes and shader
    /// parameters must be made with the same slots (the views share those of the nature look).
    /// </summary>
    public static SeasonSlots From(LookPalette palette, LookEpochs? epochs = null)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var movable = palette.Seasons.SelectMany(s => s.Colors).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in (epochs?.Epochs ?? []).SelectMany(e => e.Colors).Select(c => c.Id))
        {
            if (palette.Colors.All(c => c.Id != id))
            {
                throw new InvalidDataException($"content/look/epochs.json overrides unknown palette colour '{id}'.");
            }

            movable.Add(id);
        }

        var slots = palette.Colors.Where(c => movable.Contains(c.Id)).ToList();
        if (slots.Count > Max)
        {
            throw new InvalidDataException($"The seasons of content/look/palette.json and the epochs of content/look/epochs.json change {slots.Count} colours; the shader has {Max} tint slots.");
        }

        return new SeasonSlots([.. slots.Select(c => c.Id)], [.. slots.Select(c => Rgb.Parse(c.Color))]);
    }

    /// <summary>Slot of a palette colour, or -1 if no season and no epoch changes it.</summary>
    public int IndexOf(string id)
    {
        for (var i = 0; i < Ids.Count; i++)
        {
            if (Ids[i] == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The weights of a palette colour: 1 in its slot, or all 0 if nothing changes it.</summary>
    public TintWeights WeightsOf(string id) => IndexOf(id) is >= 0 and var slot ? TintWeights.Slot(slot) : default;

    /// <summary>A palette colour with its weights, as the meshers blend it.</summary>
    public Rgb Tinted(string id, Rgb color) => color with { Tint = WeightsOf(id) };
}

/// <summary>
/// What a season shows (B2-5 R24): the colour of every tint slot, how much snow lies and how many leaves
/// fall. <see cref="Lerp"/> blends two seasons. Presentation only.
/// </summary>
/// <param name="Colors">Colour per tint slot (<see cref="SeasonSlots"/>).</param>
/// <param name="SnowCover">Share of the ground covered by snow (0..1).</param>
/// <param name="LeafFall">Amount of falling leaves (0..1).</param>
public sealed record SeasonMix(IReadOnlyList<Rgb> Colors, double SnowCover, double LeafFall)
{
    /// <summary>
    /// The look of a season from the palette; null or a season without an entry shows the base colours.
    /// Colours listed in <paramref name="keep"/> stay as they are (an epoch palette of the screenshot tool
    /// wins over the season, as in <see cref="ShotPlan.PaletteFor"/>).
    /// </summary>
    public static SeasonMix Of(LookPalette palette, SeasonSlots slots, string? season, IReadOnlySet<string>? keep = null)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(slots);
        var data = season is null ? null : palette.Seasons.FirstOrDefault(s => s.Season == season);
        var colors = slots.BaseColors.ToArray();
        foreach (var c in data?.Colors ?? [])
        {
            if (slots.IndexOf(c.Id) is >= 0 and var slot && keep?.Contains(c.Id) != true)
            {
                colors[slot] = Rgb.Parse(c.Color);
            }
        }

        return new SeasonMix(colors, data?.SnowCover ?? 0, data?.LeafFall ?? 0);
    }

    /// <summary>Blends towards <paramref name="other"/> (0 = this, 1 = the other).</summary>
    public SeasonMix Lerp(SeasonMix other, double t)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (t <= 0 || t >= 1)
        {
            return t <= 0 ? this : other;
        }

        return new SeasonMix(
            [.. Colors.Select((c, i) => c.Lerp(other.Colors[i], (float)t))],
            SnowCover + ((other.SnowCover - SnowCover) * t),
            LeafFall + ((other.LeafFall - LeafFall) * t));
    }

    /// <summary>
    /// A vertex colour in this season, as the shader shows it: the base colour plus, per slot, the weight
    /// times the way from the base colour of the slot to its colour now.
    /// </summary>
    public Rgb Shade(Rgb vertex, SeasonSlots slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var (r, g, b) = (vertex.R, vertex.G, vertex.B);
        for (var i = 0; i < Colors.Count; i++)
        {
            var w = vertex.Tint[i];
            (r, g, b) = (r + (w * (Colors[i].R - slots.BaseColors[i].R)), g + (w * (Colors[i].G - slots.BaseColors[i].G)), b + (w * (Colors[i].B - slots.BaseColors[i].B)));
        }

        return new Rgb(r, g, b) { Tint = vertex.Tint };
    }

    /// <inheritdoc/>
    public bool Equals(SeasonMix? other) =>
        other is not null && SnowCover.Equals(other.SnowCover) && LeafFall.Equals(other.LeafFall) && Colors.SequenceEqual(other.Colors);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(SnowCover, LeafFall, Colors.Count);
}

/// <summary>Which seasons blend now: <paramref name="T"/> = 0 shows <paramref name="From"/>, 1 <paramref name="To"/>.</summary>
/// <param name="From">Season blended from (equal to <paramref name="To"/> outside a blend; null before the first season).</param>
/// <param name="To">Season of the world clock.</param>
/// <param name="T">Blend weight 0..1 (eased).</param>
public readonly record struct SeasonBlend(string? From, string? To, double T);

/// <summary>
/// The season look follows the world clock (B2-5 R24): when the season of the snapshot changes, the look
/// blends from what is shown to the new season over <c>season_blend_real_seconds</c> of the palette in
/// real time, with a soft start and end. The blend runs in real seconds, so it is as calm at x16 as at x1;
/// a second change during a blend (very fast forward) starts from what is shown, so nothing ever jumps.
/// The first season is shown at once. Presentation only: it reads the season id, never core state.
/// </summary>
public sealed class SeasonBlender
{
    private readonly LookPalette _palette;
    private readonly IReadOnlySet<string>? _keep;
    private SeasonMix _from;
    private SeasonMix _to;
    private string? _fromId;
    private string? _toId;
    private double _elapsed;
    private bool _started;

    /// <summary>Creates the blender; <paramref name="keep"/> as in <see cref="SeasonMix.Of"/>.</summary>
    public SeasonBlender(LookPalette palette, SeasonSlots slots, IReadOnlySet<string>? keep = null)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(slots);
        (_palette, Slots, _keep) = (palette, slots, keep);
        Shown = _from = _to = SeasonMix.Of(palette, slots, null, keep);
    }

    /// <summary>The tint slots the mixes are for.</summary>
    public SeasonSlots Slots { get; }

    /// <summary>What is shown now.</summary>
    public SeasonMix Shown { get; private set; }

    /// <summary>Which seasons blend now.</summary>
    public SeasonBlend Blend => new(_fromId, _toId, Eased);

    private double Duration => Math.Max(_palette.SeasonBlendRealSeconds, 0);

    private double Eased
    {
        get
        {
            var x = Duration <= 0 ? 1 : Math.Clamp(_elapsed / Duration, 0, 1);
            return x * x * (3 - (2 * x));
        }
    }

    /// <summary>Shows a season at once (start of a world, a load, the screenshot tool).</summary>
    public SeasonMix Jump(string? season)
    {
        _started = true;
        (_fromId, _toId, _elapsed) = (season, season, Duration);
        Shown = _from = _to = SeasonMix.Of(_palette, Slots, season, _keep);
        return Shown;
    }

    /// <summary>
    /// One frame: <paramref name="season"/> is the season id of the snapshot, <paramref name="realSeconds"/>
    /// the real time since the last frame (0 while the game is paused). Returns what to show.
    /// </summary>
    public SeasonMix Update(string season, double realSeconds)
    {
        if (!_started)
        {
            return Jump(season);
        }

        if (season != _toId)
        {
            // Blend from what is shown now (also in the middle of a blend), so the look is continuous.
            (_from, _fromId) = (Shown, _toId);
            (_to, _toId) = (SeasonMix.Of(_palette, Slots, season, _keep), season);
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
