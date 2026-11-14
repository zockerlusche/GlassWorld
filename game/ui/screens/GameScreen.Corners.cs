using GlassWorld.Contracts;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>
/// The corners of Variante A (A1682, D957 v4 section 1 and step 1, B2643), the standard layout: top left a round
/// portrait per inhabitant with name and what it does; top right the time block (speeds as symbols, season, date,
/// time, weather, the favour bar) with five round buttons (menu, questions, mail, chronicle, inhabitants); below
/// it on the right the one place for the window a button opens or the card; the log at the bottom left; the
/// middle stays free. The old layout stays as a fallback until step 3 (TL #23224).
/// </summary>
public partial class GameScreen
{
    private readonly PanelContainer _corner = new() { Name = "Corner", Visible = false, ThemeTypeVariation = UiTheme.CompactPanel };
    private readonly HBoxContainer _cornerSpeed = new() { Name = "Speeds" };
    private readonly HBoxContainer _cornerDate = new() { Name = "Date" };
    private readonly HBoxContainer _cornerFavor = new() { Name = "Favor" };
    private readonly HBoxContainer _cornerButtons = new() { Name = "Buttons", Alignment = BoxContainer.AlignmentMode.End };
    private readonly GwIcon _seasonIcon = new() { Name = "Season", MouseFilter = Control.MouseFilterEnum.Stop, TooltipAutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
    private readonly GwIcon _weatherIcon = new() { Name = "Weather", MouseFilter = Control.MouseFilterEnum.Stop, TooltipAutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
    private readonly ProgressBar _favorBar = new() { Name = "FavorBar", ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Stop, TooltipText = "ui.favor.tooltip", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
    private readonly GwRoundButton _menuRound = new() { Name = "CornerMenu" };
    private readonly GwPortraitList _portraits = new() { Visible = false };
    private readonly Dictionary<string, Texture2D> _portraitPictures = new(StringComparer.Ordinal);
    private readonly Queue<string> _portraitWanted = new();
    private readonly HashSet<string> _portraitAsked = new(StringComparer.Ordinal);
    private HFlowContainer? _topRow;
    private bool _portraitBusy;

    /// <summary>The portrait list top left (for the UI tour).</summary>
    public GwPortraitList PortraitsForShots => _portraits;

    /// <summary>Builds the corners once (Setup): the round buttons in place of the theme buttons, the time block, the portraits.</summary>
    private void BuildCorners()
    {
        var px = _style!.Tokens.Zones.RoundButtonPx;
        var column = new VBoxContainer { Name = "Column" };
        column.AddChild(_cornerSpeed);
        column.AddChild(_cornerDate);
        column.AddChild(_cornerFavor);
        column.AddChild(_cornerButtons);
        _corner.AddChild(column);
        _cornerSpeed.Alignment = BoxContainer.AlignmentMode.End;
        _cornerDate.Alignment = BoxContainer.AlignmentMode.End;
        _cornerFavor.Alignment = BoxContainer.AlignmentMode.End;

        // Menu first: on, save, load, settings, help and gifts, main menu, quit (TL #23224: help and gifts move into it).
        _menuRound.Setup(UiIconChoiceIcon.Menu, "ui.top.menu_tooltip", px);
        _menuRound.Pressed += () => PauseRequested?.Invoke();
        _cornerButtons.AddChild(_menuRound);

        var icon = new Vector2(px * 0.6f, px * 0.6f);
        _seasonIcon.CustomMinimumSize = icon;
        _weatherIcon.CustomMinimumSize = icon;
        _favorBar.CustomMinimumSize = new Vector2(_style.Tokens.Zones.FavorBarWidth, px * 0.3f);

        _root.AddChild(_corner);
        _root.AddChild(_portraits);
        _portraits.Setup(_style);
        _portraits.Chosen += id =>
        {
            Encounter(id);
            _presenter?.ChooseFigure(id);
        };
        _portraits.PictureWanted += WantPortrait;
        _portraits.PictureOf = id => _portraitPictures.GetValueOrDefault(id);
    }

    /// <summary>
    /// Moves date, time, speeds and favour between the top bar of the old layout and the time block of the corners,
    /// so there is one of each (keys, pause menu and the night find the same buttons in both layouts).
    /// </summary>
    private void MoveClockParts(bool corners)
    {
        if (_topBar is null || _date is null || _time is null || _favor is null)
        {
            return;
        }

        _topRow ??= (HFlowContainer)_topBar.GetChild(0);
        if (corners)
        {
            Move(_seasonIcon, _cornerDate, 0);
            Move(_date, _cornerDate, 1);
            Move(_time, _cornerDate, 2);
            Move(_weatherIcon, _cornerDate, 3);
            for (var i = 0; i < _timeButtons.Count; i++)
            {
                Move(_timeButtons[i].Button, _cornerSpeed, i);
            }

            Move(_favorBar, _cornerFavor, 0);
            Move(_favor, _cornerFavor, 1);
            return;
        }

        // The old order: date, time, speeds, favour, menu (the corner's symbols wait outside the tree).
        Move(_date, _topRow, 0);
        Move(_time, _topRow, 1);
        for (var i = 0; i < _timeButtons.Count; i++)
        {
            Move(_timeButtons[i].Button, _topRow, 2 + i);
        }

        Move(_favor, _topRow, 2 + _timeButtons.Count);
        Move(_seasonIcon, _cornerDate, 0);
        Move(_weatherIcon, _cornerDate, 1);
        Move(_favorBar, _cornerFavor, 0);
    }

    private static void Move(Control part, Node to, int index)
    {
        if (part.GetParent() != to)
        {
            if (part.GetParent() is { } from)
            {
                from.RemoveChild(part);
            }

            to.AddChild(part);
        }

        to.MoveChild(part, Math.Min(index, to.GetChildCount() - 1));
    }

    /// <summary>The corners from the snapshot (Refresh): portraits, season and weather, favour bar, counts on the buttons.</summary>
    private void RefreshCorners(IReadOnlyList<InhabitantRow> rows, ClockView clock)
    {
        if (!_zones || _style is null || _feed is null || _activities is null)
        {
            return;
        }

        var unnamed = Tr("ui.inhabitant.unnamed");
        _portraits.Selected = _cardId;
        _portraits.SetRows([.. rows.Select(r => new PortraitEntry(r.Id, ScreenModel.DisplayName(r, unnamed), _activities[r.Id], CornerModel.Brooding(r)))]);

        var zones = _style.Tokens.Zones;
        var season = CornerModel.SeasonId(clock.SeasonKey);
        _seasonIcon.Icon = CornerModel.IconFor(zones.SeasonIcons, season);
        _seasonIcon.TooltipText = Tr(clock.SeasonKey);
        var weather = _feed.Snapshot.Weather?.Weather;
        _weatherIcon.Visible = weather is not null;
        if (weather is not null)
        {
            _weatherIcon.Icon = CornerModel.IconFor(zones.WeatherIcons, weather);
            _weatherIcon.TooltipText = TextFill.Fill(Tr("ui.corner.weather"), new Dictionary<string, string> { ["weather"] = Tr(VocabKeys.Weather(weather)) });
        }

        var (value, max) = _feed.Favor();
        _favorBar.MaxValue = Math.Max(1, max);
        _favorBar.Value = value;

        foreach (var (id, button) in _themeButtons)
        {
            if (button is GwRoundButton round)
            {
                round.Count = id switch
                {
                    "questions" => CornerModel.Badge(_feed.Questions().Count),
                    "mailbox" => CornerModel.Badge(_feed.Mail(0).Open.Count),
                    _ => string.Empty,
                };
            }
        }
    }

    /// <summary>A row in view has no still picture: render one, one figure at a time (none headless).</summary>
    private void WantPortrait(string id)
    {
        if (DisplayServer.GetName() == "headless" || !_portraitAsked.Add(id))
        {
            return;
        }

        _portraitWanted.Enqueue(id);
        if (!_portraitBusy)
        {
            _ = RenderPortraitsAsync();
        }
    }

    /// <summary>
    /// The still portraits (D957 v4 step 1, TL #23224: a still first, live in step 6): a fresh copy of the figure in
    /// its own small world, a camera on head and shoulders, rendered once into a picture; then the copy is gone.
    /// </summary>
    private async Task RenderPortraitsAsync()
    {
        _portraitBusy = true;
        try
        {
            while (_portraitWanted.TryDequeue(out var id))
            {
                if (_presenter?.PortraitFigure(id) is not { } figure || _style is null)
                {
                    continue;
                }

                var px = (int)Math.Ceiling(_style.Tokens.Zones.PortraitPx * 2 * (float)_style.ContentScaleFactor);
                var studio = new SubViewport
                {
                    Name = "PortraitStudio",
                    OwnWorld3D = true,
                    TransparentBg = true,
                    Size = new Vector2I(px, px),
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                };
                var camera = new Camera3D { Fov = 30, Current = true };
                studio.AddChild(figure);
                studio.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-35, 20, 0), LightEnergy = 1.1f });
                studio.AddChild(new WorldEnvironment { Environment = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0.6f } });
                studio.AddChild(camera);
                AddChild(studio);

                // The idle pose settles in a few frames; then the camera finds the head bone.
                for (var i = 0; i < 4; i++)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                var head = new Vector3(0, 1.6f, 0);
                var skeleton = figure.FindChildren("*", nameof(Skeleton3D), true, false).OfType<Skeleton3D>().FirstOrDefault();
                var bone = skeleton?.FindBone("DEF-head") ?? -1;
                if (skeleton is not null && bone >= 0)
                {
                    head = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone).Origin;
                }

                // Head and shoulders from the front (figures face +z, as in the look prototype's close-ups).
                var look = head + new Vector3(0, -0.02f, 0);
                camera.Position = look + new Vector3(0, 0.04f, 1.05f);
                camera.LookAt(look, Vector3.Up);
                for (var i = 0; i < 3; i++)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                var image = studio.GetTexture().GetImage();
                studio.QueueFree();
                if (image is null || image.IsEmpty())
                {
                    continue;
                }

                _portraitPictures[id] = ImageTexture.CreateFromImage(image);
                _portraits.Redraw();
            }
        }
        finally
        {
            _portraitBusy = false;
        }
    }

    /// <summary>
    /// The corners' layout (position and size only, trap f of GW3 #25014): time block top right, portraits top
    /// left, the open window (or card) on the right below the time block, the log bottom left. Nothing reaches
    /// into the rectangle in the middle (data: zones.middle_share).
    /// </summary>
    private void LayoutCorners(Vector2 area, float margin, float gap)
    {
        var log = _sideBar.PanelOf(ScreenZones.Pinned);
        var list = _sideBar.PanelOf("inhabitants");
        PlaceCorners(area, _style!, _corner, _portraits, log, [.. _sideBar.Panels.Where(p => p != log).Select(p => (p, p == list ? _inhabitants.WantedWidth : 0f))]);
        PlaceToasts();
        FitCard(area);
    }

    /// <summary>
    /// Places the corners (the game and the layout check use the same rule): time block top right, portraits top
    /// left, the windows on the right below the time block, the log bottom left below the portraits. The left parts
    /// stay left of the free middle and the right ones right of it (data: zones.middle_share).
    /// </summary>
    /// <param name="area">The screen (logical pixels).</param>
    /// <param name="style">The UI style.</param>
    /// <param name="corner">The time block.</param>
    /// <param name="portraits">The portrait list.</param>
    /// <param name="log">The log.</param>
    /// <param name="windows">The windows of the right-hand place, with the width their content asks for (0 = none).</param>
    public static void PlaceCorners(Vector2 area, UiStyle style, Control corner, GwPortraitList portraits, GwSidePanel log, IReadOnlyList<(GwSidePanel Panel, float ContentPx)> windows)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(corner);
        ArgumentNullException.ThrowIfNull(portraits);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(windows);
        var zones = style.Tokens.Zones;
        var margin = style.Tokens.Window.SafeMargin;
        var gap = style.Tokens.Window.Gap;
        var (midX, _, midW, _) = CornerModel.Middle(area.X, area.Y, zones.MiddleShare);
        var leftRoom = (float)midX - margin - gap;
        var rightRoom = area.X - (float)(midX + midW) - margin - gap;

        // A container never shrinks by itself (trap a of GW3 #25014): it takes its minimum before it is placed.
        corner.Size = corner.GetCombinedMinimumSize();
        corner.Position = new Vector2(area.X - margin - corner.Size.X, margin);
        var below = margin + corner.Size.Y + gap;

        var frame = portraits.GetThemeStylebox("panel") is { } box ? box.GetContentMargin(Side.Top) + box.GetContentMargin(Side.Bottom) : 0;
        var listHeight = Math.Min(portraits.ContentHeight + frame, (area.Y - (2 * margin)) * (float)zones.PortraitListHeightShare);
        portraits.Size = new Vector2(Math.Min(portraits.WantedWidth, leftRoom), listHeight);
        portraits.Position = new Vector2(margin, margin);

        foreach (var (panel, contentPx) in windows)
        {
            panel.Dock(area, style, 0, below, widthPx: zones.SidePanelWidth, contentPx: contentPx, maxWidth: rightRoom);
        }

        // The log at the bottom left, below the portraits (it gets its own corner shape in step 3).
        var logTop = Math.Max(margin + listHeight + gap, area.Y - margin - ((area.Y - (2 * margin)) * (float)zones.LogHeightShare));
        log.Dock(area, style, 0, logTop, left: true, widthPx: zones.LogWidth, maxWidth: leftRoom);
    }

    /// <summary>
    /// Layout check only: a time block as the corners build it - the speeds as symbols, season, date, time, weather,
    /// the favour bar with its value, the five round buttons with counts - filled with long sample texts.
    /// </summary>
    public static PanelContainer CornerSample(UiStyle style, IReadOnlyList<TimeChoice> choices, string date, string time, string favor)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(choices);
        var px = style.Tokens.Zones.RoundButtonPx;
        var (bar, _, _) = CreateTopBar(style, choices, _ => { });
        var block = new PanelContainer { Name = "CornerSample", ThemeTypeVariation = UiTheme.CompactPanel };
        var column = new VBoxContainer();
        var speeds = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        foreach (var button in bar.GetChild(0).GetChildren().OfType<Button>().ToList())
        {
            button.GetParent().RemoveChild(button);
            speeds.AddChild(button);
        }

        bar.Free();
        var icon = new Vector2(px * 0.6f, px * 0.6f);
        var dateRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        dateRow.AddChild(new GwIcon { Icon = UiIconChoiceIcon.Leaf, CustomMinimumSize = icon });
        dateRow.AddChild(new Label { Text = date, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled });
        dateRow.AddChild(new Label { Text = time, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled });
        dateRow.AddChild(new GwIcon { Icon = UiIconChoiceIcon.Storm, CustomMinimumSize = icon });
        var favorRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        favorRow.AddChild(new ProgressBar { ShowPercentage = false, Value = 40, CustomMinimumSize = new Vector2(style.Tokens.Zones.FavorBarWidth, px * 0.3f), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        favorRow.AddChild(new Label { Text = favor, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled });
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var menu = new GwRoundButton();
        menu.Setup(UiIconChoiceIcon.Menu, "ui.top.menu_tooltip", px);
        buttons.AddChild(menu);
        foreach (var theme in ScreenZones.Themes)
        {
            var round = new GwRoundButton();
            round.Setup(theme.Icon, theme.TitleKey, px);
            round.Count = CornerModel.Badge(120);
            buttons.AddChild(round);
        }

        column.AddChild(speeds);
        column.AddChild(dateRow);
        column.AddChild(favorRow);
        column.AddChild(buttons);
        block.AddChild(column);
        return block;
    }

    /// <summary>
    /// Smoke run only: the corners (A1682). Portraits top left, one per inhabitant; the time block top right with
    /// the five round buttons; each window button opens its window below it, the chronicle with its timeline tab, the
    /// card as a tab of the inhabitants; a portrait click opens that card; nothing overlaps and nothing covers the
    /// rectangle in the middle. Leaves the layout as <paramref name="settings"/> say. Empty when all holds.
    /// </summary>
    public async Task<string> CheckCornersAsync(PlayerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_style is null || _topBar is null || _feed is null)
        {
            return "screen not set up";
        }

        async Task Frames()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Refresh();
            Layout();
        }

        static Rect2 Box(Control c) => new(c.Position, c.Size);

        _card?.Close();
        ApplySettings(settings with { ClassicLayout = false }, _style);
        await Frames();
        try
        {
            var view = GetViewport().GetVisibleRect().Size;
            var (mx, my, mw, mh) = CornerModel.Middle(view.X, view.Y, _style.Tokens.Zones.MiddleShare);
            var middle = new Rect2((float)mx, (float)my, (float)mw, (float)mh);
            var log = _sideBar.PanelOf(ScreenZones.Pinned);
            var rows = _feed.Inhabitants();
            if (_topBar.Visible || _sideBar.Visible || !_corner.Visible || !_portraits.Visible || _portraits.Count != rows.Count)
            {
                return $"the corners are not shown (top bar {_topBar.Visible}, side bar {_sideBar.Visible}, portraits {_portraits.Count} of {rows.Count})";
            }

            if (_corner.Position.X < view.X / 2 || _portraits.Position.X > view.X / 2 || log.Position.X > view.X / 2 || log.Position.Y < view.Y / 2)
            {
                return $"a corner is not in its corner (time block {Box(_corner)}, portraits {Box(_portraits)}, log {Box(log)})";
            }

            if (_timeButtons.Any(b => !_cornerSpeed.IsAncestorOf(b.Button)) || !_cornerDate.IsAncestorOf(_date) || _cornerButtons.GetChildCount() != 1 + ScreenZones.Themes.Count)
            {
                return "the time block misses its speeds, its date or one of the five round buttons";
            }

            var questions = CornerModel.Badge(_feed.Questions().Count);
            if (((GwRoundButton)_themeButtons["questions"]).Count != questions)
            {
                return $"the questions button shows '{((GwRoundButton)_themeButtons["questions"]).Count}', the world has '{questions}'";
            }

            string Free(params (string Name, Control Part)[] parts)
            {
                var boxes = parts.Where(p => p.Part.Visible).Select(p => (p.Name, Box: Box(p.Part))).ToList();
                foreach (var (a, b) in boxes.SelectMany((a, i) => boxes.Skip(i + 1).Select(b => (a, b))))
                {
                    if (a.Box.Intersects(b.Box))
                    {
                        return $"{a.Name} {a.Box} overlaps {b.Name} {b.Box}";
                    }
                }

                return boxes.FirstOrDefault(b => b.Box.Intersects(middle)) is { Name: not null } inside
                    ? $"{inside.Name} {inside.Box} reaches into the middle {middle}"
                    : string.Empty;
            }

            var free = Free(("time block", _corner), ("portraits", _portraits), ("log", log));
            if (free.Length > 0)
            {
                return free;
            }

            // Every window button opens its window below the time block; the second press closes it.
            foreach (var theme in ScreenZones.Themes)
            {
                _themeButtons[theme.Id].EmitSignal(BaseButton.SignalName.Pressed);
                await Frames();
                var open = _sideBar.OpenKey is { } key ? _sideBar.PanelOf(key) : null;
                if (open is null || ScreenZones.ThemeOf(_sideBar.OpenKey) != theme)
                {
                    return $"the {theme.Id} button opened '{_sideBar.OpenKey}'";
                }

                free = Free(("time block", _corner), ("portraits", _portraits), ("log", log), ($"{_sideBar.OpenKey} window", open));
                if (free.Length > 0)
                {
                    return free;
                }

                if (theme.Id == "chronicle")
                {
                    var tabs = open.Tabs.GetChildren().OfType<Button>().ToList();
                    if (!open.Tabs.Visible || tabs.Count != 2)
                    {
                        return $"the chronicle window shows {tabs.Count} tabs";
                    }

                    tabs[1].EmitSignal(BaseButton.SignalName.Pressed);
                    await Frames();
                    if (_sideBar.OpenKey != ScreenZones.Timeline || _timelineList.GetChildCount() == 0)
                    {
                        return "the timeline tab did not open or is empty";
                    }
                }

                _themeButtons[theme.Id].EmitSignal(BaseButton.SignalName.Pressed);
                await Frames();
                if (_sideBar.OpenKey is not null)
                {
                    return $"the second press of {theme.Id} left '{_sideBar.OpenKey}' open";
                }
            }

            // Help and gifts from the menu stand in the same place.
            OpenHelp();
            await Frames();
            if (_sideBar.OpenKey != ScreenZones.FromMenu)
            {
                return "help and gifts did not open in the window's place";
            }

            // A click on a portrait opens that inhabitant's card in the same place.
            var who = rows[^1].Id;
            _portraits.RowButtons[^1].EmitSignal(BaseButton.SignalName.Pressed);
            await Frames();
            var cardPanel = _sideBar.PanelOf(ScreenZones.Card);
            if (_card is not null || _sideBar.OpenKey != ScreenZones.Card || _cardId != who || _talkInput is null || !cardPanel.IsAncestorOf(_talkInput))
            {
                return $"the portrait click did not open the card of {who} (open: '{_sideBar.OpenKey}', card of '{_cardId}')";
            }

            return Free(("time block", _corner), ("portraits", _portraits), ("log", log), ("card", cardPanel));
        }
        finally
        {
            _sideBar.Open(null);
            ApplySettings(settings, _style);
            _lastCardId = null;
        }
    }

    /// <summary>Help and gifts (the pause menu's entry, TL #23224): its window in the corner layout's place, or beside the side bar.</summary>
    public void OpenHelp()
    {
        _sideBar.Open(ScreenZones.FromMenu);
        Layout();
    }
}
