using GlassWorld.Game.Audio;
using GlassWorld.Game.Localization;
using GlassWorld.Game.Ui;
using GlassWorld.Game.Ui.Screens;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Root of the start scene. Starts the core world (placeholder start until B2) and supports a headless
/// smoke run via the user argument <c>--smoke-frames=N</c>: after N frames it prints marker lines and
/// quits with exit code 0. <c>--seed=N</c> chooses the world seed (default 1).
/// </summary>
public partial class Main : Node3D
{
    private const string SmokeArgument = "--smoke-frames=";
    private const string SeedArgument = "--seed=";
    private string? _shotsFolder;

    private int _smokeFrames;
    private int _frame;
    private ulong _seed = 1;
    private WorldPresenter? _world;
    private WorldAudio? _audio;
    private GameScreen? _screen;
    private bool _uiCheckDone = true;

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

            if (arg.StartsWith(UiShots.Argument, StringComparison.Ordinal))
            {
                _shotsFolder = arg[UiShots.Argument.Length..];
            }

            if (arg.StartsWith(SeedArgument, StringComparison.Ordinal)
                && ulong.TryParse(arg.AsSpan(SeedArgument.Length), out var seed))
            {
                _seed = seed;
            }
        }

        _world = new WorldPresenter { Name = "World" };
        AddChild(_world);
        var content = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "content"));
        _world.Start(content, _seed);
        var host = _world.Host!;
        GD.Print($"WORLD_OK seed={_seed} inhabitants={host.Inhabitants.All.Count()} objects={host.Objects.Count} hash={host.World.StateHash()[..12]}");

        _audio = new WorldAudio { Name = "WorldAudio" };
        AddChild(_audio);
        var spatialReady = _audio.Setup(host, _seed);

        var audioLoaded = RetroAudio.Load();
        var style = UiSetup.Load();
        if (style is not null)
        {
            UiSetup.Apply(GetTree().Root, style);
            _screen = new GameScreen { Name = "GameScreen" };
            AddChild(_screen);
            _screen.Setup(_world, style);
            if (_shotsFolder is not null)
            {
                _ = TakeShotsAsync(style);
            }
        }

        if (_smokeFrames > 0)
        {
            CheckTexts(textsLoaded);
            CheckAudio(audioLoaded);
            CheckSpatial(spatialReady);
            _uiCheckDone = false;
            _ = CheckUiAsync(style);
        }
    }

    /// <summary>Smoke run only: every mapped sound renders to a stream of the right length (B8 R3).</summary>
    private void CheckSpatial(bool ready)
    {
        var problem = ready && _audio is not null ? _audio.SpatialSelfCheck() : "world audio not set up";
        if (problem.Length > 0)
        {
            GD.PushError($"AUDIO_SPATIAL_FAIL {problem}");
            return;
        }

        GD.Print("AUDIO_SPATIAL_OK");
    }

    private static void CheckAudio(bool loaded)
    {
        var problem = loaded ? RetroAudio.SelfCheck() : "audio data not loaded";
        if (problem.Length > 0)
        {
            GD.PushError($"AUDIO_FAIL {problem}");
            return;
        }

        GD.Print($"AUDIO_OK sounds={RetroAudio.Library!.UsedSoundIds().Count()}");
    }

    /// <summary>Screenshot run (self-acceptance, KR5): saves the shots, then quits.</summary>
    private async Task TakeShotsAsync(UiStyle style)
    {
        _world!.Speed = 0;
        var files = await UiShots.RunAsync(this, _screen!, style, _shotsFolder!);
        GD.Print($"UI_SHOTS_OK {files.Count}");
        GetTree().Quit(0);
    }

    /// <summary>Smoke run only: the UI standard data is valid and a sample window fits every reference case.</summary>
    private async Task CheckUiAsync(UiStyle? style)
    {
        try
        {
            if (style is null)
            {
                GD.PushError("UI_FAIL ui data not loaded");
                return;
            }

            var layer = new CanvasLayer();
            AddChild(layer);
            var problems = await UiLayoutCheck.RunAsync(layer, style, ScreenModel.TimeChoices(_world!.Host!.World.Calendar.Settings));
            var screen = _screen is null ? "game screen missing" : await _screen.SelfCheckAsync();
            if (screen.Length > 0)
            {
                problems = [.. problems, "game screen: " + screen];
            }
            else
            {
                GD.Print("SCREEN_OK");
            }
            foreach (var problem in problems)
            {
                GD.PushError($"UI_FAIL {problem}");
            }

            if (problems.Count == 0)
            {
                GD.Print("UI_OK");
            }

            layer.QueueFree();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            GD.PushError($"UI_FAIL {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _uiCheckDone = true;
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
        if (_frame >= _smokeFrames && _uiCheckDone)
        {
            GD.Print($"WORLD_TICKS {_world?.Host?.World.Tick ?? -1}");
            GD.Print($"SMOKE_OK frames={_frame}");
            GetTree().Quit(0);
            _smokeFrames = 0;
        }
    }
}
