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
            return
            [
                ("new world", MainMenu.NewWorldWindow(data, style, _ => { })),
                ("worlds", MainMenu.WorldsWindow(data, style, _ => { })),
                ("settings", settings),
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
            var problems = BaseSoundCheck.DataProblems(library, mapped)
                .Concat(BaseSoundCheck.FileProblems(library, name =>
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

        // Feeling symbols (B2541): the icon sheet and every symbol above the inhabitants.
        if (_symbols is not null)
        {
            var sheet = System.IO.Path.Combine(_shotsFolder!, "symbols_sheet.png");
            GD.Print($"UI_SHOT {sheet} {_symbols.IconSheet().SavePng(sheet)}");
            _symbols.ShowAllForShots();

            // A close look: no windows, the camera near the first inhabitants.
            _screen!.Visible = false;
            if (GetViewport().GetCamera3D() is { } camera && _world!.GetNodeOrNull<Node3D>(_world.Host!.Inhabitants.All.First().Id) is { } figure)
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
        }

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

            var knock = _screen is null ? "game screen missing" : await _screen.CheckKnockAsync();
            if (knock.Length > 0)
            {
                problems = [.. problems, "knock: " + knock];
            }
            else
            {
                GD.Print("KNOCK_OK");
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
