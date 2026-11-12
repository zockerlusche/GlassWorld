using GlassWorld.Contracts;
using GlassWorld.Game.Audio;
using GlassWorld.Game.Localization;
using GlassWorld.Game.Ui;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Game.Ui.Screens;
using GlassWorld.Presentation.Audio;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game;

/// <summary>Smoke and screenshot runs of the start scene (marker lines for tools/godot-smoke.sh).</summary>
public partial class Main
{
    private const string DebugOverlayName = "DebugOverlay";
    private bool _uiCheckDone = true;
    /// <summary>Frames the audio server gets to let go of the stopped music before quitting (measured: 1 is too few headless).</summary>
    private const int SilenceFrames = 30;

    private int _silenceFrames = -1;

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_smokeFrames == 0)
        {
            return;
        }

        _frame++;
        if (_frame >= _smokeFrames && _uiCheckDone)
        {
            // A few frames for the audio server to let go of the music before quitting (no leaks at exit, review #92).
            if (_silenceFrames < 0)
            {
                _audio?.Silence();
                _silenceFrames = SilenceFrames;
            }

            if (--_silenceFrames > 0)
            {
                return;
            }

            GD.Print($"WORLD_TICKS {_world?.Host?.World.Tick ?? -1}");
            GD.Print($"SMOKE_OK frames={_frame}");
            GetTree().Quit(0);
            _smokeFrames = 0;
        }
    }

    private void StartChecks(bool textsLoaded, bool audioLoaded, bool spatialReady)
    {
        CheckTexts(textsLoaded);
        CheckAudio(audioLoaded);
        CheckSpatial(spatialReady);
        CheckSoundLibrary();
        var symbols = _symbols?.SelfCheck() ?? "feeling symbols not set up";
        if (symbols.Length == 0 && _symbols is not null)
        {
            symbols = _symbols.ScreenSizeCheck();
        }

        if (symbols.Length > 0)
        {
            GD.PushError($"SYMBOLS_FAIL {symbols}");
        }
        else
        {
            GD.Print("SYMBOLS_OK");
        }

        _uiCheckDone = false;
        _ = CheckUiAsync(_style);
    }

    /// <summary>
    /// Smoke run only, after the screen check (ticks have run, a click opened a question): saving the
    /// running world and loading it into a second presenter gives the same state hash (ADR-0003,
    /// acceptance B1) - compared before open questions are sent again. Also proves SQLite in Godot.
    /// The check always uses the mock port, so no second AI service starts.
    /// </summary>
    private void CheckSave(string content)
    {
        var folder = ProjectSettings.GlobalizePath("user://smoke");
        System.IO.Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, "world.sqlite");
        var host = _world!.Host!;
        var state = Simulation.State.StateCodec.Capture(host);
        Simulation.Data.SaveGameStore.Save(path, state);
        var loaded = new WorldPresenter { Name = "LoadCheck" };
        try
        {
            loaded.Load(content, path, new Simulation.Decisions.MockDecisionPort());
            var expected = host.World.StateHash();
            var actual = loaded.HashAtLoad ?? string.Empty;
            if (actual != expected)
            {
                GD.PushError($"SAVE_FAIL hash after load {actual} != {expected}");
                return;
            }

            GD.Print($"SAVE_OK hash={actual[..12]} tick={state.Tick} questions={state.Questions.Count}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            GD.PushError($"SAVE_FAIL {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            loaded.Free();
        }
    }

    /// <summary>
    /// Smoke run only (B6 acceptance 7 and 11): main menu, new world form, world list, settings and
    /// pause menu open in DE and EN without a raw key and inside the safe area; settings survive a
    /// restart (written, read back, equal).
    /// </summary>
    private async Task<IReadOnlyList<string>> CheckMenusAsync()
    {
        var problems = new List<string>();
        if (_style is null || _limits is null || _mix is null)
        {
            return ["menu data not loaded"];
        }

        var folder = ProjectSettings.GlobalizePath("user://smoke");
        // One saved world (reused on later runs), so "continue" and the world list have content.
        var library = new WorldLibrary(System.IO.Path.Combine(folder, "worlds"));
        var meta = library.List().FirstOrDefault(w => w.Id == "smoke")
            ?? library.Create(new NewWorldChoice("Smoke", _world!.Host!.World.Seed, "small", "temperate", 50), DateTimeOffset.UnixEpoch);
        Simulation.Data.SaveGameStore.Save(library.SavePath(meta.Id), Simulation.State.StateCodec.Capture(_world!.Host!));

        var language = TranslationServer.GetLocale();
        var checkedScreens = 0;
        foreach (var locale in TextCatalogs.SupportedLocales)
        {
            TextCatalogs.SetLanguage(locale);
            var menu = new MainMenu { Name = "MenuCheck" };
            AddChild(menu);
            menu.Setup(LoadMenuData(library), _style);
            var screens = new (string Name, Action Open)[]
            {
                ("main menu", () => { }),
                ("new world", menu.OpenNewWorld),
                ("worlds", menu.OpenWorlds),
                ("settings", () => OpenSettings(menu.ShowWindow)),
                ("pause", () => menu.ShowWindow(PauseMenu.Create(_style, new PauseActions(() => { }, () => { }, () => { }, () => { }, () => { })))),
            };
            foreach (var (name, open) in screens)
            {
                open();
                problems.AddRange(UiSetup.UnthemedCanvasRoots(GetTree().Root).Select(path => $"{locale} {name}: no UI theme below a CanvasLayer at {path}"));
                for (var i = 0; i < 3; i++)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                var area = GetViewport().GetVisibleRect().Size;
                problems.AddRange(RawKeys(menu).Select(key => $"{locale} {name}: raw key '{key}'"));
                if (menu.OpenWindow is { } window && !window.IsInsideSafeArea(area))
                {
                    problems.Add($"{locale} {name}: window {window.Position}+{window.Size} leaves the safe area of {area}");
                }

                // No hint text cut, no word broken inside (D705 A5/A6, review GW1 #226).
                if (menu.OpenWindow is { } shown)
                {
                    problems.AddRange(UiFit.CutTexts(shown).Select(cut => $"{locale} {name}: {cut}"));
                }

                checkedScreens++;
            }

            menu.OpenWindow?.Close();
            menu.Free();

            // The game screen too (review #76: the favour tooltip showed its key).
            if (_screen is not null)
            {
                problems.AddRange(RawKeys(_screen).Select(key => $"{locale} game screen: raw key '{key}'"));
            }
        }

        TextCatalogs.SetLanguage(language);

        var path = System.IO.Path.Combine(folder, "settings.json");
        var written = _settings with { UiScale = 1.5, TextScale = 1.25, HighContrast = true, HintDurationFactor = 3, BusVolumesDb = new(StringComparer.Ordinal) { ["music"] = -12 } };
        SettingsStore.Save(path, written);
        if (SettingsStore.Load(path, _limits).ToJson() != written.Clamp(_limits).ToJson())
        {
            problems.Add("settings changed on the way through the settings file");
        }

        if (problems.Count == 0)
        {
            GD.Print($"MENU_OK screens={checkedScreens}");
        }

        return problems;
    }

    /// <summary>The menu, settings and pause windows for one style (layout check in all reference sizes).</summary>
    private Func<UiStyle, IReadOnlyList<(string Name, GwWindow Window)>>? MenuWindows()
    {
        if (_limits is null || _mix is null)
        {
            return null;
        }

        var data = LoadMenuData(new WorldLibrary(System.IO.Path.Combine(ProjectSettings.GlobalizePath("user://smoke"), "worlds")));
        return style =>
        {
            var settings = new SettingsWindow();
            settings.Build(_settings, _limits, _mix, style, data.Guard);

            // The AI self-check with one of each kind of line (W1703), so the layout check covers its long texts.
            var check = new SelfCheck { ContractVersion = ContractInfo.Version };
            check.Checks.Add(new AiCheck { Ai = AiCheckAi.Language, State = AiCheckState.Ok, Reason = "ready", MessageKey = "msg.selfcheck.language.ready" });
            var nodes = new AiCheck { Ai = AiCheckAi.Image, State = AiCheckState.Problem, Reason = "nodes_missing", MessageKey = "msg.selfcheck.image.nodes_missing", FixKey = "msg.selfcheck.image.nodes_missing.fix" };
            nodes.Detail.Add("UnetLoaderGGUF");
            check.Checks.Add(nodes);
            check.Checks.Add(new AiCheck { Ai = AiCheckAi.Sound, State = AiCheckState.Not_planned, Reason = "not_planned", MessageKey = "msg.selfcheck.sound.not_planned" });

            // The switch "learn by day as well" with its long hint (A1568), so the layout check covers it too.
            check.LearnByDay = new LearnByDayOffer { Offered = true, Reason = LearnByDayOfferReason.Ok, MessageKey = "msg.selfcheck.learn_by_day.ok", HintKey = "msg.selfcheck.learn_by_day.hint" };
            settings.AddAi(() => Task.FromResult(new GlassWorld.AiClient.SelfCheckAnswer(check, null)), null, ProjectSettings.GlobalizePath("user://smoke/diagnostics"), () => false, _ => { });

            // Every tab is laid out in every case: one window per tab.
            SettingsWindow Tab(string key)
            {
                var window = new SettingsWindow();
                window.Build(_settings, _limits, _mix, style, data.Guard);
                window.AddAi(() => Task.FromResult(new GlassWorld.AiClient.SelfCheckAnswer(check, null)), null, ProjectSettings.GlobalizePath("user://smoke/diagnostics"), () => false, _ => { });
                window.ShowTab(key);
                return window;
            }

            settings.ShowTab("ui.settings.section.player");
            return
            [
                ("new world", MainMenu.NewWorldWindow(data, style, _ => { })),
                ("worlds", MainMenu.WorldsWindow(data, style, _ => { })),
                ("settings", settings),
                ("settings display", Tab("ui.settings.section.display")),
                ("settings hints", Tab("ui.settings.section.hints")),
                ("settings audio", Tab("ui.settings.section.audio")),
                ("settings ai", Tab("ui.settings.section.ai")),
                ("pause", PauseMenu.Create(style, new PauseActions(() => { }, () => { }, () => { }, () => { }, () => { }))),
            ];
        };
    }

    /// <summary>Visible texts below <paramref name="root"/> that are keys without a translation, or tooltip keys the control does not translate.</summary>
    private static IEnumerable<string> RawKeys(Node root)
    {
        var texts = new List<string>();
        var untranslated = new List<string>();
        foreach (var node in root.FindChildren("*", nameof(Control), recursive: true, owned: false).OfType<Control>().Where(c => c.IsVisibleInTree()))
        {
            switch (node)
            {
                case OptionButton options:
                    texts.AddRange(Enumerable.Range(0, options.ItemCount).Select(i => options.GetItemText(i)));
                    break;
                case Button button:
                    texts.Add(button.Text);
                    break;
                case Label label:
                    texts.Add(label.Text);
                    break;
                case LineEdit edit:
                    texts.Add(edit.PlaceholderText);
                    break;
            }

            texts.Add(node.TooltipText);

            // A tooltip key on a control that does not translate its tooltip shows raw (review #76).
            var tooltipTranslated = node.TooltipAutoTranslateMode switch
            {
                Node.AutoTranslateModeEnum.Always => true,
                Node.AutoTranslateModeEnum.Disabled => false,
                _ => node.CanAutoTranslate(),
            };
            if (TextKeys.LooksLikeKey(node.TooltipText) && !tooltipTranslated)
            {
                untranslated.Add(node.TooltipText);
            }
        }

        return texts.Where(t => TextKeys.LooksLikeKey(t) && root.Tr(t) == t).Concat(untranslated).Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// Smoke run only (B8-4): the base sound library is complete, licensed and every file meets the
    /// loudness and length targets with the recorded SHA-256 (CI has the LFS files only here).
    /// </summary>
    private void CheckSoundLibrary()
    {
        try
        {
            var library = Contracts.Validation.ContractSerializer.Deserialize<Contracts.BaseSoundLibrary>(
                System.IO.File.ReadAllText(System.IO.Path.Combine(_contentDirectory, "sound", "library.json")));
            using var map = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(_contentDirectory, "sound", "sound_map.json")));
            var mapped = map.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                .SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("sound").GetString()!))
                .ToList();
            var folder = ProjectSettings.GlobalizePath("res://assets/sound/base");

            // Exported builds carry only the imported sounds, not the source files: there the check asks
            // whether each sound loads; checksums and loudness of the sources are checked in the repository.
            var exported = !OS.HasFeature("editor");
            var problems = BaseSoundCheck.DataProblems(library, mapped)
                .Concat(exported
                    ? library.Sounds.SelectMany(s => s.Files).Where(f => !ResourceLoader.Exists("res://assets/sound/base/" + f.File)).Select(f => $"{f.File}: not in the export")
                    : BaseSoundCheck.FileProblems(library, name =>
                    {
                        var path = System.IO.Path.Combine(folder, name);
                        return System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
                    }))
                .ToList();
            foreach (var problem in problems)
            {
                GD.PushError($"SOUNDLIB_FAIL {problem}");
            }

            if (problems.Count == 0)
            {
                GD.Print($"SOUNDLIB_OK sounds={library.Sounds.Count} files={library.Sounds.Sum(s => s.Files.Count)}");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            GD.PushError($"SOUNDLIB_FAIL {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Smoke run only: every mapped sound renders to a stream of the right length (B8 R3).</summary>
    private void CheckSpatial(bool ready)
    {
        var problem = ready && _audio is not null ? _audio.SpatialSelfCheck() : "world audio not set up";
        if (problem.Length == 0 && _audio is not null)
        {
            problem = _audio.BaseSelfCheck();
        }
        if (problem.Length > 0)
        {
            GD.PushError($"AUDIO_SPATIAL_FAIL {problem}");
            return;
        }

        GD.Print("AUDIO_SPATIAL_OK");

        var limiter = WorldAudio.LimiterSelfCheck();
        if (limiter.Length > 0)
        {
            GD.PushError($"AUDIO_LIMITER_FAIL {limiter}");
            return;
        }

        GD.Print("AUDIO_LIMITER_OK");

        var ambience = _audio?.AmbienceSelfCheck() ?? "world audio not set up";
        if (ambience.Length > 0)
        {
            GD.PushError($"AMBIENCE_FAIL {ambience}");
            return;
        }

        GD.Print("AMBIENCE_OK");

        var music = _audio?.MusicSelfCheck() ?? "world audio not set up";
        if (music.Length > 0)
        {
            GD.PushError($"MUSIC_FAIL {music}");
            return;
        }

        GD.Print("MUSIC_OK");

        var animals = _audio?.AnimalSelfCheck() ?? "world audio not set up";
        if (animals.Length > 0)
        {
            GD.PushError($"ANIMALS_FAIL {animals}");
            return;
        }

        GD.Print("ANIMALS_OK");
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

        var murmur = MurmurAudio.SelfCheck();
        if (murmur.Length > 0)
        {
            GD.PushError($"MURMUR_FAIL {murmur}");
            return;
        }

        GD.Print("MURMUR_OK");
    }

    /// <summary>Screenshot run (self-acceptance, KR5): saves the shots, then quits.</summary>
    private async Task TakeShotsAsync(UiStyle style)
    {
        _world!.Speed = 0;
        var files = await UiShots.RunAsync(this, _screen!, style, _shotsFolder!);
        GD.Print($"UI_SHOTS_OK {files.Count}");

        // The pause menu as the player sees it, then after pressing "Save" (W1687, W1689b).
        OpenPause();
        if (_pause is { } pause)
        {
            await Frames(6);
            Shot("pause.png");
            if (pause.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.Text == "ui.pause.save") is { } save)
            {
                save.EmitSignal(BaseButton.SignalName.Pressed);
                await Frames(6);
                Shot("pause_saved.png");
            }

            pause.Close();
            await Frames(2);
        }

        // Talking (A1342): the card with its talk field, a sentence and the inhabitant's first reaction
        // (at the start it understands little: a gesture or a question mark, D333).
        var talker = _world!.Host!.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First().Id;
        _world.Host.Conversation.Port ??= new Simulation.Player.MockConversationPort();
        if (!_world.CloseLook(talker, 8) && GetViewport().GetCamera3D() is { } talkCamera && _world.GetNodeOrNull<Node3D>(talker) is { } talkFigure)
        {
            // Placeholder world: the camera close to the inhabitant's head, the bubble above it.
            _world.CameraHeld = true;
            var head = talkFigure.GlobalPosition + new Vector3(0, 1.1f, 0);
            talkCamera.GlobalPosition = head + new Vector3(1.2f, 0.6f, 3.2f);
            talkCamera.LookAt(head + new Vector3(0, 0.4f, 0), Vector3.Up);
        }

        foreach (var (locale, sentence) in new[] { ("de", "Hallo! Wie geht es dir?"), ("en", "Hello! How are you?") })
        {
            TextCatalogs.SetLanguage(locale);
            _screen!.Say(talker, sentence);
            _screen.ShowTalkForShots(sentence);
            for (var i = 0; i < 3; i++)
            {
                _world.Host.World.Step();
            }

            for (var i = 0; i < 20; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            _screen.Refresh();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var talk = System.IO.Path.Combine(_shotsFolder!, $"talk_{locale}.png");
            GD.Print($"UI_SHOT {talk} {GetViewport().GetTexture().GetImage().SavePng(talk)}");
        }

        // Feeling symbols (B2541): the icon sheet and every symbol above the inhabitants.
        if (_symbols is not null)
        {
            var sheet = System.IO.Path.Combine(_shotsFolder!, "symbols_sheet.png");
            GD.Print($"UI_SHOT {sheet} {_symbols.IconSheet().SavePng(sheet)}");
            _symbols.ShowAllForShots();

            // A close look: no windows, the camera near the first inhabitants.
            _screen!.Visible = false;
            var first = _world!.Host!.Inhabitants.All.First().Id;
            if (_world.CloseLook(first, 12))
            {
                // Island: the camera rig follows the figure closely.
            }
            else if (GetViewport().GetCamera3D() is { } camera && _world.GetNodeOrNull<Node3D>(first) is { } figure)
            {
                var head = figure.GlobalPosition + new Vector3(0, 1.1f, 0);
                camera.GlobalPosition = head + new Vector3(0.6f, 0.1f, 1.6f);
                camera.LookAt(head, Vector3.Up);
            }

            for (var i = 0; i < 20; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var world = System.IO.Path.Combine(_shotsFolder!, "symbols_world.png");
            GD.Print($"UI_SHOT {world} {GetViewport().GetTexture().GetImage().SavePng(world)}");

            // From the usual distance (F5284): the symbols stay readable, not only close up.
            if (GetViewport().GetCamera3D() is { } far && _world!.GetNodeOrNull<Node3D>(_world.Host!.Inhabitants.All.First().Id) is { } seen)
            {
                var head = seen.GlobalPosition + new Vector3(0, 1.1f, 0);
                far.GlobalPosition = head + new Vector3(0, 14, 38);
                far.LookAt(head, Vector3.Up);
                for (var i = 0; i < 10; i++)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var farShot = System.IO.Path.Combine(_shotsFolder!, "symbols_far.png");
                GD.Print($"UI_SHOT {farShot} {GetViewport().GetTexture().GetImage().SavePng(farShot)}");
            }
        }

        GetTree().Quit(0);
    }

    /// <summary>
    /// The UI tour (Marcel W1757, B2603: agents look at the running game themselves): opens every menu and
    /// window once, in the layout so far and in the zones preview, and saves a picture of each into the shots
    /// folder (German, the size of the window as started). The pictures are the basis of the list of what can
    /// get better. Then quits. By default the world is stepped 600 ticks at once (stand-in AI);
    /// <c>--tour-run-s=SECONDS</c> lets it run that long in fast-forward instead, which is what the island
    /// world with the real AI service needs (<c>GLASSWORLD_WORLD=island</c>, <c>GLASSWORLD_AI=live</c>).
    /// </summary>
    private async Task TourAsync(UiStyle style)
    {
        TextCatalogs.SetLanguage("de");
        var size = GetWindow().Size;
        var prefix = $"{size.X}x{size.Y}_";
        var (count, failed) = (0, 0);
        DirAccess.MakeDirRecursiveAbsolute(_shotsFolder!);
        async Task Take(string name)
        {
            await Frames(12);
            _screen?.Refresh();
            await Frames(2);
            if (Shot(prefix + name + ".png"))
            {
                count++;
            }
            else
            {
                failed++;
            }
        }

        // Whatever happens on the way, the run ends and says how it went (review GW1 #214): no window left open.
        var ok = false;
        try
        {
            // Let the world live a little, so lists, log, questions and chronicle have something to show.
            var live = string.Equals(System.Environment.GetEnvironmentVariable("GLASSWORLD_AI"), "live", StringComparison.Ordinal);
            GD.Print($"UI_TOUR_START world={_world!.Mode} live={(live ? 1 : 0)} run_s={_tourRunS.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            _world.Host!.Conversation.Port ??= new Simulation.Player.MockConversationPort();
            if (_tourRunS > 0)
            {
                // As a player sees it (A1516: island world, real AI): the world runs in fast-forward for
                // real seconds, so the AI service answers on its own time and its lines reach the screen.
                _world.Speed = ScreenModel.TimeChoices(_world.Host.World.Calendar.Settings).Max(c => c.Speed);
                var until = Time.GetTicksMsec() + (ulong)(_tourRunS * 1000);
                while (Time.GetTicksMsec() < until)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                // The pictures show the game by day: if the run ended inside the compute night (the scene
                // darkens everything, first island run L854), the world runs on until the morning.
                var morningBy = Time.GetTicksMsec() + 600_000;
                var waited = false;
                while (_screen!.NightNow.Stage != NightStage.Day && Time.GetTicksMsec() < morningBy)
                {
                    waited = true;
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                if (waited)
                {
                    GD.Print($"UI_TOUR_NIGHT waited for the morning, stage now {_screen.NightNow.Stage}");
                }

                if (_screen.NightNow.Stage != NightStage.Day)
                {
                    // Dark pictures are no result: the run fails rather than report them as good (review GW1 #238).
                    throw new InvalidOperationException($"the compute night did not end within ten minutes (stage {_screen.NightNow.Stage})");
                }
            }
            else
            {
                for (var i = 0; i < 600; i++)
                {
                    _world.Host.World.Step();
                }
            }

            _world.Speed = 0;
            var first = _world.Host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First().Id;

            // 1. Windows of the main menu (shown over the game: the menu itself has no world behind it).
            if (_library is not null)
            {
                var menu = new MainMenu { Name = "TourMenu" };
                AddChild(menu);
                menu.Setup(LoadMenuData(_library), style);
                await Take("01_hauptmenue");
                menu.OpenNewWorld();
                await Take("02_neue_welt");
                menu.OpenWorlds();
                await Take("03_welten_laden");
                menu.ShowLoading("Meine Welt");
                await Take("04_lade_hinweis");
                menu.QueueFree();
                await Frames(2);
            }

            // 2. The game screen in both layouts: every side window, the card, the pause menu.
            foreach (var (zones, tag) in new[] { (false, "alt"), (true, "neu") })
            {
                _screen!.ShowZonesForShots(zones);
                await Take($"10_{tag}_spiel");
                foreach (var key in GameScreen.PanelKeysForShots(zones))
                {
                    _screen.OpenPanelForShots(key);
                    await Take($"11_{tag}_{key}");
                }

                _screen.OpenPanelForShots(null);
                _screen.ShowCardForShots(first, expand: false);
                await Take($"12_{tag}_karte");
                _screen.ShowCardForShots(first, expand: true);
                await Take($"13_{tag}_karte_aufgeklappt");

                // The memory page of the same inhabitant (B8 R16), once per layout.
                var memory = _screen.OpenMemory(first);
                await Take($"13_{tag}_erinnerungen");
                memory?.Close();
                _screen.ShowCardForShots(null, expand: false);

                OpenPause();
                await Take($"14_{tag}_pause");
                _pause?.Close();
                await Frames(2);
            }

            // 3. The settings, every tab (opened from the game, as the pause menu does).
            _screen!.ShowZonesForShots(false);
            SettingsWindow? settings = null;
            OpenSettings(window =>
            {
                settings = window as SettingsWindow;
                _screen.ShowWindow(window, modal: true);
            });
            if (settings is not null)
            {
                foreach (var tab in new[] { "player", "language", "display", "hints", "audio", "ai" })
                {
                    settings.ShowTab("ui.settings.section." + tab);
                    await Take($"20_einstellungen_{tab}");
                }

                settings.Close();
            }

            ok = failed == 0 && count > 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            GD.PushError($"UI_TOUR_FAIL {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            GD.Print(ok ? $"UI_TOUR_OK {count}" : $"UI_TOUR_FAIL pictures={count} failed={failed}");
            GetTree().Quit(ok ? 0 : 1);
        }
    }

    /// <summary>
    /// The compute night in pictures (B7-S8, acceptance B7-S10): runs the world in fast-forward until the
    /// night begins, then at normal speed through the night scene, and saves a picture at each stage - the
    /// fade-out, the dream (again whenever more dream images show), the fade-in and the morning after.
    /// With <c>GLASSWORLD_AI=live</c> the scene follows the real service; without it only the fades show.
    /// Prints <c>NIGHT_SHOTS_OK n stages=…</c> and quits; a stage that never came within the limit
    /// (<c>--night-limit=SECONDS</c>, real time) is a failure. Headless it is a dry run: the stages are
    /// walked and reported, no pictures are saved. A run with a window belongs in the AMW run queue
    /// (<c>amw lauf start --schirm --gpu --warten</c>).
    /// </summary>
    private async Task NightShotsAsync()
    {
        TextCatalogs.SetLanguage("de");
        var size = GetWindow().Size;
        var prefix = $"{size.X}x{size.Y}_nacht_";
        var (count, failed) = (0, 0);
        var seen = new List<string>();
        var ok = false;
        var reason = "limit reached";

        // Without a window nothing is drawn: the run then only walks the stages (a dry run), no pictures.
        var dry = DisplayServer.GetName() == "headless";
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(_shotsFolder!);
            var live = string.Equals(System.Environment.GetEnvironmentVariable("GLASSWORLD_AI"), "live", StringComparison.Ordinal);
            GD.Print($"NIGHT_SHOTS_START live={(live ? 1 : 0)} world={_world!.Mode} limit_s={_nightLimitS.ToString(System.Globalization.CultureInfo.InvariantCulture)} content={(ContentLocator.Variant is null ? "repository" : "variant")}");
            var fastest = ScreenModel.TimeChoices(_world.Host!.World.Calendar.Settings).Max(c => c.Speed);
            _world.Speed = fastest;
            var started = Time.GetTicksMsec();
            var last = NightStage.Day;
            var dreams = 0;
            var wasNight = false;
            var nextReport = 0.0;
            var (calendarNight, sawDay) = (false, false);
            while ((Time.GetTicksMsec() - started) / 1000.0 < _nightLimitS)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var (stage, darkness) = _screen!.NightNow;
                var nowS = (Time.GetTicksMsec() - started) / 1000.0;
                if (nowS >= nextReport)
                {
                    // A line every few seconds, so a run that waits in vain shows where it stands.
                    nextReport = nowS + 10;
                    var at = _world.Host.World.Calendar.At(_world.Host.World.Tick);
                    GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"NIGHT_SHOTS_AT s={nowS:0} tick={_world.Host.World.Tick} phase={at.Phase} speed={_world.Speed} stage={stage} darkness={darkness:0.00} night={_world.Host.Snapshot().ComputeNight}"));
                }

                // The night is watched at the speed a player sees it; once the scene is over the rest of the
                // calendar night runs in fast-forward again (it took seven minutes of waiting otherwise).
                var wanted = stage is NightStage.Day or NightStage.Awake ? fastest : 1;
                if (_world.Speed != wanted)
                {
                    _world.Speed = wanted;
                }

                var shown = _screen.DreamsShownForShots;
                var name = stage == last
                    ? stage == NightStage.Dream && shown > dreams ? $"traum_{shown}_bilder" : null
                    : stage switch
                    {
                        NightStage.FadingOut => "1_abblenden",
                        NightStage.Dream => "2_traum",
                        NightStage.FadingIn => "3_aufblenden",
                        NightStage.Awake => "4_wach",
                        _ => wasNight ? "5_tag" : null,
                    };
                wasNight |= stage != NightStage.Day;
                last = stage;

                // A calendar night that ends without the scene: the core called no compute night (it does so
                // only with an AI service to compute for). Waiting longer shows nothing new.
                var phase = _world.Host.World.Calendar.At(_world.Host.World.Tick).Phase;
                calendarNight |= phase == GlassWorld.Simulation.Time.DayPhase.Night && _world.Host.World.Tick > 0 && sawDay;
                sawDay |= phase == GlassWorld.Simulation.Time.DayPhase.Day;
                if (calendarNight && phase == GlassWorld.Simulation.Time.DayPhase.Day && !wasNight)
                {
                    reason = "a night passed without a night scene (no compute night: is the AI service running? GLASSWORLD_AI=live)";
                    break;
                }

                dreams = Math.Max(dreams, shown);
                if (name is null)
                {
                    continue;
                }

                // A fade is caught in its middle, a standing stage once it has settled.
                for (var i = 0; i < (stage is NightStage.FadingOut or NightStage.FadingIn ? 45 : 20); i++)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"NIGHT_SHOT {name} stage={_screen.NightNow.Stage} darkness={_screen.NightNow.Darkness:0.00} dreams={_screen.DreamsShownForShots} text='{_screen.NightTextForShots}'"));
                // The name is the stage that triggered the picture; if the scene moved on during the wait,
                // the file says which stage it really shows (review GW1 #235).
                var now = _screen.NightNow.Stage;
                var changed = now == stage ? string.Empty : $"_zeigt_{now}";
                if (dry)
                {
                    seen.Add(name);
                }
                else if (await DrawnShot($"{prefix}{count + 1:00}_{name}{changed}.png"))
                {
                    count++;
                    seen.Add(name);
                }
                else
                {
                    failed++;
                }

                if (stage == NightStage.Day && wasNight)
                {
                    break;
                }
            }

            // The scene must have gone dark and come back; with the live service a dream stage belongs to it.
            ok = failed == 0 && seen.Contains("1_abblenden") && seen.Contains("5_tag") && (!live || seen.Contains("2_traum"));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            GD.PushError($"NIGHT_SHOTS_FAIL {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            var stages = string.Join(',', seen);
            GD.Print(ok ? $"NIGHT_SHOTS_OK {count} stages={stages}{(dry ? " dry=1" : string.Empty)}" : $"NIGHT_SHOTS_FAIL pictures={count} failed={failed} stages={stages} reason='{reason}'");
            GetTree().Quit(ok ? 0 : 1);
        }
    }

    private async Task<bool> DrawnShot(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        return Shot(name);
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    private bool Shot(string name)
    {
        var path = System.IO.Path.Combine(_shotsFolder!, name);
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"UI_SHOT {path} {error}");
        return error == Error.Ok;
    }

    /// <summary>Smoke run only: visible controls that would take the mouse at <paramref name="point"/> (names, comma-separated).</summary>
    private string MouseCatchersAt(Vector2 point) =>
        string.Join(", ", GetTree().Root.FindChildren("*", nameof(Control), true, false).OfType<Control>()
            .Where(c => c.IsVisibleInTree() && c.MouseFilter != Control.MouseFilterEnum.Ignore && c.GetGlobalRect().HasPoint(point))
            .Select(c => c.GetPath().ToString()));

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

            // Open ground takes the mouse (review GW1 #158): the wheel zooms only where no control is hovered, so
            // nothing visible may catch the mouse in the middle of the picture.
            var catchers = MouseCatchersAt(GetViewport().GetVisibleRect().GetCenter());
            if (catchers.Length > 0)
            {
                GD.PushError($"UI_FAIL controls catch the mouse over open ground: {catchers}");
                return;
            }

            var layer = new CanvasLayer();
            AddChild(layer);
            var problems = await UiLayoutCheck.RunAsync(layer, style, ScreenModel.TimeChoices(_world!.Host!.World.Calendar.Settings), MenuWindows());
            var screen = _screen is null ? "game screen missing" : await _screen.SelfCheckAsync();
            if (screen.Length > 0)
            {
                problems = [.. problems, "game screen: " + screen];
            }
            else
            {
                GD.Print("SCREEN_OK");
            }

            // The dream images of the night scene (B7, D637 A8): shown from a made-up night list, none from an empty one.
            var dreamCheck = _screen?.CheckDreams() ?? "game screen missing";
            if (dreamCheck.Length > 0)
            {
                problems = [.. problems, "night scene: " + dreamCheck];
            }
            else
            {
                GD.Print("NIGHT_OK");
            }

            // The zones preview of the UI concept (W1717): switched on, checked, switched back.
            var zones = _screen is null ? "game screen missing" : await _screen.CheckZonesAsync(_settings);
            if (zones.Length > 0)
            {
                problems = [.. problems, "zones preview: " + zones];
            }
            else
            {
                GD.Print("ZONES_OK");
            }

            // The debug view and the time keys (B2-6 R31).
            var debug = CheckDebugOverlay();
            if (debug.Length > 0)
            {
                problems = [.. problems, "debug view: " + debug];
            }
            else
            {
                GD.Print("DEBUG_OVERLAY_OK");
            }

            // Camera keys (GW3 #144): a button that took the focus leaves them to the camera, a slider keeps them.
            var (button, slider) = (new Button(), new HSlider());
            if (GlassWorld.Game.World.CameraRig.BlocksCameraKeys(button) || !GlassWorld.Game.World.CameraRig.BlocksCameraKeys(slider))
            {
                problems = [.. problems, "camera keys: a focused button blocks them or a focused slider does not"];
            }

            button.Free();
            slider.Free();
            CheckSave(_contentDirectory);

            // After the save check: placing a gift runs more ticks (A1340).
            var gift = _screen is null ? "game screen missing" : await _screen.CheckGiftAsync();
            if (gift.Length > 0)
            {
                problems = [.. problems, "gift: " + gift];
            }
            else
            {
                GD.Print("GIFT_OK");
            }

            var chronicle = _screen is null ? "game screen missing" : await _screen.CheckChronicleAsync();
            if (chronicle.Length > 0)
            {
                problems = [.. problems, "chronicle: " + chronicle];
            }
            else
            {
                GD.Print("CHRONICLE_OK");
            }

            var bubbles = _screen is null ? "game screen missing" : await _screen.CheckBubblesAsync();
            if (bubbles.Length > 0)
            {
                problems = [.. problems, "bubbles: " + bubbles];
            }

            var talk = _screen is null ? "game screen missing" : await _screen.CheckTalkAsync();
            if (talk.Length > 0)
            {
                problems = [.. problems, "talk: " + talk];
            }
            else
            {
                GD.Print("TALK_OK");
            }

            var knock = _screen is null ? "game screen missing" : await _screen.CheckKnockAsync();
            if (knock.Length > 0)
            {
                problems = [.. problems, "knock: " + knock];
            }
            else
            {
                GD.Print("KNOCK_OK");
            }

            // Island mode (TL #19505): figures, objects, camera and the UI anchored to them.
            if (_world.Mode == WorldLookMode.Island)
            {
                var island = _world.IslandSelfCheck();
                if (island.Length == 0 && _screen is not null)
                {
                    island = await _screen.CheckIslandAsync();
                }

                if (island.Length > 0)
                {
                    problems = [.. problems, "island: " + island];
                }
                else
                {
                    GD.Print($"ISLAND_UI_OK figures={_world.FigureViews} objects_drawn={_world.ObjectsDrawn}");
                }
            }

            problems = [.. problems, .. await CheckMenusAsync()];
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
    /// <summary>
    /// The debug view (B2-6 R31): off by default, toggled on it has lines in every language without a raw
    /// key and never takes the mouse; the time keys press the time buttons and the pause key toggles.
    /// </summary>
    private string CheckDebugOverlay()
    {
        if (_screen?.GetNodeOrNull<GwDebugOverlay>(DebugOverlayName) is not { } view || _world is null)
        {
            return "missing";
        }

        var problems = new List<string>();
        if (view.Visible)
        {
            problems.Add("shown before the first toggle");
        }

        view.Toggle();
        if (!view.Visible || view.MouseFilter != Control.MouseFilterEnum.Ignore)
        {
            problems.Add("not shown after the toggle, or it takes the mouse");
        }

        var language = TranslationServer.GetLocale();
        foreach (var locale in TextCatalogs.SupportedLocales)
        {
            TextCatalogs.SetLanguage(locale);
            view.Refresh();
            if (view.Lines.Count == 0 || view.Lines.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"{locale}: empty lines");
            }

            problems.AddRange(DebugOverlayModel.RawKeys(view.Lines).Select(key => $"{locale}: raw key '{key}'"));
        }

        TextCatalogs.SetLanguage(language);
        view.Toggle();
        if (view.Visible)
        {
            problems.Add("still shown after the second toggle");
        }

        // Without --debug the core refuses the debug jump.
        if (!_world.DebugCommands && view.StepEpoch(1))
        {
            problems.Add("an epoch step was accepted without debug commands");
        }

        var speed = _world.Speed;
        var speeds = new List<double>();
        foreach (var key in new[] { 2, 0, 0, 1 })
        {
            _screen.PressTimeKey(key);
            speeds.Add(_world.Speed);
        }

        _world.Speed = speed;
        if (speeds[0] <= 1 || speeds[1] != 0 || speeds[2] != speeds[0] || speeds[3] != 1)
        {
            problems.Add($"time keys fast, pause, pause, normal gave the speeds {string.Join(' ', speeds.Select(s => s.ToString(System.Globalization.CultureInfo.InvariantCulture)))}");
        }

        return string.Join("; ", problems);
    }

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
}
