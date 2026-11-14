using GlassWorld.Contracts;
using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// A symbol drawn by the game (A1682, D957 v4 section 1): the round buttons, the speeds, seasons and weather of
/// the corners. No picture files, so no licence of others; one colour, the text colour of the theme, so the
/// high-contrast palette reaches it too. Lets the mouse through; the control around it holds the tooltip.
/// </summary>
public partial class GwIcon : Control
{
    private UiIconChoiceIcon _icon = UiIconChoiceIcon.Unknown;
    private int _arrows = 2;

    /// <summary>Creates the symbol.</summary>
    public GwIcon()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>The symbol drawn.</summary>
    public UiIconChoiceIcon Icon
    {
        get => _icon;
        set
        {
            if (_icon != value)
            {
                _icon = value;
                QueueRedraw();
            }
        }
    }

    /// <summary>Arrows of <see cref="UiIconChoiceIcon.Fast"/> (one more per faster speed).</summary>
    public int Arrows
    {
        get => _arrows;
        set
        {
            if (_arrows != value)
            {
                _arrows = value;
                QueueRedraw();
            }
        }
    }

    /// <summary>Colour of the symbol; null = the theme's text colour.</summary>
    public Color? Tint { get; set; }

    /// <inheritdoc/>
    public override void _Notification(int what)
    {
        if (what == NotificationThemeChanged)
        {
            QueueRedraw();
        }
    }

    /// <inheritdoc/>
    public override void _Draw()
    {
        var s = Mathf.Min(Size.X, Size.Y);
        if (s <= 1)
        {
            return;
        }

        var c = Size / 2;
        var colour = Tint ?? GetThemeColor("font_color", "Label");
        var w = Mathf.Max(1.5f, s * 0.08f);
        var r = s * 0.42f;
        switch (_icon)
        {
            case UiIconChoiceIcon.Menu:
                for (var i = -1; i <= 1; i++)
                {
                    DrawLine(c + new Vector2(-r * 0.8f, i * r * 0.55f), c + new Vector2(r * 0.8f, i * r * 0.55f), colour, w * 1.2f, true);
                }

                break;
            case UiIconChoiceIcon.Thumb_up:
            case UiIconChoiceIcon.Thumb_down:
                Thumb(c, r, colour, w, _icon == UiIconChoiceIcon.Thumb_down);
                break;
            case UiIconChoiceIcon.Question:
            case UiIconChoiceIcon.Unknown:
                Letter("?", c, s * 0.8f, colour);
                break;
            case UiIconChoiceIcon.Letter:
                {
                    var box = new Rect2(c - new Vector2(r * 0.95f, r * 0.65f), new Vector2(r * 1.9f, r * 1.3f));
                    DrawRect(box, colour, false, w);
                    DrawPolyline([box.Position, c + new Vector2(0, r * 0.1f), new Vector2(box.End.X, box.Position.Y)], colour, w, true);
                    break;
                }

            case UiIconChoiceIcon.Book:
                {
                    var top = c.Y - (r * 0.7f);
                    var bottom = c.Y + (r * 0.7f);
                    DrawPolyline([new Vector2(c.X, top + (r * 0.1f)), new Vector2(c.X - r, top), new Vector2(c.X - r, bottom), new Vector2(c.X, bottom + (r * 0.1f)), new Vector2(c.X + r, bottom), new Vector2(c.X + r, top), new Vector2(c.X, top + (r * 0.1f)), new Vector2(c.X, bottom + (r * 0.1f))], colour, w, true);
                    break;
                }

            case UiIconChoiceIcon.Person:
                DrawCircle(c + new Vector2(0, -r * 0.45f), r * 0.32f, colour);
                DrawColoredPolygon(Arc(c + new Vector2(0, r * 0.75f), r * 0.75f, r * 0.6f, Mathf.Pi, Mathf.Tau), colour);
                break;
            case UiIconChoiceIcon.Pause:
                DrawRect(new Rect2(c + new Vector2(-r * 0.6f, -r * 0.7f), new Vector2(r * 0.4f, r * 1.4f)), colour);
                DrawRect(new Rect2(c + new Vector2(r * 0.2f, -r * 0.7f), new Vector2(r * 0.4f, r * 1.4f)), colour);
                break;
            case UiIconChoiceIcon.Play:
                Triangle(c, r * 0.8f, colour);
                break;
            case UiIconChoiceIcon.Fast:
                {
                    // The arrows share the whole width (the button is wider for more of them).
                    var n = Math.Clamp(_arrows, 2, 5);
                    var span = Mathf.Max(Size.X * 0.95f, r * 1.8f);
                    var step = span / n;
                    for (var i = 0; i < n; i++)
                    {
                        Triangle(new Vector2(c.X - (span / 2) + (step * (i + 0.5f)), c.Y), Mathf.Min(step * 0.75f, r * 0.8f), colour);
                    }

                    break;
                }

            case UiIconChoiceIcon.Blossom:
                for (var i = 0; i < 5; i++)
                {
                    var a = (Mathf.Tau * i / 5) - (Mathf.Pi / 2);
                    DrawCircle(c + (new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * 0.55f), r * 0.32f, colour);
                }

                DrawCircle(c, r * 0.22f, GetThemeColor("font_outline_color", "Label") with { A = 1 });
                break;
            case UiIconChoiceIcon.Sun:
                Sun(c, r, colour, w);
                break;
            case UiIconChoiceIcon.Leaf:
                {
                    var tip = c + new Vector2(r * 0.8f, -r * 0.8f);
                    var stem = c + new Vector2(-r * 0.8f, r * 0.8f);
                    var side = new Vector2(r * 0.55f, r * 0.55f);
                    DrawColoredPolygon(Bezier(stem, c - side, tip, c + side), colour);
                    DrawLine(stem, tip, GetThemeColor("font_outline_color", "Label") with { A = 1 }, w * 0.7f, true);
                    break;
                }

            case UiIconChoiceIcon.Snowflake:
                Flake(c, r, colour, w);
                break;
            case UiIconChoiceIcon.Cloud:
                Cloud(c, r, colour);
                break;
            case UiIconChoiceIcon.Rain:
                Cloud(c + new Vector2(0, -r * 0.3f), r * 0.85f, colour);
                for (var i = -1; i <= 1; i++)
                {
                    var x = c.X + (i * r * 0.5f);
                    DrawLine(new Vector2(x, c.Y + (r * 0.35f)), new Vector2(x - (r * 0.2f), c.Y + (r * 0.9f)), colour, w, true);
                }

                break;
            case UiIconChoiceIcon.Snow:
                Cloud(c + new Vector2(0, -r * 0.3f), r * 0.85f, colour);
                for (var i = -1; i <= 1; i++)
                {
                    DrawCircle(new Vector2(c.X + (i * r * 0.5f), c.Y + (r * (i == 0 ? 0.85f : 0.6f))), w * 0.9f, colour);
                }

                break;
            case UiIconChoiceIcon.Fog:
                for (var i = -1; i <= 1; i++)
                {
                    var y = c.Y + (i * r * 0.5f);
                    var shift = i * r * 0.2f;
                    DrawLine(new Vector2(c.X - r + shift, y), new Vector2(c.X + r * 0.8f + shift, y), colour, w * 1.2f, true);
                }

                break;
            case UiIconChoiceIcon.Storm:
                Cloud(c + new Vector2(0, -r * 0.35f), r * 0.85f, colour);
                DrawColoredPolygon([c + new Vector2(r * 0.1f, r * 0.15f), c + new Vector2(-r * 0.3f, r * 0.6f), c + new Vector2(0, r * 0.6f), c + new Vector2(-r * 0.15f, r), c + new Vector2(r * 0.35f, r * 0.45f), c + new Vector2(r * 0.05f, r * 0.45f)], colour);
                break;
            case UiIconChoiceIcon.Frost:
                DrawArc(c, r, 0, Mathf.Tau, 32, colour, w * 0.8f, true);
                Flake(c, r * 0.7f, colour, w * 0.8f);
                break;
            case UiIconChoiceIcon.Heat:
                {
                    // A thermometer: tube and bulb.
                    DrawRect(new Rect2(c + new Vector2(-r * 0.18f, -r), new Vector2(r * 0.36f, r * 1.3f)), colour, false, w);
                    DrawCircle(c + new Vector2(0, r * 0.55f), r * 0.35f, colour);
                    DrawLine(c + new Vector2(0, -r * 0.5f), c + new Vector2(0, r * 0.3f), colour, w * 1.3f, true);
                    break;
                }

            case UiIconChoiceIcon.Drought:
                Sun(c + new Vector2(0, -r * 0.35f), r * 0.6f, colour, w * 0.8f);
                DrawPolyline([c + new Vector2(-r, r * 0.75f), c + new Vector2(-r * 0.4f, r * 0.75f), c + new Vector2(-r * 0.2f, r * 0.45f), c + new Vector2(0, r * 0.75f), c + new Vector2(r * 0.3f, r * 0.75f), c + new Vector2(r * 0.45f, r * 0.5f), c + new Vector2(r * 0.6f, r * 0.75f), c + new Vector2(r, r * 0.75f)], colour, w, true);
                break;
            default:
                Letter("?", c, s * 0.8f, colour);
                break;
        }
    }

    private void Letter(string text, Vector2 centre, float height, Color colour)
    {
        var font = GetThemeFont("font", "Label");
        var size = Math.Max(8, (int)height);
        var ascent = font.GetAscent(size);
        var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        DrawString(font, new Vector2(centre.X - (width / 2), centre.Y + (ascent / 2) - (size * 0.08f)), text, HorizontalAlignment.Left, -1, size, colour);
    }

    /// <summary>
    /// A thumb up, or down (the bar rule B2642): a closed hand with the thumb raised; down is the same hand turned
    /// upside down, so both read alike at a glance.
    /// </summary>
    private void Thumb(Vector2 centre, float r, Color colour, float width, bool down)
    {
        var flip = down ? -1f : 1f;
        Vector2 P(float x, float y) => centre + new Vector2(x * r, y * r * flip);

        // The cuff on the left, the closed fingers as a block, the thumb raised from the block's upper left.
        DrawColoredPolygon([P(-0.9f, -0.05f), P(-0.55f, -0.05f), P(-0.55f, 0.85f), P(-0.9f, 0.85f)], colour);
        DrawColoredPolygon([P(-0.45f, -0.1f), P(0.75f, -0.1f), P(0.85f, 0.05f), P(0.85f, 0.75f), P(0.7f, 0.85f), P(-0.45f, 0.85f)], colour);
        DrawLine(P(-0.25f, -0.05f), P(0.05f, -0.85f), colour, width * 2.2f, true);
        DrawCircle(P(0.05f, -0.85f), width * 1.1f, colour);
    }

    private void Triangle(Vector2 centre, float r, Color colour) =>
        DrawColoredPolygon([centre + new Vector2(-r * 0.6f, -r), centre + new Vector2(r * 0.9f, 0), centre + new Vector2(-r * 0.6f, r)], colour);

    private void Sun(Vector2 centre, float r, Color colour, float width)
    {
        DrawCircle(centre, r * 0.45f, colour);
        for (var i = 0; i < 8; i++)
        {
            var a = Mathf.Tau * i / 8;
            var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            DrawLine(centre + (d * r * 0.65f), centre + (d * r * 0.95f), colour, width, true);
        }
    }

    private void Flake(Vector2 centre, float r, Color colour, float width)
    {
        for (var i = 0; i < 3; i++)
        {
            var a = (Mathf.Pi * i / 3) + (Mathf.Pi / 2);
            var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * 0.9f;
            DrawLine(centre - d, centre + d, colour, width, true);
            foreach (var end in new[] { centre + d, centre - d })
            {
                var back = (centre - end).Normalized() * r * 0.3f;
                DrawLine(end + (back * 0.6f), end + (back * 0.6f) + back.Rotated(0.9f), colour, width * 0.8f, true);
                DrawLine(end + (back * 0.6f), end + (back * 0.6f) + back.Rotated(-0.9f), colour, width * 0.8f, true);
            }
        }
    }

    private void Cloud(Vector2 centre, float r, Color colour)
    {
        DrawCircle(centre + new Vector2(-r * 0.45f, r * 0.1f), r * 0.38f, colour);
        DrawCircle(centre + new Vector2(r * 0.05f, -r * 0.15f), r * 0.5f, colour);
        DrawCircle(centre + new Vector2(r * 0.5f, r * 0.12f), r * 0.35f, colour);
        DrawRect(new Rect2(centre + new Vector2(-r * 0.45f, r * 0.1f), new Vector2(r * 0.95f, r * 0.37f)), colour);
    }

    private static Vector2[] Arc(Vector2 centre, float rx, float ry, float from, float to)
    {
        const int Steps = 16;
        var points = new Vector2[Steps + 1];
        for (var i = 0; i <= Steps; i++)
        {
            var a = from + ((to - from) * i / Steps);
            points[i] = centre + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
        }

        return points;
    }

    private static Vector2[] Bezier(Vector2 from, Vector2 controlA, Vector2 to, Vector2 controlB)
    {
        const int Steps = 12;
        var points = new List<Vector2>();
        for (var i = 0; i <= Steps; i++)
        {
            points.Add(from.BezierInterpolate(controlA, controlA, to, (float)i / Steps));
        }

        for (var i = 1; i < Steps; i++)
        {
            points.Add(to.BezierInterpolate(controlB, controlB, from, (float)i / Steps));
        }

        return [.. points];
    }
}
