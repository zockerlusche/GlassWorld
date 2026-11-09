using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// The calm thought dots ("…") above the head of an inhabitant with an open question (F5282, presentation
/// only): three dots side by side whose opacity rises and falls in a slow wave that runs over them once per
/// period (data: content/look/figures.json thinking_dots). Each inhabitant has its own phase from its id, so
/// the dots of figures standing together do not pulse in lock-step. Godot-free; the figure view draws it.
/// </summary>
public static class ThinkingDots
{
    /// <summary>Number of dots.</summary>
    public const int Count = 3;

    /// <summary>The dot glyph.</summary>
    public const string Glyph = "•";

    private const ulong SaltPhase = 5;

    /// <summary>Whether the dots show: while the core reports an open question of the inhabitant.</summary>
    public static bool Shown(int openQuestions) => openQuestions > 0;

    /// <summary>The inhabitant's phase of the wave in [0, 1), from its id.</summary>
    public static double Phase(string inhabitantId) => AnimationChooser.Unit(inhabitantId, 0, SaltPhase);

    /// <summary>
    /// Opacity of dot <paramref name="dot"/> (0 = left) after <paramref name="seconds"/> real seconds: a
    /// cosine wave between alpha_min and alpha_max, each dot a third of a period behind the one before.
    /// </summary>
    public static double Alpha(FigureThinkingDots look, int dot, double seconds, double phase)
    {
        ArgumentNullException.ThrowIfNull(look);
        ArgumentOutOfRangeException.ThrowIfNegative(dot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(dot, Count);
        var t = (seconds / look.PeriodS) + phase - ((double)dot / Count);
        var wave = 0.5 - (0.5 * Math.Cos(2 * Math.PI * t));
        return look.AlphaMin + ((look.AlphaMax - look.AlphaMin) * wave);
    }

    /// <summary>Horizontal screen offset of a dot in pixels, centred on the head.</summary>
    public static double OffsetPx(FigureThinkingDots look, int dot)
    {
        ArgumentNullException.ThrowIfNull(look);
        return (dot - ((Count - 1) / 2.0)) * look.SpacingPx;
    }
}
