using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>Where the compute night's picture stands.</summary>
public enum NightStage
{
    /// <summary>The world as usual.</summary>
    Day,

    /// <summary>The picture darkens into the dream scene.</summary>
    FadingOut,

    /// <summary>The dream scene while the AI service computes.</summary>
    Dream,

    /// <summary>The morning fades in.</summary>
    FadingIn,

    /// <summary>The service is done but the world's night goes on: the world is shown again until the next night.</summary>
    Awake,
}

/// <summary>
/// The compute night's picture (B7, B2579/B2580, D637 R14) without Godot: when the world's day phase turns to
/// night, the picture darkens into the dream scene; once the AI service is done (nothing queued or running, or its
/// budget spent) and the scene stood its minimum time, the morning fades in. A world that starts or is loaded in
/// the night shows no scene for that night (nothing was seen beginning). Real seconds only, so a pause (0 seconds)
/// holds every fade; fast forward does not hurry it. Presentation only: it changes nothing in the world.
/// </summary>
public sealed class NightScene(LookNight data)
{
    private readonly LookNight _data = data ?? throw new ArgumentNullException(nameof(data));
    private bool? _wasNight;
    private double _inStage;

    /// <summary>The stage now.</summary>
    public NightStage Stage { get; private set; } = NightStage.Day;

    /// <summary>How dark the picture is now, 0..<c>darkness</c>.</summary>
    public double Darkness { get; private set; }

    /// <summary>Whether the dream scene (title, progress) shows: while darkening, dreaming and fading in.</summary>
    public bool SceneShown => Stage is NightStage.FadingOut or NightStage.Dream or NightStage.FadingIn;

    /// <summary>
    /// One frame. <paramref name="known"/>: the core's compute night runs (null: not decided yet for a new or
    /// loaded world - neither a start nor an end; the first known value is never taken as a change).
    /// <paramref name="serviceDone"/>: the service has nothing left this night (null: unknown - no service or no
    /// answer yet; then the scene ends after its minimum time). <paramref name="realSeconds"/>: real time since
    /// the last frame, 0 while paused.
    /// </summary>
    public void Update(bool? known, bool? serviceDone, double realSeconds)
    {
        var dt = Math.Max(0, realSeconds);
        var night = known == true;
        var began = _wasNight == false && night;
        if (known is not null)
        {
            _wasNight = known;
        }

        if (!night && Stage is NightStage.Awake)
        {
            Stage = NightStage.Day;
        }

        if (began && Stage is NightStage.Day)
        {
            Enter(NightStage.FadingOut);
        }

        _inStage += dt;
        switch (Stage)
        {
            case NightStage.FadingOut:
                Darkness = _data.Darkness * Math.Clamp(_inStage / _data.FadeOutS, 0, 1);
                if (_inStage >= _data.FadeOutS)
                {
                    Enter(NightStage.Dream);
                }

                break;
            case NightStage.Dream:
                Darkness = _data.Darkness;
                if (!night || (_inStage >= _data.MinSceneS && serviceDone != false))
                {
                    Enter(NightStage.FadingIn);
                }

                break;
            case NightStage.FadingIn:
                Darkness = _data.Darkness * (1 - Math.Clamp(_inStage / _data.FadeInS, 0, 1));
                if (_inStage >= _data.FadeInS)
                {
                    Darkness = 0;
                    Enter(night ? NightStage.Awake : NightStage.Day);
                }

                break;
            default:
                Darkness = 0;
                break;
        }
    }

    /// <summary>
    /// Whether the service reports nothing left of its night: it is in the night phase and has nothing queued or
    /// running, or its night budget is spent. A status still in the day phase (the first poll right after nightfall,
    /// before the service switched) is never "done" (review GW1 #209).
    /// </summary>
    public static bool Done(PhaseStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Phase == PhaseStatusPhase.Night && ((status.Queued + status.Running == 0) || status.BudgetLeftS <= 0);
    }

    /// <summary>
    /// What the scene is told about the service (A1586, B7 R16): null without a service, or once the last
    /// <c>lost_after_polls</c> questions in a row went unanswered (the service is gone or not ready - the scene then
    /// ends after its minimum time instead of waiting for the morning); else whether its night is done, false while
    /// no answer came yet.
    /// </summary>
    public static bool? ServiceDone(LookNight data, bool hasService, PhaseStatus? status, int unansweredPolls)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!hasService || (data.LostAfterPolls is { } lost && unansweredPolls >= lost))
        {
            return null;
        }

        return status is not null && Done(status);
    }

    private void Enter(NightStage stage)
    {
        Stage = stage;
        _inStage = 0;
    }
}
