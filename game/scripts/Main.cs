using GlassWorld.Game.Localization;
using GlassWorld.Simulation;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Root of the start scene. Supports a headless smoke run via the user argument
/// <c>--smoke-frames=N</c>: after N frames it prints a marker line and quits with exit code 0.
/// </summary>
public partial class Main : Node3D
{
    private const string SmokeArgument = "--smoke-frames=";

    private int _smokeFrames;
    private int _frame;

    public override void _Ready()
    {
        GD.Print($"GlassWorld started, core: {CoreInfo.AssemblyName}");
        var textsLoaded = TextCatalogs.Install();
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith(SmokeArgument, StringComparison.Ordinal)
                && int.TryParse(arg.AsSpan(SmokeArgument.Length), out var frames)
                && frames > 0)
            {
                _smokeFrames = frames;
            }
        }

        if (_smokeFrames > 0)
        {
            CheckTexts(textsLoaded);
        }
    }

    /// <summary>Smoke run only: proves that every catalog is loaded and the language can be switched.</summary>
    private static void CheckTexts(bool loaded)
    {
        var problem = loaded ? TextCatalogs.SelfCheck() : "catalogs not loaded";
        if (problem.Length > 0)
        {
            GD.PushError($"I18N_FAIL {problem}");
            return;
        }

        GD.Print($"I18N_OK locales={string.Join(',', TextCatalogs.SupportedLocales)} keys={TextCatalogs.KeyCount}");
    }

    public override void _Process(double delta)
    {
        if (_smokeFrames == 0)
        {
            return;
        }

        _frame++;
        if (_frame >= _smokeFrames)
        {
            GD.Print($"SMOKE_OK frames={_frame}");
            GetTree().Quit(0);
            _smokeFrames = 0;
        }
    }
}
