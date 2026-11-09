namespace GlassWorld.Presentation.World;

/// <summary>
/// Tells a click from a drag (B2567): a press that stays within <c>click_max_px</c> is a click (select a
/// figure, place a gift); once it moves farther it is a camera drag for the rest of the press. A press
/// that never moved is a click however long it is held; one that moved a little (within the distance) is
/// a click only when let go within <c>click_max_ms</c> (content/look/camera.json). Without Godot;
/// positions in pixels, times in milliseconds.
/// </summary>
public sealed class ClickGate
{
    private readonly double _maxPx;
    private readonly double _maxMs;
    private double _x, _y, _ms;
    private bool _moved;

    /// <summary>Creates the gate with the data thresholds.</summary>
    public ClickGate(double maxPx, double maxMs)
    {
        _maxPx = maxPx;
        _maxMs = maxMs;
    }

    /// <summary>Whether a press is running.</summary>
    public bool Pressed { get; private set; }

    /// <summary>Whether the running press has become a drag.</summary>
    public bool Dragging { get; private set; }

    /// <summary>Where the running press started.</summary>
    public (double X, double Y) Start => (_x, _y);

    /// <summary>A press starts.</summary>
    public void Press(double x, double y, double ms)
    {
        (_x, _y, _ms) = (x, y, ms);
        Pressed = true;
        Dragging = false;
        _moved = false;
    }

    /// <summary>The pointer moved; returns whether the press is (now) a drag.</summary>
    public bool Move(double x, double y)
    {
        if (Pressed && (x != _x || y != _y))
        {
            _moved = true;
        }

        if (Pressed && !Dragging && Far(x, y))
        {
            Dragging = true;
        }

        return Dragging;
    }

    /// <summary>The press ends; returns whether it was a click. The gate is free again afterwards.</summary>
    public bool Release(double x, double y, double ms)
    {
        var moved = _moved || x != _x || y != _y;
        var click = Pressed && !Dragging && !Far(x, y) && (!moved || ms - _ms <= _maxMs);
        Cancel();
        return click;
    }

    /// <summary>Forgets the running press (focus lost, button released elsewhere).</summary>
    public void Cancel()
    {
        Pressed = false;
        Dragging = false;
    }

    private bool Far(double x, double y) => ((x - _x) * (x - _x)) + ((y - _y) * (y - _y)) > _maxPx * _maxPx;
}
