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
    private readonly WorldTimeSettings _time;

    /// <summary>Creates the shared state from the data and the world's calendar at a time factor.</summary>
    public FigureShared(AnimationLibrary library, LookFigures figures, WorldTimeSettings time, double speed)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(figures);
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        Library = library;
        Figures = figures;
        Chooser = new AnimationChooser(library);
        Gate = new PoseGate(figures.PoseCadence.StaggerS);
        GameSecondsPerTick = FigureMotion.GameSecondsPerTick(time);
        RealSecondsPerTick = FigureMotion.RealSecondsPerTick(time, speed);
    }

    /// <summary>The animation library.</summary>
    public AnimationLibrary Library { get; }

    /// <summary>The figure look data.</summary>
    public LookFigures Figures { get; }

    /// <summary>The one chooser of the scene.</summary>
    public AnimationChooser Chooser { get; }

    /// <summary>Whether the figures get the ink outline of the quality level (<see cref="QualityPlan.HullOutline"/>; the figure look data can still switch it off).</summary>
    public bool HullOutline { get; init; } = true;

    /// <summary>The scene's pose gate: one figure changes its pose per frame (W1697).</summary>
    public PoseGate Gate { get; }

    /// <summary>Reused buffer for the core values of the figure being updated.</summary>
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Game seconds per tick.</summary>
    public double GameSecondsPerTick { get; }

    /// <summary>Real seconds per tick at the scene's time factor.</summary>
    public double RealSecondsPerTick { get; private set; }

    /// <summary>
    /// The player changed the time factor (pause, normal, fast forward in the main game): the next ticks
    /// are walked over their new real time, so figures keep up instead of trailing behind.
    /// </summary>
    public void UseSpeed(double speed) => RealSecondsPerTick = FigureMotion.RealSecondsPerTick(_time, speed);

    /// <summary>True the first time <paramref name="key"/> is reported in this run, false after.</summary>
    public bool FirstReport(string key) => _reported.Add(key);

    /// <summary>True the first time the fallback of clip <paramref name="clipId"/> is reported in this run (no string building per tick).</summary>
    public bool FirstClipReport(string clipId) => _reportedClips.Add(clipId);
}
