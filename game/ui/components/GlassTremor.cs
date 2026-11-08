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

    /// <summary>Ring colour.</summary>
    public Color Color { get; set; } = Colors.White;

    /// <inheritdoc/>
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
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
        var shake = Mathf.Sin(t * Mathf.Pi * 6f) * 2f * fade;
        var color = new Color(Color, 0.8f * fade);
        DrawArc(Center + new Vector2(shake, 0), 18f + (14f * t), 0, Mathf.Tau, 48, color, 2f, true);
        DrawArc(Center - new Vector2(shake, 0), 10f + (8f * t), 0, Mathf.Tau, 32, color, 1.5f, true);
    }
}
