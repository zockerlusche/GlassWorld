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

    /// <summary>The player wants a new world.</summary>
    public event Action<NewWorldChoice>? NewWorldRequested;

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

        var panel = new PanelContainer { ThemeTypeVariation = UiTheme.WindowPanel };
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
        _loading = new PanelContainer { ThemeTypeVariation = UiTheme.WindowPanel };
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
            ShowWindow(NewWorldWindow(_data, _style, choice => NewWorldRequested?.Invoke(choice)));
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
        message.AddAction("ui.common.close").Pressed += message.Close;
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
    public static GwWindow NewWorldWindow(MenuData data, UiStyle style, Action<NewWorldChoice> create)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(style);
        var window = new GwWindow();
        window.Setup("ui.new_world.title", style);
        var name = new LineEdit { MaxLength = data.Options.NameMaxLength, PlaceholderText = "ui.new_world.name_hint" };
        // The seed is drawn at once and shown, so the world code exists before the world (B2534).
        var seed = new LineEdit { PlaceholderText = "ui.new_world.seed_hint", Text = NewWorldModel.DrawSeed(DateTimeOffset.UtcNow).ToString(CultureInfo.InvariantCulture) };
        var code = new LineEdit { PlaceholderText = "ui.new_world.code_hint" };
        var copy = new Button { Text = "ui.common.copy" };
        var size = Options(data.Options.Sizes.Select(s => s.Id), "ui.new_world.size.", data.Options.DefaultSize);
        var climate = Options(data.Options.Climates.Select(c => c.Id), "ui.new_world.climate.", data.Options.DefaultClimate);
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
        Row(window, "ui.new_world.name", name);
        Row(window, "ui.new_world.code", codeRow);
        Row(window, "ui.new_world.seed", seed);
        Row(window, "ui.new_world.size", size);
        Row(window, "ui.new_world.climate", climate);
        Row(window, "ui.new_world.start_favor", favor);
        window.Content.AddChild(problem);
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

            var choice = new NewWorldChoice(
                name.Text.Trim(),
                seedValue ?? NewWorldModel.DrawSeed(DateTimeOffset.UtcNow),
                size.GetItemMetadata(size.Selected).AsString(),
                climate.GetItemMetadata(climate.Selected).AsString(),
                favor.GetItemMetadata(favor.Selected).AsInt32());
            window.Close();
            create(choice);
        };
        name.CallDeferred(Control.MethodName.GrabFocus);
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

    private static void Row(GwWindow window, string key, Control control)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = key, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(control);
        window.Content.AddChild(row);
    }
}
