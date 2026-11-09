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
        var factor = FactorAt(node, node.GlobalPosition, sizeM, tokens, uiScale);
        node.Scale = new Vector3(factor, factor, factor);
    }

    /// <summary>
    /// The scale factor for a symbol of world height <paramref name="sizeM"/> at <paramref name="at"/>; offsets
    /// around the head scale by the same factor, so a bigger symbol does not reach into the head (review GW1 #141).
    /// 1 without a camera.
    /// </summary>
    public static float FactorAt(Node node, Vector3 at, double sizeM, UiTokens tokens, double uiScale)
    {
        if (node.GetViewport()?.GetCamera3D() is not { } camera)
        {
            return 1;
        }

        var height = node.GetViewport().GetVisibleRect().Size.Y;
        var perMetre = camera.Projection == Camera3D.ProjectionType.Orthogonal
            ? height / Math.Max(camera.Size, 0.001)
            : HeadSymbolSize.PixelsPerMetre(camera.GlobalPosition.DistanceTo(at), camera.Fov, height);
        var pixels = height / tokens.BaseHeight * uiScale;
        return (float)HeadSymbolSize.Factor(sizeM, perMetre, tokens.HeadSymbol.MinPx * pixels, tokens.HeadSymbol.MaxPx * pixels);
    }

    /// <summary>
    /// Smoke only: whether the symbol's height as the camera projects it lies within floor and ceiling
    /// (5 % tolerance for perspective off the view axis); empty when it does or the node is not in view.
    /// </summary>
    public static string CheckOnScreen(Node3D node, double worldHeightM, UiTokens tokens, double uiScale)
    {
        if (node.GetViewport()?.GetCamera3D() is not { } camera || camera.IsPositionBehind(node.GlobalPosition)
            || !node.GetViewport().GetVisibleRect().HasPoint(camera.UnprojectPosition(node.GlobalPosition)))
        {
            return "not in view";
        }

        // A billboard stands along the camera's up axis, not the world's.
        var half = camera.GlobalBasis.Y.Normalized() * (float)(worldHeightM * node.Scale.Y / 2);
        var px = (camera.UnprojectPosition(node.GlobalPosition - half) - camera.UnprojectPosition(node.GlobalPosition + half)).Length();
        var pixels = node.GetViewport().GetVisibleRect().Size.Y / tokens.BaseHeight * uiScale;
        // Off the view axis perspective stretches a little: 5 % (at least 2 px) tolerance.
        var (floor, ceiling) = (tokens.HeadSymbol.MinPx * pixels, tokens.HeadSymbol.MaxPx * pixels);
        var (min, max) = (Math.Min(floor - 2, floor * 0.95), Math.Max(ceiling + 2, ceiling * 1.05));
        return px >= min && px <= max ? string.Empty : $"'{node.Name}' is {px:0.0} px on screen, expected {floor:0}-{ceiling:0} (scale {node.Scale.Y:0.000}, {camera.GlobalPosition.DistanceTo(node.GlobalPosition):0.00} m, {camera.Projection})";
    }
}

/// <summary>The question mark above a head: a label that keeps its screen size within the data's limits (F5284).</summary>
public partial class HeadSymbolLabel : Label3D
{
    /// <summary>World height at scale 1.</summary>
    public double SizeM { get; set; }

    /// <summary>Height of the mark's centre above its parent at scale 1; a bigger mark grows upwards from its lower edge.</summary>
    public double BaseHeightM { get; set; }

    /// <summary>The current UI tokens and scale (they change with the settings); null leaves the size alone.</summary>
    public Func<GlassWorld.Presentation.Ui.UiStyle?> Style { get; set; } = () => null;

    public override void _Process(double delta)
    {
        if (Visible && Style() is { } style)
        {
            HeadSymbolScale.Apply(this, SizeM, style.Tokens, style.ContentScaleFactor);
            Position = new Vector3(Position.X, (float)(BaseHeightM + ((Scale.Y - 1) * SizeM / 2)), Position.Z);
        }
    }
}
