namespace GlassWorld.Presentation.World;

/// <summary>A speech or thought bubble to place: its size and the point above the head it belongs to (screen pixels).</summary>
/// <param name="Id">Inhabitant id (one bubble per inhabitant).</param>
/// <param name="AnchorX">Screen x of the point above the head.</param>
/// <param name="AnchorY">Screen y of that point; the bubble's lower edge sits there when nothing is in the way.</param>
/// <param name="Width">Bubble width.</param>
/// <param name="Height">Bubble height.</param>
public readonly record struct BubbleRequest(string Id, double AnchorX, double AnchorY, double Width, double Height);

/// <summary>Where a bubble goes: its top-left corner on screen.</summary>
/// <param name="Id">Inhabitant id.</param>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
public readonly record struct BubblePlace(string Id, double Left, double Top);

/// <summary>
/// Lays out the bubbles above the inhabitants' heads on screen (W1687): each sits centred above its head;
/// bubbles that would overlap stack above one another (or below, when there is no room above) instead of
/// lying on top of each other; all stay inside the free area (below the top bar, beside the side panel,
/// within the screen). Deterministic for the same input. Godot-free.
/// </summary>
public static class BubbleLayout
{
    /// <summary>Places the bubbles inside the area (left, top, right, bottom) with <paramref name="gap"/> pixels between them.</summary>
    public static IReadOnlyList<BubblePlace> Place(IReadOnlyList<BubbleRequest> bubbles, (double Left, double Top, double Right, double Bottom) area, double gap)
    {
        ArgumentNullException.ThrowIfNull(bubbles);

        // The bubble nearest the bottom of the screen (usually the nearest inhabitant) keeps its place first.
        var order = bubbles
            .OrderByDescending(b => b.AnchorY)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();
        var placed = new List<(string Id, double L, double T, double W, double H)>(order.Count);
        foreach (var b in order)
        {
            var w = Math.Min(b.Width, Math.Max(0, area.Right - area.Left));
            var h = b.Height;
            var left = Math.Clamp(b.AnchorX - (w / 2), area.Left, Math.Max(area.Left, area.Right - w));
            var top = ClampTop(b.AnchorY - h, h, area);

            // Move up past every bubble it overlaps; when that leaves the area, try below them instead.
            var up = top;
            for (var guard = 0; guard <= placed.Count && Hit(placed, left, up, w, h) is { } hit; guard++)
            {
                up = hit.T - gap - h;
            }

            if (up >= area.Top && Hit(placed, left, up, w, h) is null)
            {
                top = up;
            }
            else
            {
                var down = top;
                for (var guard = 0; guard <= placed.Count && Hit(placed, left, down, w, h) is { } hit; guard++)
                {
                    down = hit.T + hit.H + gap;
                }

                top = ClampTop(down, h, area);
            }

            placed.Add((b.Id, left, top, w, h));
        }

        return [.. placed.Select(p => new BubblePlace(p.Id, p.L, p.T))];
    }

    private static double ClampTop(double top, double height, (double Left, double Top, double Right, double Bottom) area) =>
        Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - height));

    private static (string Id, double L, double T, double W, double H)? Hit(List<(string Id, double L, double T, double W, double H)> placed, double left, double top, double w, double h)
    {
        foreach (var p in placed)
        {
            if (left < p.L + p.W && p.L < left + w && top < p.T + p.H && p.T < top + h)
            {
                return p;
            }
        }

        return null;
    }
}
