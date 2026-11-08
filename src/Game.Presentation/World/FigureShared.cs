using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// What all figures of a scene share (B2-4): one clip chooser, one buffer for the core values, the look
/// and library data, the tick times and the set of things already reported, so a tick allocates nothing
/// per figure and every missing clip or gait is logged once per run. Not thread-safe (main thread only).
/// </summary>
public sealed class FigureShared
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedClips = new(StringComparer.Ordinal);

    /// <summary>Creates the shared state from the data and the world's calendar at a time factor.</summary>
    public FigureShared(AnimationLibrary library, LookFigures figures, WorldTimeSettings time, double speed)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(figures);
        Library = library;
        Figures = figures;
        Chooser = new AnimationChooser(library);
        GameSecondsPerTick = FigureMotion.GameSecondsPerTick(time);
        RealSecondsPerTick = FigureMotion.RealSecondsPerTick(time, speed);
    }

    /// <summary>The animation library.</summary>
    public AnimationLibrary Library { get; }

    /// <summary>The figure look data.</summary>
    public LookFigures Figures { get; }

    /// <summary>The one chooser of the scene.</summary>
    public AnimationChooser Chooser { get; }

    /// <summary>Reused buffer for the core values of the figure being updated.</summary>
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Game seconds per tick.</summary>
    public double GameSecondsPerTick { get; }

    /// <summary>Real seconds per tick at the scene's time factor.</summary>
    public double RealSecondsPerTick { get; }

    /// <summary>True the first time <paramref name="key"/> is reported in this run, false after.</summary>
    public bool FirstReport(string key) => _reported.Add(key);

    /// <summary>True the first time the fallback of clip <paramref name="clipId"/> is reported in this run (no string building per tick).</summary>
    public bool FirstClipReport(string clipId) => _reportedClips.Add(clipId);
}
