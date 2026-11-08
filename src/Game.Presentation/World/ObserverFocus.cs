using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// The observer camera of the island mode without Godot (#63 for the island): it shows the group where
/// the last thought appeared, otherwise moves on to the next group after a while. The player takes the
/// camera by panning or by choosing a figure; after a calm pause without camera input, and only when no
/// figure is chosen, the observer takes it back. Zoom and turn never take it away. Presentation only.
/// </summary>
public sealed class ObserverFocus
{
    private readonly WorldLookObserver _data;
    private double _switchAt = double.NegativeInfinity;
    private double _manualUntil = double.NegativeInfinity;

    /// <summary>Creates the observer with the data values.</summary>
    public ObserverFocus(WorldLookObserver data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
    }

    /// <summary>Group shown now (null before the first <see cref="Update"/> with groups).</summary>
    public int? Group { get; private set; }

    /// <summary>Inhabitant the player chose (the camera follows it), or null.</summary>
    public string? Chosen { get; private set; }

    /// <summary>Whether the observer has the camera at <paramref name="now"/> (real seconds).</summary>
    public bool Observing(double now) => Chosen is null && now >= _manualUntil;

    /// <summary>A thought appeared in a group: the observer stays with that group for a while.</summary>
    public void Thought(int group, double now)
    {
        Group = group;
        _switchAt = now + _data.ThoughtFocusS;
    }

    /// <summary>
    /// The group to show at <paramref name="now"/> among <paramref name="groups"/> (in order); moves on to the
    /// next group when the time is up or the shown group is gone. Null without groups.
    /// </summary>
    public int? Update(double now, IReadOnlyList<int> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0)
        {
            Group = null;
            return null;
        }

        if (Group is not { } group || !groups.Contains(group))
        {
            Group = groups[0];
            _switchAt = now + _data.GroupSwitchS;
        }
        else if (now >= _switchAt)
        {
            Group = groups[(IndexOf(groups, group) + 1) % groups.Count];
            _switchAt = now + _data.GroupSwitchS;
        }

        return Group;
    }

    /// <summary>The player moved the camera (panning, a click into empty ground): the observer waits.</summary>
    public void PlayerTookCamera(double now)
    {
        Chosen = null;
        _manualUntil = now + _data.ResumeAfterIdleS;
    }

    /// <summary>The player chose a figure: the camera follows it until the player lets go.</summary>
    public void Choose(string inhabitantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(inhabitantId);
        Chosen = inhabitantId;
    }

    /// <summary>The chosen inhabitant is gone (or let go): the observer waits its pause, then takes over.</summary>
    public void Release(double now)
    {
        if (Chosen is not null)
        {
            PlayerTookCamera(now);
        }
    }

    private static int IndexOf(IReadOnlyList<int> groups, int group)
    {
        for (var i = 0; i < groups.Count; i++)
        {
            if (groups[i] == group)
            {
                return i;
            }
        }

        return -1;
    }
}
