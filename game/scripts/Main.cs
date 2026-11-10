using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Game.Audio;
using GlassWorld.Game.Localization;
using GlassWorld.Game.Ui;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Game.Ui.Screens;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Safety;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Root of the start scene: main menu (B6 R3), the running world with its screen and pause menu, and
/// the player settings (R4). Development and check runs skip the menu and start the placeholder world
/// at once: <c>--smoke-frames=N</c> (headless smoke, prints marker lines and quits after N frames),
/// <c>--ui-shots=&lt;dir&gt;</c> (screenshots) and <c>--seed=N</c> (world seed, default 1).
/// <c>--debug</c> lets the core accept debug commands (the debug jumps of the debug view, B2-6 R31);
/// without it they are refused.
/// </summary>
public partial class Main : Node3D
{
    private const string SmokeArgument = "--smoke-frames=";
    private const string SeedArgument = "--seed=";
    private const string DebugArgument = "--debug";

    private string? _shotsFolder;
    private string _contentDirectory = string.Empty;
    private int _smokeFrames;
    private int _frame;
    private ulong _seed = 1;
    private bool _seedGiven;

    /// <summary>Debug commands of the core (debug jumps, B2-6 R31): only with <c>--debug</c>, off otherwise.</summary>
    private bool _debugCommands;
    private UiStyle? _baseStyle;
    private UiStyle? _style;
    private SoundMix? _mix;
    private SettingsLimits? _limits;
    private PlayerSettings _settings = new();
    private string _settingsPath = string.Empty;
    private WorldLibrary? _library;
    private MainMenu? _menu;
    private WorldPresenter? _world;
    private WorldAudio? _audio;
    private FeelingSymbols? _symbols;
    private GameScreen? _screen;
    private WorldMeta? _meta;
    private GwWindow? _pause;

    /// <summary>Whether the autosave notice was shown in this session (once is enough).</summary>
    private bool _autosaveNoticeShown;
    private double _speedBeforePause = 1;
    private bool _settingsDirty;
    private MenuData? _menuData;

    /// <inheritdoc/>
    public override void _Ready()
    {
        if (TimedShots.FromEnvironment() is { } shots)
        {
            AddChild(shots);
        }

        GD.Print($"GlassWorld started, core: {CoreInfo.AssemblyName}");

        // Closing the window always saves first (B2469); the quit happens in _Notification.
        GetTree().AutoAcceptQuit = false;
        var textsLoaded = TextCatalogs.Install();
        ReadArguments();
        _contentDirectory = ContentLocator.ContentDirectory();
        var audioLoaded = RetroAudio.Load() & MurmurAudio.Load();
        _baseStyle = UiSetup.Load();
        _mix = ContractSerializer.Deserialize<SoundMix>(System.IO.File.ReadAllText(System.IO.Path.Combine(_contentDirectory, "sound", "mix.json")));
        _library = new WorldLibrary(ProjectSettings.GlobalizePath("user://worlds"));
        if (_baseStyle is not null)
        {
            _menuData = LoadMenuData(_library);
            _limits = new SettingsLimits(_baseStyle.Tokens, TextCatalogs.SupportedLocales, _mix.PlayerVolumeDb, _menuData.Options.PlayerNameMaxLength, _menuData.Guard, _mix.DefaultsVersion);

            // Check runs use the defaults, so a player's settings file never changes a smoke result.
            _settingsPath = SettingsStore.DefaultPath;
            _settings = _smokeFrames > 0 || _shotsFolder is not null ? PlayerSettings.Parse(null, _limits) : SettingsStore.Load(_settingsPath, _limits);
            ApplySettings(_settings);
        }

        if (_smokeFrames > 0 || _shotsFolder is not null || _seedGiven || _baseStyle is null)
        {
            var spatialReady = StartWorld(presenter => presenter.Start(_contentDirectory, _seed), null);
            if (_shotsFolder is not null && _style is not null)
            {
                _ = TakeShotsAsync(_style);
            }

            if (_smokeFrames > 0)
            {
                StartChecks(textsLoaded, audioLoaded, spatialReady);
            }

            return;
        }

        ShowMenu();
    }

    /// <inheritdoc/>
    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            SaveAndQuit();
        }
    }

    private void ReadArguments()
    {
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
                _seedGiven = true;
            }

            if (arg == DebugArgument)
            {
                _debugCommands = true;
            }
        }
    }

    /// <summary>Applies settings at once (R4): language, UI and text size, contrast, hints, volumes.</summary>
    private void ApplySettings(PlayerSettings settings)
    {
        if (_baseStyle is null || _mix is null)
        {
            return;
        }

        if (settings.Language is not null)
        {
            TextCatalogs.SetLanguage(settings.Language);
        }

        _style = _baseStyle.With(settings.ToUiSettings());
        UiSetup.Apply(GetTree().Root, _style);
        if (_symbols is not null)
        {
            _symbols.Style = _style;
        }
        _screen?.ApplySettings(settings, _style);
        WorldAudio.ApplyVolumes(_mix, settings.BusVolumesDb);
    }

    private void ShowMenu()
    {
        if (_style is null || _library is null)
        {
            return;
        }

        _menu = new MainMenu { Name = "MainMenu" };
        AddChild(_menu);
        _menu.Setup(LoadMenuData(_library), _style);
        _menu.NewWorldRequested += choice =>
        {
            var meta = _library.Create(choice, DateTimeOffset.UtcNow);
            Enter(presenter => presenter.CreateWorld(_contentDirectory, choice.Seed, choice.StartFavor, _library.SavePath(meta.Id)), meta, created: true);
        };
        _menu.LoadRequested += meta => Enter(presenter => presenter.Load(_contentDirectory, _library.SavePath(meta.Id)), meta);
        _menu.SettingsRequested += () => OpenSettings(_menu.ShowWindow);
        _menu.QuitRequested += () =>
        {
            SaveSettings();
            GetTree().Quit(0);
        };
    }

    /// <summary>
    /// Saves and writes the world as one shareable local file (F5234 C) into user://shared; the hint names
    /// the folder and says the file holds the whole world, conversations and names included.
    /// </summary>
    private void ShareWorld()
    {
        if (_meta is null || _library is null || _screen is null)
        {
            return;
        }

        TrySave();
        try
        {
            var sharing = ContractSerializer.Deserialize<WorldSharing>(System.IO.File.ReadAllText(System.IO.Path.Combine(_contentDirectory, "player", "sharing.json")));
            var meta = _library.List().FirstOrDefault(w => w.Id == _meta.Id) ?? _meta;
            var folder = ProjectSettings.GlobalizePath("user://shared");
            var file = WorldShare.Export(_library, meta, _screen.ChronicleHtml(), folder, sharing, DateTimeOffset.UtcNow);
            _screen.ShowHint(new ToastMessage("msg.share.saved", new Dictionary<string, string> { ["folder"] = folder, ["file"] = System.IO.Path.GetFileName(file) }, null));
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            GD.PushWarning($"sharing the world failed: {e.Message}");
            _screen.ShowHint(new ToastMessage("msg.share.failed", new Dictionary<string, string>(), null));
        }
    }

    private MenuData LoadMenuData(WorldLibrary library)
    {
        var player = System.IO.Path.Combine(_contentDirectory, "player");
        return new MenuData(
            library,
            ContractSerializer.Deserialize<WorldOptions>(System.IO.File.ReadAllText(System.IO.Path.Combine(player, "world_options.json"))),
            ContractSerializer.Deserialize<FavorSettings>(System.IO.File.ReadAllText(System.IO.Path.Combine(player, "favor.json"))),
            TextGuard.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(_contentDirectory, "safety", "rules.json"))))
        {
            Sharing = ContractSerializer.Deserialize<WorldSharing>(System.IO.File.ReadAllText(System.IO.Path.Combine(player, "sharing.json"))),
        };
    }

    /// <summary>Leaves the menu for a world; if the world cannot be built, the menu stays with a message.</summary>
    private void Enter(Action<WorldPresenter> begin, WorldMeta meta, bool created = false)
    {
        // Loading takes a moment and blocks the frame: say so first, then load once that has been drawn (W1688).
        if (_menu is null)
        {
            EnterNow(begin, meta, created);
            return;
        }

        _menu.ShowLoading(meta.Name);
        GetTree().CreateTimer(0.05).Timeout += () => EnterNow(begin, meta, created);
    }

    private void EnterNow(Action<WorldPresenter> begin, WorldMeta meta, bool created)
    {
        try
        {
            StartWorld(begin, meta);
            _menu?.QueueFree();
            _menu = null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            GD.PushWarning($"LOAD_FAILED {meta.Id} {e.GetType().Name}: {e.Message}");
            CloseWorld();
            if (created)
            {
                // A new world that failed before its first save leaves no empty entry behind.
                _library?.Discard(meta.Id);
            }

            if (_menu is not null && _style is not null)
            {
                var message = new GwWindow();
                message.Setup("ui.worlds.title", _style);
                message.Content.AddChild(new Label { Text = "msg.load.failed", AutowrapMode = TextServer.AutowrapMode.WordSmart });
                message.AddAction("ui.common.close").Pressed += message.Close;
                _menu.Rebuild();
                _menu.ShowWindow(message);
            }
        }
    }

    /// <summary>Builds presenter, audio and screen around a world; returns whether spatial audio is ready.</summary>
    private bool StartWorld(Action<WorldPresenter> begin, WorldMeta? meta)
    {
        _meta = meta;
        _world = new WorldPresenter { Name = "World", DebugCommands = _debugCommands };
        AddChild(_world);
        begin(_world);
        var host = _world.Host!;
        GD.Print($"WORLD_OK seed={host.World.Seed} inhabitants={host.Inhabitants.All.Count()} objects={host.Objects.Count} hash={host.World.StateHash()[..12]}");

        _symbols = new FeelingSymbols { Name = "FeelingSymbols", Style = _style };
        _world.AddChild(_symbols);
        if (!_symbols.Setup(host, _world, () => _settings.FeelingSymbols, () => _settings.ReduceMotion))
        {
            _symbols.QueueFree();
            _symbols = null;
        }

        _audio = new WorldAudio { Name = "WorldAudio" };
        AddChild(_audio);
        if (_world.Mode == WorldLookMode.Island)
        {
            _audio.GroundHeight = _world.GroundHeight;
            _audio.Hearing = _world.Hearing;
        }

        var spatialReady = _audio.Setup(host, host.World.Seed, GlassWorld.Presentation.Audio.InventionSounds.DefaultFolder(_contentDirectory, OS.GetEnvironment("GW_AI_SOUND_OUTPUT_DIR"), OS.GetEnvironment("GLASSWORLD_AI_ROOT")));
        if (_mix is not null)
        {
            WorldAudio.ApplyVolumes(_mix, _settings.BusVolumesDb);
        }

        // Once per session is enough to know the game saves by itself; every day would be every ~75 s
        // at the fastest speed (review GW1 #139, calm world).
        _world.AutoSaved += () =>
        {
            if (!_autosaveNoticeShown)
            {
                _autosaveNoticeShown = true;
                _screen?.ShowHint(new ToastMessage("msg.hint.autosaved", new Dictionary<string, string>(), null));
            }
        };
        _world.Saved += _ =>
        {
            if (_meta is not null && _library is not null)
            {
                _meta = _meta with { Day = (host.World.Tick / host.World.Calendar.TicksPerDay) + 1, LastPlayedUtc = DateTimeOffset.UtcNow };
                _library.Write(_meta);
            }
        };

        if (_style is not null)
        {
            _screen = new GameScreen { Name = "GameScreen" };
            AddChild(_screen);
            _screen.Setup(_world, _style);

            // The debug view (B2-6 R31): its own control on the screen's layer, hidden until F12.
            var debug = new GwDebugOverlay { Name = DebugOverlayName };
            _screen.AddChild(debug);
            debug.Setup(_world, _style);
            HintIfAnAiNeedsThePlayer();
            _screen.ApplySettings(_settings, _style);
            _screen.SetupChronicle(ContractSerializer.Deserialize<ChronicleKinds>(System.IO.File.ReadAllText(System.IO.Path.Combine(_contentDirectory, "chronicle", "kinds.json"))));
            _world.SaveFailed += _ => _screen?.ShowHint(SaveFailedHint());
            _screen.PauseRequested += OpenPause;
        }

        return spatialReady;
    }

    private void OpenPause()
    {
        if (_pause is not null || _world is null || _screen is null || _style is null)
        {
            return;
        }

        _speedBeforePause = _world.Speed;
        _world.Speed = 0;
        _screen.SetTimeControlsEnabled(false);
        _pause = PauseMenu.Create(_style, new PauseActions(
            Resume: () =>
            {
                _pause = null;
                _screen?.SetTimeControlsEnabled(true);
                if (_world is not null)
                {
                    _world.Speed = _speedBeforePause;
                }
            },
            Save: () => TrySave(),
            Settings: () => OpenSettings(window => _screen?.ShowWindow(window)),
            MainMenu: () =>
            {
                _pause = null;
                LeaveWorld();
            },
            Quit: () =>
            {
                _pause = null;
                SaveAndQuit();
            })
        {
            Share = _meta is null || _library is null ? null : ShareWorld,

            // A world without a save place (screenshot runs) never claims "saved" (W1689b).
            SaveNow = () => _world?.SavePath is not null && TrySave(),
            Load = _library is null ? null : () =>
            {
                _pause = null;
                LeaveWorld();
                if (_world is null)
                {
                    _menu?.OpenWorlds();
                }
            },
        });
        _screen.ShowWindow(_pause, modal: true);
    }

    private void OpenSettings(Action<GwWindow> show)
    {
        if (_limits is null || _mix is null || _style is null)
        {
            return;
        }

        var window = new SettingsWindow();
        window.Build(_settings, _limits, _mix, _style, (_menuData ?? LoadMenuData(_library!)).Guard);
        window.Changed += settings =>
        {
            // Applied at once (also audible for volumes); written when the window closes or the game quits.
            _settings = settings.Clamp(_limits);
            _settingsDirty = true;
            ApplySettings(_settings);

            // UI and text size change the logical screen; fit the window again after the change.
            Callable.From(() =>
            {
                if (IsInstanceValid(window) && window.IsInsideTree())
                {
                    window.FitInto(window.GetViewport().GetVisibleRect().Size);
                }
            }).CallDeferred();
        };
        window.Closed += SaveSettings;

        // The AI self-check (W1703): with a running world's service; in the main menu the calm "not running" text.
        var setup = _world?.SetupPort;
        window.AddAi(setup is null ? null : () => setup.CheckAsync(), setup is null ? null : () => setup.DiagnosticsAsync(), DiagnosticsFolder());
        show(window);
    }

    /// <summary>Where the player's copies of diagnostics reports lie (W1703): the game's own folder, which it can open.</summary>
    private static string DiagnosticsFolder() => System.IO.Path.Combine(OS.GetUserDataDir(), "diagnostics");

    /// <summary>
    /// Once per world (W1703 rule 2): if an AI needs the player, one calm hint that points to the settings.
    /// The world runs on either way (rule 3).
    /// </summary>
    private async void HintIfAnAiNeedsThePlayer()
    {
        if (_world?.SetupPort is not { } setup)
        {
            return;
        }

        var world = _world;
        var answer = await setup.CheckAsync();
        if (answer.Check is { } check && SelfCheckView.NeedsPlayer(check) && IsInstanceValid(this) && _world == world)
        {
            _screen?.ShowHint(new ToastMessage("msg.hint.selfcheck", new Dictionary<string, string>(), "selfcheck"));
        }
    }

    private void SaveSettings()
    {
        if (_settingsDirty && _settingsPath.Length > 0)
        {
            _settingsDirty = !SettingsStore.Save(_settingsPath, _settings);
        }
    }

    /// <summary>Saves the running world; on failure a calm hint, and false (B6 R3).</summary>
    private bool TrySave()
    {
        try
        {
            _world?.Save();
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            GD.PushWarning($"SAVE_FAILED {e.GetType().Name}: {e.Message}");
            _screen?.ShowHint(SaveFailedHint());
            return false;
        }
    }

    /// <summary>Quitting saves on every path (menu, window close, Alt+F4); a failed save keeps the game open.</summary>
    private void SaveAndQuit()
    {
        SaveSettings();
        if (TrySave())
        {
            // Like the smoke: silence all sounds and quit a moment later, so the audio server lets go of
            // every playback first (no stream left at exit, #142).
            _audio?.Silence();
            GetTree().CreateTimer(0.1).Timeout += () => GetTree().Quit(0);
        }
    }

    private void LeaveWorld()
    {
        if (!TrySave())
        {
            if (_world is not null)
            {
                _world.Speed = _speedBeforePause;
            }

            return;
        }

        CloseWorld();
        ShowMenu();
    }

    /// <summary>Removes screen, audio and world; the presenter stops the AI service when it leaves the tree.</summary>
    private void CloseWorld()
    {
        _screen?.QueueFree();
        _audio?.QueueFree();
        _world?.QueueFree();
        _screen = null;
        _audio = null;
        _symbols = null;
        _world = null;
        _meta = null;
        _pause = null;
    }

    private static ToastMessage SaveFailedHint() => new("msg.save.failed", new Dictionary<string, string>(), null);
}
