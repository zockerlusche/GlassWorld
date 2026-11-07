using System.Globalization;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Window runs only (screenshots for acceptance documents, B2 R32 groundwork): with the environment
/// variable <c>GLASSWORLD_SHOTS=dir;seconds,seconds,...</c> the game saves a PNG of the whole window at
/// each given real second after start and quits after the last one. Never active otherwise.
/// </summary>
public partial class TimedShots : Node
{
    private readonly Queue<double> _times = new();
    private string _directory = string.Empty;
    private double _elapsed;

    /// <summary>Creates the node if the environment asks for shots, else null.</summary>
    public static TimedShots? FromEnvironment()
    {
        var value = System.Environment.GetEnvironmentVariable("GLASSWORLD_SHOTS");
        if (string.IsNullOrWhiteSpace(value) || !value.Contains(';', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = value.Split(';', 2);
        var shots = new TimedShots { Name = "TimedShots", _directory = parts[0] };
        var times = new List<double>();
        foreach (var text in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A wrong value must never stop the game from starting: it is reported and skipped.
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t >= 0 && double.IsFinite(t))
            {
                times.Add(t);
            }
            else
            {
                GD.PushWarning($"TIMED_SHOT ignored invalid time '{text}' in GLASSWORLD_SHOTS");
            }
        }

        foreach (var t in times.Order())
        {
            shots._times.Enqueue(t);
        }

        return shots._times.Count > 0 ? shots : null;
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_times.Count == 0 || _elapsed < _times.Peek())
        {
            return;
        }

        var at = _times.Dequeue();
        var size = GetViewport().GetTexture().GetSize();
        System.IO.Directory.CreateDirectory(_directory);
        var file = System.IO.Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"game_{size.X}x{size.Y}_{at:000}s.png"));
        GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print($"TIMED_SHOT {file}");
        if (_times.Count == 0)
        {
            GetTree().Quit();
        }
    }
}
