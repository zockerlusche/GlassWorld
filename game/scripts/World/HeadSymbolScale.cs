using GlassWorld.Contracts;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>Scales a head symbol each frame so its screen height stays within the data's floor and ceiling (F5284).</summary>
public static class HeadSymbolScale
{
    /// <summary>Sets the node's scale for its world height <paramref name="sizeM"/> as the current camera sees it.</summary>
    /// <param name="node">The symbol (sprite or label).</param>
    /// <param name="sizeM">The symbol's own height in the world at scale 1.</param>
    /// <param name="tokens">UI tokens: head_symbol floor and ceiling, given at the reference height base_height.</param>
    /// <param name="uiScale">The player's UI size (bigger UI, bigger symbols).</param>
    public static void Apply(Node3D node, double sizeM, UiTokens tokens, double uiScale)
    {
        if (node.GetViewport()?.GetCamera3D() is not { } camera)
        {
            return;
        }

        var height = node.GetViewport().GetVisibleRect().Size.Y;
        var perMetre = camera.Projection == Camera3D.ProjectionType.Orthogonal
            ? height / Math.Max(camera.Size, 0.001)
            : HeadSymbolSize.PixelsPerMetre(camera.GlobalPosition.DistanceTo(node.GlobalPosition), camera.Fov, height);
        var pixels = height / tokens.BaseHeight * uiScale;
        var factor = (float)HeadSymbolSize.Factor(sizeM, perMetre, tokens.HeadSymbol.MinPx * pixels, tokens.HeadSymbol.MaxPx * pixels);
        node.Scale = new Vector3(factor, factor, factor);
    }
}

/// <summary>The question mark above a head: a label that keeps its screen size within the data's limits (F5284).</summary>
public partial class HeadSymbolLabel : Label3D
{
    /// <summary>World height at scale 1.</summary>
    public double SizeM { get; set; }

    /// <summary>The current UI tokens and scale (they change with the settings); null leaves the size alone.</summary>
    public Func<GlassWorld.Presentation.Ui.UiStyle?> Style { get; set; } = () => null;

    public override void _Process(double delta)
    {
        if (Visible && Style() is { } style)
        {
            HeadSymbolScale.Apply(this, SizeM, style.Tokens, style.ContentScaleFactor);
        }
    }
}
