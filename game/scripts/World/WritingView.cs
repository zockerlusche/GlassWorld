using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// A1356 (B8-11): a few marks above every written thing - per carrier from look/writing.json, paler as the text fades.
/// Redrawn only when the texts changed (count, legibility in steps). Presentation only.
/// </summary>
public partial class WritingView : Node3D
{
    private readonly Dictionary<string, Label3D> _marks = new(StringComparer.Ordinal);
    private LookWriting _look = null!;
    private Dictionary<string, Color> _colors = null!;
    private Func<double, double, double> _ground = null!;
    private string _signature = string.Empty;

    /// <summary>Creates the view.</summary>
    public static WritingView Create(LookWriting look, LookPalette palette, Func<double, double, double> ground) => new()
    {
        Name = "Writing",
        _look = look ?? throw new ArgumentNullException(nameof(look)),
        _colors = (palette ?? throw new ArgumentNullException(nameof(palette))).Colors.ToDictionary(c => c.Id, c => Color.FromHtml(c.Color), StringComparer.Ordinal),
        _ground = ground ?? throw new ArgumentNullException(nameof(ground)),
    };

    /// <summary>Shows the texts now (object id, medium, legibility, position); returns whether it redrew.</summary>
    public bool Refresh(IReadOnlyList<(string ObjectId, string MediumId, double Legibility, double X, double Z)> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var signature = string.Join(";", texts.Select(t => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{t.ObjectId}:{t.MediumId}:{Math.Round(t.Legibility * 20)}:{t.X:0.0}:{t.Z:0.0}")));
        if (signature == _signature)
        {
            return false;
        }

        _signature = signature;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, medium, legibility, x, z) in texts)
        {
            if (_look.Media.FirstOrDefault(m => m.Medium == medium) is not { } marks || !_colors.TryGetValue(marks.Color, out var color))
            {
                continue;
            }

            keep.Add(id);
            if (!_marks.TryGetValue(id, out var label))
            {
                label = new Label3D
                {
                    Name = "Marks_" + id,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    PixelSize = (float)_look.PixelSizeM,
                    FontSize = _look.FontPx,
                    OutlineSize = 0,
                    NoDepthTest = false,
                    Shaded = false,
                };
                AddChild(label);
                _marks[id] = label;
            }

            label.Text = marks.Marks;
            label.Modulate = color with { A = (float)WritingLook.Alpha(legibility, _look.FaintBelow) };
            label.Position = new Vector3((float)x, (float)(_ground(x, z) + _look.AboveM), (float)z);
        }

        foreach (var gone in _marks.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _marks[gone].QueueFree();
            _marks.Remove(gone);
        }

        return true;
    }
}
