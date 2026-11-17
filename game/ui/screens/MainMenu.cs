using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Safety;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>Data the main menu needs: options and limits from content, the player's worlds.</summary>
/// <param name="Worlds">The player's worlds (user://worlds).</param>
/// <param name="Options">Size, climate and name limit (content/player/world_options.json).</param>
/// <param name="Favor">Start favour options (content/player/favor.json).</param>
/// <param name="Guard">Content guard for the world name (R13).</param>
public sealed record MenuData(WorldLibrary Worlds, WorldOptions Options, FavorSettings Favor, TextGuard Guard)
{
    /// <summary>Sharing a world as one file (content/player/sharing.json); null hides "Open a world".</summary>
    public WorldSharing? Sharing { get; init; }

    /// <summary>
    /// A1771 (B2805): generates the island of a choice and its picture (called off the main thread); null = no preview
    /// (the layout check).
    /// </summary>
    public Func<NewWorldChoice, (GlassWorld.Simulation.Map.PreviewIsland Island, PreviewImage Image)>? Preview { get; init; }

    /// <summary>A1774: the character value ids a start inhabitant can be designed with (content/inhabitants/model.json).</summary>
    public IReadOnlyList<string> Traits { get; init; } = [];
}

/// <summary>
/// Main menu (B6 R3): continue the last world, new world (name, seed, size, climate, start favour;
/// options as data), list of worlds, settings, quit. Loading and saving themselves come from the core
/// (B1); this screen only raises events.
/// </summary>
public partial class MainMenu : CanvasLayer
{
    private readonly Control _root = new();
    private readonly VBoxContainer _buttons = new();
    private MenuData? _data;
    private UiStyle? _style;
    private GwWindow? _window;
    private PanelContainer? _loading;
    private PanelContainer? _menuBox;

    /// <summary>The player wants a new world.</summary>
    public event Action<NewWorldChoice, PreviewShown?>? NewWorldRequested;

    /// <summary>The player wants to play a saved world.</summary>
    public event Action<WorldMeta>? LoadRequested;

    /// <summary>The player opens the settings.</summary>
    public event Action? SettingsRequested;

    /// <summary>The player quits the game.</summary>
    public event Action? QuitRequested;

    /// <summary>The window currently open over the menu (checks, screenshots).</summary>
    public GwWindow? OpenWindow => _window;

    /// <summary>Builds the menu.</summary>
    public void Setup(MenuData data, UiStyle style)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _style = style ?? throw new ArgumentNullException(nameof(style));
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_root);
        UiSetup.Bind(_root);

        // B2791: the menu's box on the same dark ground as every window.
        var panel = new PanelContainer { ThemeTypeVariation = UiTheme.WindowGround, Theme = UiTheme.BuildWindowOnWorld(style) };
        _menuBox = panel;
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        _buttons.AddChild(new Label { Text = "ui.menu.title", ThemeTypeVariation = UiTheme.SubheaderLabel, HorizontalAlignment = HorizontalAlignment.Center });
        panel.AddChild(_buttons);
        _root.AddChild(panel);
        Rebuild();
    }

    /// <summary>Rebuilds the buttons (e.g. after a world was saved, so "continue" appears).</summary>
    public void Rebuild()
    {
        if (_data is null)
        {
            return;
        }

        _loading?.QueueFree();
        _loading = null;
        if (_buttons.GetParent() is Control menu)
        {
            menu.Visible = true;
        }

        foreach (var child in _buttons.GetChildren().Skip(1))
        {
            _buttons.RemoveChild(child);
            child.QueueFree();
        }

        var worlds = _data.Worlds.List();
        if (worlds.Count > 0)
        {
            var latest = worlds[0];
            Add("ui.menu.continue", () => LoadRequested?.Invoke(latest)).TooltipText = latest.Name;
        }

        Add("ui.menu.new_world", OpenNewWorld);
        Add("ui.menu.worlds", OpenWorlds).Disabled = worlds.Count == 0;
        if (_data.Sharing is not null)
        {
            Add("ui.menu.open_shared", OpenShared);
        }
        Add("ui.menu.settings", () => SettingsRequested?.Invoke());
        Add("ui.menu.quit", () => QuitRequested?.Invoke());
        _buttons.GetChild<Button>(1).CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>
    /// Answers a click on a world at once (W1688: "dass zumindest, wenn man irgendwo geklickt hat, dass eine
    /// Rückmeldung kommt"): a calm "loading" window over the menu, the buttons locked until the world is open
    /// or loading failed (then <see cref="Rebuild"/> frees them).
    /// </summary>
    public void ShowLoading(string worldName)
    {
        if (_style is null)
        {
            return;
        }

        foreach (var button in _buttons.GetChildren().OfType<Button>())
        {
            button.Disabled = true;
        }

        // A small notice in the middle, alone on the screen (W1726, picture D659: the window stood tall and
        // half empty over the menu): the menu behind it is hidden until the world is open or loading failed.
        _window?.Close();
        if (_buttons.GetParent() is Control menu)
        {
            menu.Visible = false;
        }

        _loading?.QueueFree();
        _loading = new PanelContainer { ThemeTypeVariation = UiTheme.WindowGround, Theme = UiTheme.BuildWindowOnWorld(_style) };
        _loading.SetAnchorsPreset(Control.LayoutPreset.Center);
        _loading.GrowHorizontal = Control.GrowDirection.Both;
        _loading.GrowVertical = Control.GrowDirection.Both;
        _loading.AddChild(new Label
        {
            Text = TextFill.Fill(Tr("ui.menu.loading"), new Dictionary<string, string> { ["name"] = worldName }),
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _root.AddChild(_loading);
    }

    /// <summary>Shows a window over the menu (also used for the settings).</summary>
    public void ShowWindow(GwWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window?.Close();
        _window = window;
        window.Closed += () =>
        {
            if (_window == window)
            {
                _window = null;
            }
        };
        _root.AddChild(window);
        window.FitInto(GetViewport().GetVisibleRect().Size);
    }

    /// <summary>Opens the new-world form.</summary>
    public void OpenNewWorld()
    {
        if (_data is not null && _style is not null)
        {
            ShowWindow(NewWorldWindow(_data, _style, (choice, shown) => NewWorldRequested?.Invoke(choice, shown)));
        }
    }

    /// <summary>
    /// Opens a world someone shared (F5234 C): a file dialog for the shared world file; the file is
    /// checked (untrusted) and becomes a new world in the list, never replacing one.
    /// </summary>
    private void OpenShared()
    {
        var sharing = _data!.Sharing!;
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Filters = ["*" + sharing.Extension],
            Title = Tr("ui.menu.open_shared"),
        };
        dialog.FileSelected += path =>
        {
            dialog.QueueFree();
            string key;
            var args = new Dictionary<string, string>();
            try
            {
                var options = new WorldShareOptions(
                    _data.Options.Sizes.Select(o => o.Id).ToHashSet(StringComparer.Ordinal),
                    _data.Options.Climates.Select(o => o.Id).ToHashSet(StringComparer.Ordinal),
                    _data.Favor.StartOptions.ToHashSet());
                var world = WorldShare.Import(_data.Worlds, path, sharing, options, DateTimeOffset.UtcNow);
                key = "msg.share.opened";
                args["name"] = world.Name;
            }
            catch (WorldShareRefusedException e)
            {
                // The reason as a DE/EN text, never text or names from the file (review GW1 #138).
                key = "msg.share.refused";
                args["reason"] = Tr(e.ReasonKey);
                GD.PushWarning($"shared world refused: {e.ReasonKey}");
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                key = "msg.share.refused";
                args["reason"] = Tr("msg.share.reason.not_zip");
                GD.PushWarning($"shared world could not be read: {e.Message}");
            }

            Rebuild(); // "Continue" and the world list know the new world
            Notice(key, args);
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    private void Notice(string key, IReadOnlyDictionary<string, string> args)
    {
        if (_style is null)
        {
            return;
        }

        var message = new GwWindow();
        message.Setup("ui.menu.open_shared", _style);
        message.Content.AddChild(new Label
        {
            Text = TextFill.Fill(Tr(key), args),
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        ShowWindow(message);
    }

    /// <summary>Opens the list of saved worlds.</summary>
    public void OpenWorlds()
    {
        if (_data is not null && _style is not null)
        {
            ShowWindow(WorldsWindow(_data, _style, world => LoadRequested?.Invoke(world)));
        }
    }

    /// <summary>Builds the new-world form (also for the layout check in all reference sizes).</summary>
    public static GwWindow NewWorldWindow(MenuData data, UiStyle style, Action<NewWorldChoice, PreviewShown?> create)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(style);
        var window = new GwWindow();
        window.Setup("ui.new_world.title", style);
        var name = new LineEdit { MaxLength = data.Options.NameMaxLength, PlaceholderText = "ui.new_world.name_hint" };
        // The seed is drawn at once and shown, so the world code exists before the world (B2534).
        var seed = new LineEdit { PlaceholderText = "ui.new_world.seed_hint", Text = NewWorldModel.DrawSeed(DateTimeOffset.UtcNow).ToString(CultureInfo.InvariantCulture) };
        var code = new LineEdit { PlaceholderText = "ui.new_world.code_hint" };
        // Wide enough for their hint texts in the language and text size chosen (D705 A5: "Name der Wel"),
        // at most a share of the picture, so the window still fits the smallest screen with the largest text.
        foreach (var field in new[] { name, seed, code })
        {
            UiFit.HintFits(field, style.Tokens.Window.FieldMinWidth, (float)style.Tokens.Window.FieldMaxShare);
        }

        var copy = new Button { Text = "ui.common.copy" };
        var size = Options(data.Options.Sizes.Select(s => s.Id), "ui.new_world.size.", data.Options.DefaultSize);
        var climate = Options(data.Options.Climates.Select(c => c.Id), "ui.new_world.climate.", data.Options.DefaultClimate);

        // A1770 (B2805 'Einfach'): how much grows and lies on the island, and how many pairs start; from the data.
        var resources = Options((data.Options.Resources ?? []).Select(r => r.Id), "ui.new_world.resources.", data.Options.DefaultResources ?? "normal");

        // A1833 (B2805 'Erweitert'): the island's terrain and how things lie on it.
        var terrain = Options((data.Options.Terrains ?? []).Select(t => t.Id), "ui.new_world.terrain.", data.Options.DefaultTerrain ?? "hilly");
        var distribution = Options((data.Options.Distributions ?? []).Select(t => t.Id), "ui.new_world.distribution.", data.Options.DefaultDistribution ?? "normal");
        var pairs = Options((data.Options.StartPairs ?? []).Select(p => "pairs_" + p.ToString(CultureInfo.InvariantCulture)), "ui.new_world.start_pairs.", "pairs_" + (data.Options.DefaultStartPairs ?? 2).ToString(CultureInfo.InvariantCulture));
        var favor = new OptionButton();
        foreach (var option in data.Favor.StartOptions)
        {
            favor.AddItem(option.ToString(CultureInfo.InvariantCulture));
            favor.SetItemMetadata(favor.ItemCount - 1, option);
        }

        // Preselection: the middle option, until favor.json names a default.
        favor.Selected = data.Favor.StartOptions.Count / 2;
        var problem = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        var updating = false;
        void ShowCode()
        {
            // Code of the current choice; an empty or broken seed leaves the field empty.
            if (updating)
            {
                return;
            }

            var (ok, value) = NewWorldModel.ParseSeed(seed.Text);
            updating = true;
            code.Text = ok && value is { } s
                ? WorldCode.Encode(new WorldStart(s, size.GetItemMetadata(size.Selected).AsString(), climate.GetItemMetadata(climate.Selected).AsString(), favor.GetItemMetadata(favor.Selected).AsInt32()), data.Options, data.Favor)
                : string.Empty;
            updating = false;
        }

        void TakeCode(string text)
        {
            // A pasted code sets seed, size, climate and start favour; the name stays the player's own.
            if (updating || !WorldCode.TryDecode(text, data.Options, data.Favor, out var start) || start is null)
            {
                return;
            }

            updating = true;
            seed.Text = start.Seed.ToString(CultureInfo.InvariantCulture);
            Select(size, start.Size);
            Select(climate, start.Climate);
            Select(favor, start.StartFavor);
            updating = false;
            ShowCode();
        }

        seed.TextChanged += _ => ShowCode();
        size.ItemSelected += _ => ShowCode();
        climate.ItemSelected += _ => ShowCode();
        favor.ItemSelected += _ => ShowCode();
        code.TextChanged += TakeCode;
        copy.Pressed += () => DisplayServer.ClipboardSet(code.Text);
        ShowCode();
        var codeRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        code.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        codeRow.AddChild(code);
        codeRow.AddChild(copy);
        // A1771 (B2805): 'Einfach' - the few choices most players make - and the island they give; 'Erweitert' (folded)
        // holds seed, world code and start favour (later also the terrain controls, A1833, and the start inhabitants, A1774).
        Row(window.Content, "ui.new_world.name", name);
        Row(window.Content, "ui.new_world.size", size);
        Row(window.Content, "ui.new_world.climate", climate);
        if (resources.ItemCount > 0)
        {
            Row(window.Content, "ui.new_world.resources", resources);
        }

        if (pairs.ItemCount > 0)
        {
            Row(window.Content, "ui.new_world.start_pairs", pairs);
        }

        var preview = new TextureRect
        {
            Name = "IslandPreview",
            CustomMinimumSize = new Vector2(style.Tokens.Window.PreviewPx, style.Tokens.Window.PreviewPx),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
        };
        var busy = new ProgressBar { Name = "PreviewBusy", ShowPercentage = false, Indeterminate = true, Visible = false };
        var previewBox = new VBoxContainer();
        previewBox.AddChild(preview);
        previewBox.AddChild(busy);
        if (data.Preview is not null)
        {
            Row(window.Content, "ui.new_world.preview", previewBox);
        }

        var advanced = new FoldableContainer { Name = "Advanced", Title = "ui.new_world.advanced", Folded = true };
        var advancedRows = new VBoxContainer();
        advanced.AddChild(advancedRows);
        if (terrain.ItemCount > 0)
        {
            Row(advancedRows, "ui.new_world.terrain", terrain);
        }

        if (distribution.ItemCount > 0)
        {
            Row(advancedRows, "ui.new_world.distribution", distribution);
        }

        // A1774 (B2805): design the start inhabitants or leave them to the seed - in a window of its own.
        IReadOnlyList<GlassWorld.Simulation.State.StartInhabitantSpec>? designed = null;
        var designButton = new Button { Name = "DesignInhabitants", Text = "ui.new_world.inhabitants_random" };
        designButton.Pressed += () =>
        {
            var count = (pairs.ItemCount > 0 ? int.Parse(pairs.GetItemMetadata(pairs.Selected).AsString()["pairs_".Length..], CultureInfo.InvariantCulture) : 2) * 2;
            var design = DesignWindow(data, style, count, designed, chosen =>
            {
                designed = chosen;
                designButton.Text = chosen is null ? "ui.new_world.inhabitants_random" : "ui.new_world.inhabitants_designed";
            });
            window.GetParent().AddChild(design);
            design.FitInto(window.GetViewport().GetVisibleRect().Size);
        };
        if (data.Traits.Count > 0)
        {
            Row(advancedRows, "ui.new_world.inhabitants", designButton);
        }

        Row(advancedRows, "ui.new_world.code", codeRow);
        Row(advancedRows, "ui.new_world.seed", seed);
        Row(advancedRows, "ui.new_world.start_favor", favor);
        window.Content.AddChild(advanced);
        window.Content.AddChild(problem);

        // The preview: the real generator off the main thread, some time after the last change; an older island that is
        // still being made when the player changes again is dropped. A quiet bar shows only when it takes a while.
        PreviewShown? shown = null;
        var latest = new LatestOnly<(GlassWorld.Simulation.Map.PreviewIsland Island, PreviewImage Image)>();
        var running = false;
        var again = false;
        var debounce = new Godot.Timer { OneShot = true, WaitTime = style.Tokens.Window.PreviewDebounceMs / 1000.0, Autostart = data.Preview is not null };
        var slow = new Godot.Timer { OneShot = true, WaitTime = Math.Max(0.01, style.Tokens.Window.PreviewProgressAfterMs / 1000.0) };
        window.AddChild(debounce);
        window.AddChild(slow);
        slow.Timeout += () => busy.Visible = running;
        async Task RefreshAsync()
        {
            // At most one island is generated at a time (review GW3 #586): a change during a run only marks that one more
            // run is due, which starts with the newest choice when the running one ends.
            if (running)
            {
                again = true;
                return;
            }

            var (ok, value) = NewWorldModel.ParseSeed(seed.Text);
            if (data.Preview is not { } make || !ok || value is not { } s)
            {
                return;
            }

            var asked = Chosen(s);
            running = true;
            again = false;
            slow.Start();
            try
            {
                var (isLatest, made) = await latest.RunAsync(() => make(asked));
                if (!isLatest || !GodotObject.IsInstanceValid(preview))
                {
                    return;
                }

                shown = new PreviewShown(asked, made.Island);
                preview.Texture = ImageTexture.CreateFromImage(Image.CreateFromData(made.Image.Width, made.Image.Height, false, Image.Format.Rgb8, made.Image.Rgb));
            }
            catch (InvalidOperationException e)
            {
                GD.PushWarning($"NEW_WORLD_PREVIEW failed: {e.Message}");
            }
            catch (InvalidDataException e)
            {
                GD.PushWarning($"NEW_WORLD_PREVIEW failed: {e.Message}");
            }
            catch (ArgumentException e)
            {
                GD.PushWarning($"NEW_WORLD_PREVIEW failed: {e.Message}");
            }
            catch (Contracts.Validation.ContractViolationException e)
            {
                // Also the first content load inside the preview maker (its Lazy keeps the failure: no preview then).
                GD.PushWarning($"NEW_WORLD_PREVIEW failed: {e.Message}");
            }
            finally
            {
                running = false;
                if (GodotObject.IsInstanceValid(busy))
                {
                    busy.Visible = again;
                    if (again)
                    {
                        _ = RefreshAsync();
                    }
                }
            }
        }

        debounce.Timeout += () => _ = RefreshAsync();
        void Changed()
        {
            if (data.Preview is not null && debounce.IsInsideTree())
            {
                debounce.Start();
            }
        }

        seed.TextChanged += _ => Changed();
        code.TextChanged += _ => Changed();
        size.ItemSelected += _ => Changed();
        resources.ItemSelected += _ => Changed();
        pairs.ItemSelected += _ => Changed();
        terrain.ItemSelected += _ => Changed();
        distribution.ItemSelected += _ => Changed();
        window.AddAction("ui.common.cancel").Pressed += window.Close;
        window.AddAction("ui.new_world.create").Pressed += () =>
        {
            var nameProblem = NewWorldModel.NameProblem(name.Text, data.Options, data.Guard);
            var (seedOk, seedValue) = NewWorldModel.ParseSeed(seed.Text);
            var key = nameProblem ?? (seedOk ? null : "ui.new_world.problem.seed");
            if (key is not null)
            {
                problem.Text = key;
                problem.Visible = true;
                window.FitInto(window.GetViewport().GetVisibleRect().Size);
                return;
            }

            var choice = Chosen(seedValue ?? NewWorldModel.DrawSeed(DateTimeOffset.UtcNow));
            window.Close();
            create(choice, shown);
        };

        NewWorldChoice Chosen(ulong seedValue) =>
            new(
                name.Text.Trim(),
                seedValue,
                size.GetItemMetadata(size.Selected).AsString(),
                climate.GetItemMetadata(climate.Selected).AsString(),
                favor.GetItemMetadata(favor.Selected).AsInt32(),
                resources.ItemCount > 0 ? resources.GetItemMetadata(resources.Selected).AsString() : null,
                pairs.ItemCount > 0 ? int.Parse(pairs.GetItemMetadata(pairs.Selected).AsString()["pairs_".Length..], CultureInfo.InvariantCulture) : null,
                terrain.ItemCount > 0 ? terrain.GetItemMetadata(terrain.Selected).AsString() : null,
                distribution.ItemCount > 0 ? distribution.GetItemMetadata(distribution.Selected).AsString() : null,
                designed);
        name.CallDeferred(Control.MethodName.GrabFocus);
        return window;
    }

    /// <summary>
    /// A1774 (B2805 'Erweitert'): the start inhabitants to design - per inhabitant whether to design it, its name, sex and
    /// character values (a share of the data's range, 0..1); what is not designed is drawn from the seed. Names go through
    /// the content guard (R13). <paramref name="done"/> gets the design, or null for "all random".
    /// </summary>
    public static GwWindow DesignWindow(MenuData data, UiStyle style, int count, IReadOnlyList<GlassWorld.Simulation.State.StartInhabitantSpec>? current, Action<IReadOnlyList<GlassWorld.Simulation.State.StartInhabitantSpec>?> done)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(done);
        var window = new GwWindow { Name = "DesignWindow" };
        window.Setup("ui.new_world.design_title", style);
        // Wide enough for a label and its control side by side, tall enough for one designed inhabitant (shots 17.11.).
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(style.Tokens.Window.FieldMinWidth * 2, style.Tokens.Window.TableMinHeight * 2),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(list);
        window.Content.AddChild(scroll);
        var rows = new List<(CheckBox Design, LineEdit Name, OptionButton Sex, List<(string Id, HSlider Slider)> Traits)>();
        for (var i = 0; i < count; i++)
        {
            var had = current is not null && i < current.Count ? current[i] : null;
            var designedBefore = had is not null && (had.Name is not null || had.Sex is not null || had.Traits is not null);
            var section = new FoldableContainer
            {
                Title = TextFill.Fill(TranslationServer.Translate("ui.new_world.inhabitant_n"), new Dictionary<string, string> { ["n"] = (i + 1).ToString(CultureInfo.InvariantCulture) }),
                Folded = i > 0 && !designedBefore,
            };
            var body = new VBoxContainer();
            section.AddChild(body);
            var design = new CheckBox { Text = "ui.new_world.design_this", ButtonPressed = designedBefore };
            body.AddChild(design);
            var name = new LineEdit { MaxLength = data.Options.PlayerNameMaxLength, PlaceholderText = "ui.new_world.name_random", Text = had?.Name ?? string.Empty };
            Row(body, "ui.new_world.inhabitant_name", name);
            var sex = new OptionButton();
            sex.AddItem("ui.new_world.sex_random");
            sex.SetItemMetadata(0, string.Empty);
            sex.AddItem("ui.new_world.sex_female");
            sex.SetItemMetadata(1, "female");
            sex.AddItem("ui.new_world.sex_male");
            sex.SetItemMetadata(2, "male");
            sex.Selected = had?.Sex switch { "female" => 1, "male" => 2, _ => 0 };
            Row(body, "ui.new_world.inhabitant_sex", sex);
            var traits = new List<(string Id, HSlider Slider)>();
            foreach (var id in data.Traits)
            {
                var slider = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05, Value = had?.Traits is { } t && t.TryGetValue(id, out var v) ? v : 0.5 };
                Row(body, VocabKeys.Trait(id), slider);
                traits.Add((id, slider));
            }

            void Enable()
            {
                name.Editable = design.ButtonPressed;
                sex.Disabled = !design.ButtonPressed;
                foreach (var (_, slider) in traits)
                {
                    slider.Editable = design.ButtonPressed;
                }
            }

            design.Toggled += _ => Enable();
            Enable();
            list.AddChild(section);
            rows.Add((design, name, sex, traits));
        }

        var problem = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        window.Content.AddChild(problem);
        window.AddAction("ui.common.cancel").Pressed += window.Close;
        window.AddAction("ui.new_world.design_all_random").Pressed += () =>
        {
            window.Close();
            done(null);
        };
        window.AddAction("ui.new_world.design_apply").Pressed += () =>
        {
            var chosen = new List<GlassWorld.Simulation.State.StartInhabitantSpec>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (design, name, sex, traits) in rows)
            {
                if (!design.ButtonPressed)
                {
                    chosen.Add(new GlassWorld.Simulation.State.StartInhabitantSpec());
                    continue;
                }

                var text = name.Text.Trim();
                // The shape of the name data (2-16 letters, capital first) and the content guard; each name once (review GW3 #598).
                var bad = text.Length > 0
                    && (NewWorldModel.InputProblem(text, data.Options.PlayerNameMaxLength, data.Guard) is not null
                        || !GlassWorld.Simulation.Content.ContentSet.IsProperName(text) || !names.Add(text));
                if (bad)
                {
                    problem.Text = "ui.new_world.problem.inhabitant_name";
                    problem.Visible = true;
                    window.FitInto(window.GetViewport().GetVisibleRect().Size);
                    return;
                }

                var sexId = sex.GetItemMetadata(sex.Selected).AsString();
                chosen.Add(new GlassWorld.Simulation.State.StartInhabitantSpec(
                    text.Length > 0 ? text : null,
                    sexId.Length > 0 ? sexId : null,
                    traits.ToDictionary(t => t.Id, t => t.Slider.Value, StringComparer.Ordinal)));
            }

            window.Close();
            done(chosen.Any(c => c.Name is not null || c.Sex is not null || c.Traits is not null) ? chosen : null);
        };
        return window;
    }

    /// <summary>Builds the list of saved worlds (also for the layout check).</summary>
    public static GwWindow WorldsWindow(MenuData data, UiStyle style, Action<WorldMeta> load)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(style);
        var worlds = data.Worlds.List();
        var window = new GwWindow();
        window.Setup("ui.worlds.title", style);
        var table = new GwTable { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, style.Tokens.Window.TableMinHeight) };
        table.Setup(
            [new GwColumn("ui.worlds.column.name", false), new GwColumn("ui.worlds.column.day", true), new GwColumn("ui.worlds.column.seed", true)],
            "ui.worlds.empty");
        table.SetRows([.. worlds.Select(w => new GwRow(
            w.Id,
            [w.Name, w.Day.ToString(CultureInfo.InvariantCulture), w.Seed.ToString(CultureInfo.InvariantCulture)],
            [0, w.Day, w.Seed]))]);
        string? selected = worlds.Count > 0 ? worlds[0].Id : null;
        table.RowSelected += id => selected = id;
        window.Content.AddChild(table);
        window.AddAction("ui.common.cancel").Pressed += window.Close;
        var copyProblem = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        window.Content.AddChild(copyProblem);
        window.AddAction("ui.worlds.copy_code").Pressed += () =>
        {
            var world = worlds.FirstOrDefault(w => w.Id == selected);
            if (world is null)
            {
                return;
            }

            // The seed comes from the save game (the truth); size, climate and favour from meta.json.
            // Unknown values (older worlds, edited files) give a calm message instead of nothing.
            try
            {
                var seed = Simulation.Data.SaveGameStore.Load(data.Worlds.SavePath(world.Id)).Seed;
                DisplayServer.ClipboardSet(WorldCode.Encode(new WorldStart(seed, world.Size, world.Climate, world.StartFavor), data.Options, data.Favor));
                copyProblem.Visible = false;
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                copyProblem.Text = "ui.worlds.no_code";
                copyProblem.Visible = true;
            }
        };
        window.AddAction("ui.worlds.load").Pressed += () =>
        {
            var world = worlds.FirstOrDefault(w => w.Id == selected);
            if (world is not null)
            {
                window.Close();
                load(world);
            }
        };
        return window;
    }

    private Button Add(string key, Action pressed)
    {
        var button = new Button { Text = key };
        button.Pressed += pressed;
        _buttons.AddChild(button);
        return button;
    }

    private static OptionButton Options(IEnumerable<string> ids, string keyPrefix, string selected)
    {
        var options = new OptionButton();
        foreach (var id in ids)
        {
            options.AddItem(keyPrefix + id);
            options.SetItemMetadata(options.ItemCount - 1, id);
            if (id == selected)
            {
                options.Selected = options.ItemCount - 1;
            }
        }

        return options;
    }

    private static void Select(OptionButton options, object id)
    {
        for (var i = 0; i < options.ItemCount; i++)
        {
            if (options.GetItemMetadata(i).ToString() == id.ToString())
            {
                options.Selected = i;
                return;
            }
        }
    }

    private static void Row(GwWindow window, string key, Control control) => Row(window.Content, key, control);

    private static void Row(Container parent, string key, Control control)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = key, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(control);
        parent.AddChild(row);
    }

    public override void _EnterTree() => UiSetup.ThemeApplied += OnThemeApplied;

    public override void _ExitTree() => UiSetup.ThemeApplied -= OnThemeApplied;

    // A new style (text size, high contrast) reaches the menu's dark box too (B2791).
    private void OnThemeApplied(Theme theme)
    {
        if (_menuBox is not null && UiSetup.Current is { } style)
        {
            _menuBox.Theme = UiTheme.BuildWindowOnWorld(style);
        }
    }
}
