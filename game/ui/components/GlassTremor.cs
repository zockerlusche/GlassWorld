using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// A knock at the glass (B6 R10): two thin rings that tremble a little and fade at one place of the
/// screen, then the node frees itself. Only created when motion is allowed (glass_tremor time > 0).
/// </summary>
public partial class GlassTremor : Control
{
    private double _age;

    /// <summary>Screen position of the knock.</summary>
    public Vector2 Center { get; set; }

    /// <summary>How long it lasts (token glass_tremor).</summary>
    public double Seconds { get; set; } = 0.4;

    /// <summary>Ring sizes and shake from the UI tokens (knock).</summary>
    public GlassWorld.Contracts.UiKnock? Shape { get; set; }

    /// <summary>Ring colour.</summary>
    public Color Color { get; set; } = Colors.White;

    /// <inheritdoc/>
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _age += delta;
        if (_age >= Seconds)
        {
            QueueFree();
            return;
        }

        QueueRedraw();
    }

    /// <inheritdoc/>
    public override void _Draw()
    {
        var t = (float)(_age / Seconds);
        var fade = 1f - t;
        if (Shape is not { } k)
        {
            return;
        }

        var shake = Mathf.Sin(t * Mathf.Pi * (float)k.Shakes) * (float)k.ShakePx * fade;
        var color = new Color(Color, 0.8f * fade);
        DrawArc(Center + new Vector2(shake, 0), (float)(k.OuterPx + (k.OuterGrowPx * t)), 0, Mathf.Tau, 48, color, 2f, true);
        DrawArc(Center - new Vector2(shake, 0), (float)(k.InnerPx + (k.InnerGrowPx * t)), 0, Mathf.Tau, 32, color, 1.5f, true);
    }
}
