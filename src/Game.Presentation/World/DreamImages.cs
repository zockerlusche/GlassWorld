using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>A dream image on screen now.</summary>
/// <param name="Key">Stable key of the image (job and whether it is the symbol or the picture).</param>
/// <param name="JobId">The night job it belongs to.</param>
/// <param name="ObjectType">The object type whose shape shows (the symbol), or null for a picture without one.</param>
/// <param name="ImageFile">File name of the picture the job made, or null for the symbol.</param>
/// <param name="X">Centre on screen, 0..1 from the left.</param>
/// <param name="Y">Centre on screen, 0..1 from the top.</param>
/// <param name="Alpha">Opacity now, 0..brightness.</param>
public sealed record DreamImage(string Key, string JobId, string? ObjectType, string? ImageFile, double X, double Y, double Alpha);

/// <summary>
/// The dream images of the night scene (B2579, D637 R11/R13) without Godot. They come only from the service's
/// night list (<c>GET /night/items</c>): a job about an object type shows that type's shape as soon as it is
/// seen, a job that made a picture shows the picture once it is done - which is also the progress. What a kind
/// shows is data. Images appear one after the other, softly: at most <c>max_at_once</c>, at least
/// <c>min_gap_s</c> apart, each fading in, standing and fading out. Their places come from a hash of the job
/// (presentation only, never the core's random streams). An empty list shows nothing. A new service session
/// starts the list over. <see cref="Shown"/> is the record for the acceptance (every image belongs to a job).
/// </summary>
public sealed class DreamImages(NightDreams data)
{
    private readonly NightDreams _data = data ?? throw new ArgumentNullException(nameof(data));
    private readonly Queue<(string Key, string JobId, string? Type, string? File)> _waiting = new();
    private readonly List<(string Key, string JobId, string? Type, string? File, double X, double Y, double Age)> _active = [];
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);
    private readonly List<string> _shown = [];
    private string? _session;
    private double _sinceStart = double.PositiveInfinity;

    /// <summary>The number to ask the service with next (<c>since</c>).</summary>
    public long Since { get; private set; }

    /// <summary>Images waiting their turn.</summary>
    public int Waiting => _waiting.Count;

    /// <summary>The job ids of every image shown so far this night, in order (acceptance A6).</summary>
    public IReadOnlyList<string> Shown => _shown;

    /// <summary>Takes a page of the night list. Returns true if a full page came, i.e. more may wait at the service.</summary>
    public bool Take(NightItems items, int pageSize = 512)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (_session is not null && _session != items.Session)
        {
            // The service started anew: its numbers begin again, and so does the list.
            Clear();
        }

        _session = items.Session;
        foreach (var item in items.Items)
        {
            Since = Math.Max(Since, item.Seq);
            var show = _data.Appearances.FirstOrDefault(a => a.Kind == item.Kind)?.Show ?? NightAppearanceShow.None;
            if (show == NightAppearanceShow.None)
            {
                continue;
            }

            if (item.ObjectType is { } type && _taken.Add(item.JobId + "|symbol"))
            {
                _waiting.Enqueue((item.JobId + "|symbol", item.JobId, type, null));
            }

            if (show == NightAppearanceShow.Image && item.State == JobState.Done && item.Image is { } file && _taken.Add(item.JobId + "|image"))
            {
                _waiting.Enqueue((item.JobId + "|image", item.JobId, item.ObjectType, file));
            }
        }

        return items.Items.Count >= pageSize;
    }

    /// <summary>The night is over (or no scene shows): nothing waits, nothing stands.</summary>
    public void Clear()
    {
        _waiting.Clear();
        _active.Clear();
        _taken.Clear();
        _shown.Clear();
        _session = null;
        Since = 0;
        _sinceStart = double.PositiveInfinity;
    }

    /// <summary>One frame: ages the images, lets the next one appear when there is room and the gap has passed.</summary>
    public IReadOnlyList<DreamImage> Update(double realSeconds, bool reduceMotion)
    {
        var dt = Math.Max(0, realSeconds);
        var slow = reduceMotion ? Math.Max(1, _data.ReduceMotionFactor) : 1;
        var (fadeIn, hold, fadeOut) = (_data.FadeInS * slow, _data.HoldS, _data.FadeOutS * slow);
        _sinceStart += dt;
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var a = _active[i];
            a.Age += dt;
            if (a.Age >= fadeIn + hold + fadeOut)
            {
                _active.RemoveAt(i);
            }
            else
            {
                _active[i] = a;
            }
        }

        if (_waiting.Count > 0 && _active.Count < _data.MaxAtOnce && _sinceStart >= _data.MinGapS)
        {
            var next = _waiting.Dequeue();
            var (x, y) = Place(next.Key);
            _active.Add((next.Key, next.JobId, next.Type, next.File, x, y, 0));
            _shown.Add(next.JobId);
            _sinceStart = 0;
        }

        return [.. _active.Select(a => new DreamImage(a.Key, a.JobId, a.File is null ? a.Type : null, a.File, a.X, a.Y, _data.Brightness * Level(a.Age, fadeIn, hold, fadeOut)))];
    }

    /// <summary>Opacity share 0..1 of an image of the given age: a soft rise, a stand, a soft fall.</summary>
    public static double Level(double age, double fadeIn, double hold, double fadeOut)
    {
        static double Smooth(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - (2 * t));
        return age < fadeIn ? Smooth(age / fadeIn)
            : age < fadeIn + hold ? 1
            : Smooth(1 - ((age - fadeIn - hold) / fadeOut));
    }

    /// <summary>Where an image stands: spread over the screen by a hash of its key, clear of the edges and of the words in the middle.</summary>
    public static (double X, double Y) Place(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ulong h = 14695981039346656037UL;
        foreach (var c in key)
        {
            h = (h ^ c) * 1099511628211UL;
        }

        var x = 0.12 + (0.76 * ((h & 0xFFFF) / 65535.0));
        var y = 0.18 + (0.62 * (((h >> 16) & 0xFFFF) / 65535.0));

        // The middle band carries "Die Welt träumt …": images keep above or below it.
        if (y is > 0.42 and < 0.58 && x is > 0.3 and < 0.7)
        {
            y = y < 0.5 ? 0.32 : 0.68;
        }

        return (x, y);
    }
}
