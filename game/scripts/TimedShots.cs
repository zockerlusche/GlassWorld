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
        foreach (var t in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).Order())
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
