using System.Collections.Concurrent;
using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Decisions;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>
/// The game screen of B6 (R5, R6, R7, R12, R15; D366 section 1a): world clock and time control at the
/// top, the side bar at the right (inhabitants, open questions), the inhabitant card, question marks
/// over heads (B2487) and calm hints. It only shows what the core reports (WorldFeed) and sends the
/// player's interventions as commands the core checks; it decides nothing for an inhabitant.
/// </summary>
public partial class GameScreen : CanvasLayer
{
    private const string MarkNode = "QuestionMark";

    private readonly Control _root = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Control _overlay = new() { Name = "Overlay", MouseFilter = Control.MouseFilterEnum.Ignore };
    private int _relayoutFrames;
    private readonly GwSideBar _sideBar = new();
    private readonly VBoxContainer _themeBar = new() { Name = "ThemeBar", Visible = false };
    private readonly ColorRect _nightShade = new() { Name = "NightShade", Color = new Color(0, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly VBoxContainer _nightText = new() { Name = "NightText", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Label _nightProgress = new() { ThemeTypeVariation = UiTheme.OnDarkLabel, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
    private NightScene? _night;
    private DreamImages? _dreams;
    private readonly Dictionary<string, Control> _dreamNodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dreamFailed = new(StringComparer.Ordinal);
    private string _dreamFolder = string.Empty;

    // A1603: the textures of invention icons, by type, with what they were made from (a new picture replaces stripes).
    private readonly Dictionary<string, (string Made, Texture2D Texture)> _inventionIcons = new(StringComparer.Ordinal);
    private LookNight? _nightLook;
    private readonly Dictionary<string, Label> _zzz = new(StringComparer.Ordinal);
    private double _nightClock;
    private PhaseStatus? _nightStatus;
    private int _nightUnanswered;
    private double _nightPoll;
    private double _nightPollS = 2;
    private double _nightDarkness = 1;
    private bool _nightAsking;
    private readonly VBoxContainer _timelineList = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Dictionary<string, Button> _themeButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastOfTheme = new(StringComparer.Ordinal);
    private readonly List<(bool Own, Button Tab)> _zoneTabs = [];
    private bool _zones;
    private readonly GwTable _inhabitants = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _questionList = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _logList = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly HashSet<string> _hintedQuestions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QuestionEntry> _questionEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QuestionEntry> _cardQuestionEntries = new(StringComparer.Ordinal);
    private Label? _questionsEmpty;
    private Label? _questionsLater;
    private VBoxContainer? _cardQuestionBox;
    private VBoxContainer? _cardDiaryBox;
    private (string Id, long Version) _cardDiaryShown;
    private ActivityDiary? _diary;
    private ConversationLog? _log;
    private long _logShown = -1;
    private string _questionShape = "-";
    private readonly VBoxContainer _chronicleList = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private ChronicleBook? _chronicle;
    private Contracts.ChronicleKinds? _kinds;
    private int _chronicleShown = -1;
    private long? _chronicleDay;
    private readonly List<(Button Button, TimeChoice Choice)> _timeButtons = [];
    private readonly Dictionary<string, ProgressBar> _cardBars = new(StringComparer.Ordinal);
    private Label? _cardAction;
    private TextureRect? _cardActionIcon;
    private Dictionary<string, string>? _activities;
    private Label? _cardQuestions;
    private LineEdit? _talkInput;

    // A1617 (B2678): pointing while writing - the inhabitant whose card waits for a click into the world, the type
    // pointed at for the next sentence, and the chip that shows it.
    private string? _pointingFor;
    private string? _pointedType;
    private Button? _pointButton;
    private HBoxContainer? _pointedChip;
    private Label? _pointedLabel;
    private readonly Dictionary<string, bool> _cardFolded = new(StringComparer.Ordinal);
    private string _cardShape = string.Empty;
    private readonly GwToasts _toasts = new();
    private readonly List<Control> _restingUi = [];
    private bool _worldHeld;
    private bool _menuPaused;
    private double _speedBeforeNight = 1;
    private bool _cameraHeldBeforeNight;
    private long? _nightfallTick;
    private PanelContainer? _topBar;
    private Label? _date;
    private Label? _favor;
    private readonly VBoxContainer _helpList = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private string _helpShape = "-";
    private readonly Dictionary<string, IReadOnlyList<HelpCard>> _aiCards = new(StringComparer.Ordinal);
    private readonly HashSet<string> _askedHelp = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Key, IReadOnlyList<HelpCard>? Cards, string? Problem)> _helpAnswers = new();
    private string? _placing;
    private ClickGate? _click;
    private readonly VBoxContainer _mailList = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private string _mailShape = "-";
    private Label? _time;
    private WorldPresenter? _presenter;
    private WorldFeed? _feed;
    private UiStyle? _style;
    private ToastBoard? _board;
    private GwWindow? _card;
    private string? _cardId;
    private string? _lastCardId;
    private double _sinceRefreshMs = double.MaxValue;

    /// <summary>Id of the inhabitant whose card is open, or null.</summary>
    public string? CardId => _cardId;

    /// <summary>Binds the screen to the running world and builds it.</summary>
    public void Setup(WorldPresenter presenter, UiStyle style)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));

        // Thoughts and said sentences on screen (W1687): below the top bar, beside the side panel, inside the window.
        presenter.ShowBubbles(BubbleArea, () => (float)(_style?.ContentScaleFactor ?? 1));
        _style = style ?? throw new ArgumentNullException(nameof(style));
        _feed = new WorldFeed(presenter.Host ?? throw new InvalidOperationException("world not started"));
        _board = new ToastBoard(style.Tokens.Toast, new ToastSettings(1, UntilDismissed: false));

        AddChild(_root);
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // The compute night (B7, B2579/B2580): a shade over the world, behind every bar and window, and the
        // dream scene's words in the middle. Both let the mouse through.
        var nightLook = GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<LookNight>(System.IO.File.ReadAllText(System.IO.Path.Combine(presenter.ContentDirectory, "look", "night.json")));
        _night = new NightScene(nightLook);
        _nightLook = nightLook;
        _dreams = new DreamImages(nightLook.Dreams);

        // Pictures the service made lie in its output folder, like the made sounds (GW2 #206).
        _dreamFolder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(GlassWorld.Presentation.Audio.InventionSounds.DefaultFolder(
            presenter.ContentDirectory, null, OS.GetEnvironment("GLASSWORLD_AI_ROOT")))!, "images");
        if (OS.GetEnvironment("GW_AI_IMAGE_OUTPUT_DIR") is { Length: > 0 } images)
        {
            _dreamFolder = System.IO.Path.GetFullPath(images);
        }
        (_nightPollS, _nightDarkness) = (nightLook.PollS, nightLook.Darkness);
        _root.AddChild(_nightShade);
        _nightShade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _nightText.AddChild(new Label { Text = "ui.night.title", ThemeTypeVariation = UiTheme.OnDarkHeaderLabel, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore });
        _nightText.AddChild(_nightProgress);
        _root.AddChild(_nightText);
        _nightText.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        _nightText.GrowHorizontal = Control.GrowDirection.Both;
        _nightText.GrowVertical = Control.GrowDirection.Both;

        // Windows over the game (pause, settings) stay visible when the UI is hidden ("watch only").
        AddChild(_overlay);
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        UiSetup.Bind(_root);
        UiSetup.Bind(_overlay);
        (_topBar, _date, _time) = CreateTopBar(style, ScreenModel.TimeChoices(presenter.Host.World.Calendar.Settings), speed => presenter.Speed = speed);
        foreach (var (button, choice) in _topBar.GetChild(0).GetChildren().OfType<Button>().Zip(ScreenModel.TimeChoices(presenter.Host.World.Calendar.Settings)))
        {
            _timeButtons.Add((button, choice));
        }
        _root.AddChild(_topBar);

        _inhabitants.CustomMinimumSize = new Vector2(0, style.Tokens.Window.TableMinHeight);
        _inhabitants.Setup(
            [new GwColumn("ui.inhabitants.column.name", false), new GwColumn("ui.inhabitants.column.activity", false), new GwColumn("ui.inhabitants.column.will_to_live", true), new GwColumn("ui.inhabitants.column.questions", true)],
            "ui.inhabitants.empty");
        _inhabitants.RowSelected += ShowInhabitant;
        _log = new ConversationLog(style.Tokens.Window.LogLines);
        _diary = new ActivityDiary(style.Tokens.Window.DiaryLines, presenter.Host.World.Calendar);
        _sideBar.Add("inhabitants", "ui.inhabitants.title").Content.AddChild(_inhabitants);
        _sideBar.Add("questions", "ui.questions.title").Content.AddChild(_questionList);

        // Who said or thought what (Marcel W1696): the readable source, while the bubbles stay short.
        _sideBar.Add("log", "ui.log.title").Content.AddChild(_logList);
        _sideBar.Add("chronicle", "ui.chronicle.title").Content.AddChild(_chronicleList);
        _sideBar.Add("help", "ui.help.title").Content.AddChild(_helpList);
        _sideBar.Add("mailbox", "ui.mailbox.title").Content.AddChild(_mailList);

        // Favour in the top bar (B6 R9): a number, the tooltip says what it is for.
        // The value is filled in code (no translation), the tooltip is a key: translate it (review #76).
        _favor = new Label { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, TooltipText = "ui.favor.tooltip", TooltipAutoTranslateMode = Node.AutoTranslateModeEnum.Always, MouseFilter = Control.MouseFilterEnum.Stop };
        _topBar.GetChild(0).AddChild(_favor);

        // Saving was not found in the test (F5287): a visible menu button, the tooltip names Esc.
        var menu = new Button { Text = "ui.top.menu", TooltipText = "ui.top.menu_tooltip", Name = "MenuButton" };
        menu.Pressed += () => PauseRequested?.Invoke();
        _topBar.GetChild(0).AddChild(menu);
        _root.AddChild(_sideBar);
        foreach (var panel in _sideBar.Panels)
        {
            _root.AddChild(panel);
        }

        _sideBar.Add(ScreenZones.Card, ScreenZones.TitleKey(ScreenZones.Card));
        _sideBar.ButtonOf(ScreenZones.Card).Visible = false;
        _root.AddChild(_sideBar.PanelOf(ScreenZones.Card));

        // The timeline over seasons and years as its own tab of the world theme (D642 step 3); the layout so
        // far keeps it as the drop-down inside the chronicle.
        _sideBar.Add(ScreenZones.Timeline, ScreenZones.TitleKey(ScreenZones.Timeline)).Content.AddChild(_timelineList);
        _sideBar.ButtonOf(ScreenZones.Timeline).Visible = false;
        _root.AddChild(_sideBar.PanelOf(ScreenZones.Timeline));
        BuildZones();

        _toasts.Board = _board;
        _root.AddChild(_toasts);

        // The hints grow upwards from the bottom edge (W1934, W1744): they were placed only on a new window
        // size, when the area was empty, so every later hint hung below the screen edge with only its top
        // border to see.
        _toasts.MinimumSizeChanged += PlaceToasts;

        // Events never move the camera by themselves (TL #19976): a new thought is a hint, and only its
        // "look" button makes the camera follow that inhabitant (until the player moves it or presses Esc).
        presenter.Noticed += NoticeThought;
        _toasts.LookRequested += id => _presenter?.ChooseFigure(id);
        GetViewport().SizeChanged += Layout;

        // A new theme (text size, UI size) changes the top bar's height after it has wrapped again;
        // lay out once more two frames later, so the card stays below the bar (CI found it).
        _root.ThemeChanged += () => _relayoutFrames = 2;
        _sideBar.Toggled += PanelToggled;
        Layout();
    }

    /// <summary>
    /// The zones layout (W1717, D642 step 1, preview): three theme buttons instead of six side buttons, and a
    /// row of tabs in each side window whose theme holds more than one. Hidden until the player switches the
    /// preview on; the side windows themselves stay as they are.
    /// </summary>
    private void BuildZones()
    {
        foreach (var theme in ScreenZones.Themes)
        {
            var button = new Button { Text = theme.TitleKey, ToggleMode = true };
            button.Pressed += () => _sideBar.Open(ScreenZones.Open(theme, _sideBar.OpenKey, _lastOfTheme.GetValueOrDefault(theme.Id)));
            _themeBar.AddChild(button);
            _themeButtons[theme.Id] = button;
            if (theme.Panels.Count < 2)
            {
                continue;
            }

            foreach (var shown in theme.Panels)
            {
                foreach (var target in theme.Panels)
                {
                    var tab = new Button { Text = ScreenZones.TabKey(target), ToggleMode = true };
                    tab.SetPressedNoSignal(target == shown);
                    tab.Pressed += () => _sideBar.Open(target);
                    _sideBar.PanelOf(shown).Tabs.AddChild(tab);
                    _zoneTabs.Add((target == shown, tab));
                }
            }
        }

        _root.AddChild(_themeBar);
    }

    /// <summary>Switches between the layout so far and the zones preview.</summary>
    private void SetZones(bool on)
    {
        if (_zones == on)
        {
            return;
        }

        _zones = on;
        _sideBar.Visible = !_zones;
        _themeBar.Visible = _zones;

        // Compact frames in the zones layout (feedback on D646: panels too wide, top bar too high).
        var frame = _zones ? UiTheme.CompactPanel : UiTheme.WindowPanel;
        if (_topBar is not null)
        {
            _topBar.ThemeTypeVariation = frame;
        }

        foreach (var panel in _sideBar.Panels)
        {
            panel.ThemeTypeVariation = frame;
        }

        // The card changes its home with the layout: close it, the next click opens it in the right place.
        _card?.Close();
        _cardId = null;
        if (_sideBar.OpenKey is ScreenZones.Pinned or ScreenZones.Card)
        {
            _sideBar.Open(null);
        }

        ShowZoneState();
        ShowChronicleDay(_chronicleDay);
    }

    /// <summary>
    /// Screenshots only (W1934): two hints in the hint area (<paramref name="on"/>), or none of them again.
    /// </summary>
    public void ShowHintsForShots(bool on)
    {
        if (_board is null)
        {
            return;
        }

        foreach (var toast in _board.Visible.Where(t => t.Message.Group is "shot-1" or "shot-2"))
        {
            _board.Dismiss(toast.Id);
        }

        if (on)
        {
            _board.Push(new ToastMessage("msg.mail.kind.knock", new Dictionary<string, string>(), "shot-1"), fastForward: false);
            _board.Push(new ToastMessage("msg.mail.kind.knock", new Dictionary<string, string>(), "shot-2"), fastForward: false);
        }
    }

    /// <summary>
    /// Screenshots only: the zones preview in one of its states - a theme's side window (<paramref name="theme"/>),
    /// the card of the first inhabitant (<paramref name="card"/>), or just the zones; <paramref name="on"/>
    /// false returns to the layout so far.
    /// </summary>
    public void ShowZonesForShots(bool on, string? theme = null, bool card = false)
    {
        _card?.Close();
        _sideBar.Open(null);
        SetZones(on);
        if (on && theme is not null && ScreenZones.Themes.FirstOrDefault(t => t.Id == theme) is { } chosen)
        {
            _sideBar.Open(chosen.Panels[0]);
        }
        else if (on && card && _feed?.Inhabitants() is { Count: > 0 } rows)
        {
            ShowInhabitant(rows[0].Id);
        }

        Layout();
    }

    /// <summary>UI tour only: the side windows a player can open in the given layout, in the order of the buttons and tabs.</summary>
    public static IReadOnlyList<string> PanelKeysForShots(bool zones) =>
        zones
            ? [.. ScreenZones.Themes.SelectMany(t => t.Panels).Where(k => k != ScreenZones.Card)]
            : ["inhabitants", "questions", ScreenZones.Pinned, "chronicle", "help", "mailbox"];

    /// <summary>UI tour only: opens a side window (null closes the open one).</summary>
    public void OpenPanelForShots(string? key)
    {
        _sideBar.Open(key);
        Layout();
    }

    /// <summary>UI tour only: the card of an inhabitant (null closes it), with every section unfolded if asked.</summary>
    public void ShowCardForShots(string? inhabitantId, bool expand)
    {
        if (inhabitantId is null)
        {
            CloseCard();
            _sideBar.Open(null);
            return;
        }

        foreach (var key in new[] { "ui.card.wellbeing", "ui.card.feelings", "ui.card.character", "ui.card.diary" })
        {
            _cardFolded[key] = !expand;
        }

        _cardShape = string.Empty;
        ShowInhabitant(inhabitantId);
        Layout();
    }

    /// <summary>A side window opened or closed: in the zones layout it shares its place with the card, and the log stays.</summary>
    private void PanelToggled()
    {
        if (_zones)
        {
            if (_sideBar.OpenKey == ScreenZones.Pinned)
            {
                _sideBar.Open(null);
                return;
            }

            if (_sideBar.OpenKey is { } open && ScreenZones.ThemeOf(open) is { } theme)
            {
                _lastOfTheme[theme.Id] = open;
            }

            if (_sideBar.OpenKey != ScreenZones.Card)
            {
                // Another window took the place (or none): the card is closed.
                _cardId = null;
            }
            else if (_cardId is null)
            {
                // The card tab opened by its tab or theme button: the inhabitant last shown, else how to choose one.
                if (_lastCardId is { } last && _feed?.Card(last) is not null)
                {
                    _cardId = last;
                    _cardShape = string.Empty;
                    FillCard(last);
                }
                else
                {
                    ShowNoCard();
                }
            }

            ShowZoneState();
        }

        Layout();
    }

    /// <summary>Theme buttons, tabs and the pinned log as the open side window and the layout want them.</summary>
    private void ShowZoneState()
    {
        var theme = ScreenZones.ThemeOf(_sideBar.OpenKey);
        foreach (var (id, button) in _themeButtons)
        {
            button.SetPressedNoSignal(theme?.Id == id);
        }

        foreach (var (own, tab) in _zoneTabs)
        {
            // A toggled tab keeps showing which window is open, also after a click on the open one.
            tab.SetPressedNoSignal(own);
        }

        foreach (var panel in _sideBar.Panels)
        {
            panel.Tabs.Visible = _zones && panel.Tabs.GetChildCount() > 0;
        }

        _sideBar.PanelOf(ScreenZones.Pinned).Visible = _zones || _sideBar.OpenKey == ScreenZones.Pinned;
    }

    /// <summary>
    /// The top bar: world clock (date and time as two parts) and one toggle per speed (pause, normal,
    /// fast-forward factors). Its parts flow into a second line when the room is too narrow (large UI
    /// or text size), so nothing is cut off (D366 section 2).
    /// </summary>
    public static (PanelContainer Bar, Label Date, Label Time) CreateTopBar(UiStyle style, IReadOnlyList<TimeChoice> choices, Action<double> setSpeed)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(choices);
        var bar = new PanelContainer { ThemeTypeVariation = UiTheme.WindowPanel };
        var row = new HFlowContainer();
        var date = new Label { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        var time = new Label { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        row.AddChild(date);
        row.AddChild(time);
        var group = new ButtonGroup();
        foreach (var choice in choices)
        {
            var button = new Button { ToggleMode = true, ButtonGroup = group, Text = choice.Symbol };
            button.TooltipText = TextFill.Fill(button.Tr(choice.TooltipKey), choice.Args);
            button.ButtonPressed = choice.Speed == 1;
            var speed = choice.Speed;
            button.Pressed += () => setSpeed(speed);
            row.AddChild(button);
        }

        bar.AddChild(row);
        return (bar, date, time);
    }

    public override void _Process(double delta)
    {
        if (_relayoutFrames > 0 && --_relayoutFrames == 0)
        {
            Layout();
        }

        if (_feed is null || _board is null || _style is null || _presenter is null)
        {
            return;
        }

        while (_helpAnswers.TryDequeue(out var answer))
        {
            if (answer.Cards is not null)
            {
                _aiCards[answer.Key] = answer.Cards;
                _helpShape = "-";
            }
            else
            {
                // Calm fallback: the window keeps the suggestions from the data (B3 A3).
                if (answer.Problem?.StartsWith("error: ", StringComparison.Ordinal) == true)
                {
                    GD.PushError($"help suggestions: {answer.Problem}");
                }
                else
                {
                    GD.Print($"HELP_FALLBACK {answer.Problem}");
                }
            }
        }

        var events = _feed.NewEvents();
        var rowsNow = _feed.Inhabitants();
        _board.DismissWhere(m => ScreenModel.IsSettledQuestionHint(m, rowsNow));
        if (events.Count > 0)
        {
            _log?.Add(events);
            foreach (var hint in ScreenModel.Hints(events, _feed.Inhabitants(), Tr("ui.inhabitant.unnamed"), _feed.QuestionSubject, _hintedQuestions))
            {
                _board.Push(hint, fastForward: _presenter.Speed > 1);
            }

            foreach (var knock in events.Where(e => e.Kind == Simulation.Player.MailboxSystem.MailArrived && e.Detail == Simulation.Player.MailboxSystem.KnockKind))
            {
                KnockAt(knock.InhabitantId);
            }
        }

        UpdateNight(delta);

        _sinceRefreshMs += delta * 1000;
        if (_sinceRefreshMs >= _style.Tokens.Window.ListRefreshMs)
        {
            _sinceRefreshMs = 0;
            Refresh();
        }
    }

    public override void _Notification(int what)
    {
        // Tooltips of the symbol buttons are filled once; refresh them after a language switch.
        if (what == NotificationTranslationChanged)
        {
            foreach (var (button, choice) in _timeButtons)
            {
                button.TooltipText = TextFill.Fill(Tr(choice.TooltipKey), choice.Args);
            }

            _cardShape = string.Empty;
            _chronicleShown = -1;
            _chronicleDay = null;
            _helpShape = "-";
            _mailShape = "-";
            _sinceRefreshMs = double.MaxValue;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel") && _pointingFor is not null)
        {
            // Esc first ends waiting for the pointing click (A1617).
            _pointingFor = null;
            UpdatePointing();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("ui_cancel") && _placing is not null)
        {
            // Esc first ends placing a gift.
            _placing = null;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("ui_cancel"))
        {
            // Open windows close themselves on Esc first (GwWindow); only then the pause menu opens. On the
            // way Esc lets go of a followed inhabitant, so one Esc always reaches the menu (GW3 #144).
            _presenter?.StopFollowing();
            PauseRequested?.Invoke();
            GetViewport().SetInputAsHandled();
            return;
        }

        // B2680: in the compute night only Esc works - no time keys, no clicks into the world.
        if (_worldHeld)
        {
            return;
        }

        if (TimeKey(@event, beforeGui: false))
        {
            return;
        }

        // A left press into the world only starts a gesture: let go soon and close by it is a click (gift
        // placement or figure selection), moved farther it is a camera drag (B2567). The press is not
        // consumed, so the camera sees it as well and both use the same thresholds.
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press && _style is not null)
        {
            var data = _presenter?.Rig?.Model.Data;
            _click = new ClickGate(data?.ClickMaxPx ?? double.MaxValue, data?.ClickMaxMs ?? double.MaxValue);
            _click.Press(press.Position.X, press.Position.Y, Time.GetTicksMsec());
        }
    }

    /// <inheritdoc/>
    public override void _Input(InputEvent @event)
    {
        // Moves and the release of a started press are read before the GUI, so a drag across a panel is
        // still a drag; nothing is consumed here (only a time key on a focused time button, GameScreen.Keys).
        if (_worldHeld || TimeKey(@event, beforeGui: true) || _click is not { Pressed: true } click)
        {
            return;
        }

        if (@event is InputEventMouseMotion motion)
        {
            click.Move(motion.Position.X, motion.Position.Y);
        }
        else if (@event is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left } release
            && click.Release(release.Position.X, release.Position.Y, Time.GetTicksMsec()))
        {
            WorldClick(release.Position);
        }
    }

    /// <summary>A short left click into the world: places the gift being placed, else selects a figure or stops following.</summary>
    private void WorldClick(Vector2 position)
    {
        if (_pointingFor is not null)
        {
            // A1617: the nearest thing to the clicked spot within the data's radius; the core keeps it only if
            // the inhabitant sees it.
            var radius = _style?.Tokens.Window.PointRadiusM ?? 2.5;
            if (WorldPoint(position) is { } spot && _feed?.Snapshot is { } snapshot
                && PointTarget.Nearest(snapshot.Objects, spot.X, spot.Z, radius) is { } thing)
            {
                _pointedType = thing.TypeId;
            }
            else
            {
                ShowHint(new ToastMessage("ui.card.point_nothing", new Dictionary<string, string>(), null));
            }

            _pointingFor = null;
            UpdatePointing();
            return;
        }

        if (_placing is not null)
        {
            if (WorldPoint(position) is { } point)
            {
                // On the island a spot on water or off the island is refused here, before the core: no
                // favour is spent and the gift never sinks out of reach (GW3 #113, finding 1).
                if (_presenter?.GiftRefusal(point) is { } reason)
                {
                    ShowHint(new ToastMessage(reason, new Dictionary<string, string>(), null));
                }
                else
                {
                    PlaceGift(_placing, point.X, point.Z);
                }
            }

            return;
        }

        // The screen owns the left click (gift placement above, otherwise figure selection), so one click
        // never does both; it also tells the camera (follow the figure, or stop following).
        if (_style is null)
        {
            return;
        }

        if (PickAt(position) is { } id)
        {
            Encounter(id);
            _presenter?.ChooseFigure(id);
        }
        else
        {
            _presenter?.ClickedGround();
        }
    }

    /// <summary>The player clicks an inhabitant in the world: a perception for the core (R7) and the card.</summary>
    public void Encounter(string inhabitantId)
    {
        _presenter?.Host?.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, inhabitantId));
        OpenCard(inhabitantId);
    }

    /// <summary>Time buttons on or off (off while the pause menu is open, GW1 review #50 K3).</summary>
    public void SetTimeControlsEnabled(bool enabled)
    {
        // The pause menu calls this: while it is open the night scene's time stands too (a paused game holds the fade).
        _menuPaused = !enabled;
        foreach (var (button, _) in _timeButtons)
        {
            button.Disabled = !enabled;
        }
    }

    /// <summary>Esc without an open window: the player wants the pause menu (B6 R5).</summary>
    public event Action? PauseRequested;

    /// <summary>Applies changed settings at once (R4): style, hint duration, hidden UI.</summary>
    public void ApplySettings(PlayerSettings settings, UiStyle style)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _style = style ?? throw new ArgumentNullException(nameof(style));
        if (_board is not null)
        {
            _board.Settings = settings.ToToastSettings();
        }

        // "Watch only" (XAG 117): bars, panels and hints disappear; Esc still opens the pause menu.
        _root.Visible = !settings.HideUi;
        SetZones(settings.ZonesPreview);
        Layout();
    }

    /// <summary>
    /// Shows a window over the game (pause menu, settings) and fits it to the screen. A modal window
    /// blocks clicks on everything behind it until it closes.
    /// </summary>
    public void ShowWindow(GwWindow window, bool modal = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (modal)
        {
            // The game behind a modal window (pause menu) is darkened a little (W1687, data: window.modal_dim).
            var blocker = new ColorRect { Name = "ModalDim", MouseFilter = Control.MouseFilterEnum.Stop, Color = new Color(0, 0, 0, (float)(_style?.Tokens.Window.ModalDim ?? 0)) };
            _overlay.AddChild(blocker);

            // Anchors and offsets once it has a parent: the picture of the pause menu showed no dimming
            // (D705 B7) - the rectangle had no size.
            blocker.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            window.Closed += blocker.QueueFree;
        }

        _overlay.AddChild(window);
        window.FitInto(GetViewport().GetVisibleRect().Size);
    }

    /// <summary>Starts the chronicle window with the chronicle kinds of the data (B8 R9/R10).</summary>
    public void SetupChronicle(Contracts.ChronicleKinds kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (_presenter?.Host is { } host)
        {
            _chronicle = new ChronicleBook(kinds, host.World.Calendar);
            _kinds = kinds;
        }

        _chronicleShown = -1;
    }

    /// <summary>The text of a chronicle entry in the current language (date line and base line).</summary>
    public string ChronicleText(ChronicleEntry entry) => ChronicleDate(entry) + "\n" + ChronicleLine(entry);

    /// <summary>The date of a chronicle entry in the current language.</summary>
    public string ChronicleDate(ChronicleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return TextFill.Fill(Tr("ui.chronicle.day"), new Dictionary<string, string>
        {
            ["season"] = Tr(entry.SeasonKey),
            ["day"] = Number(entry.Day),
            ["year"] = Number(entry.Year),
        });
    }

    /// <summary>The base line of a chronicle entry in the current language (names resolved now).</summary>
    public string ChronicleLine(ChronicleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Names are resolved now (GW1 review #64 C2); parameters that are keys are translated now.
        var (key, raw) = ChronicleBook.Line(entry, ChronicleNamesNow());
        var args = raw.ToDictionary(a => a.Key, a => TextKeys.LooksLikeKey(a.Value) ? Tr(a.Value).ToString() : a.Value, StringComparer.Ordinal);
        return TextFill.Fill(Tr(key), args);
    }

    /// <summary>
    /// Smoke run only (review GW1 #131): the talk field of a card takes a sentence, it costs favour and an
    /// answer from the mock comes back as a reaction event; a refused sentence costs nothing.
    /// </summary>
    /// <summary>
    /// Smoke run only (review GW1 #136): two chronicle entries on two days make two pages; earlier/later
    /// page between them; the export writes a web page and a text file (removed again afterwards).
    /// </summary>
    public async Task<string> CheckChronicleAsync()
    {
        if (_presenter?.Host is not { } host || _chronicle is null || _feed?.Inhabitants() is not { Count: > 0 } rows)
        {
            return "no world or chronicle";
        }

        var kind = host.Content.Chronicle.Kinds.First();
        var day = host.World.Calendar.TicksPerDay;
        host.Inhabitants.Report(new Simulation.Inhabitants.WorldEvent(kind.Event, host.World.Tick, rows[0].Id, null, null));
        host.Inhabitants.Report(new Simulation.Inhabitants.WorldEvent(kind.Event, host.World.Tick + day, rows[0].Id, null, null));
        RefreshChronicle();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_chronicle.Days().Count < 2)
        {
            return "two days did not make two pages";
        }

        var newest = _chronicle.Days()[0];
        ShowChronicleDay(_chronicle.Next(newest, -1));
        if (_chronicleDay is null)
        {
            return "earlier did not page back";
        }

        ShowChronicleDay(_chronicle.Next(_chronicleDay.Value, +1));
        if (_chronicleDay is not null)
        {
            return "later did not page to the newest day";
        }

        if (ExportChronicle() is not { } html || !System.IO.File.Exists(html) || !System.IO.File.Exists(System.IO.Path.ChangeExtension(html, ".txt")))
        {
            return "export wrote no files";
        }

        System.IO.File.Delete(html);
        System.IO.File.Delete(System.IO.Path.ChangeExtension(html, ".txt"));
        return string.Empty;
    }

    /// <summary>
    /// Smoke run only (W1687): two inhabitants say long sentences at once; the bubbles shown never overlap and
    /// stay inside the free area (below the top bar, beside the side panel).
    /// </summary>
    public async Task<string> CheckBubblesAsync()
    {
        if (_presenter is null || _feed?.Inhabitants() is not { Count: >= 2 } rows || BubbleArea() is not { } area)
        {
            return "no world, two inhabitants or screen area";
        }

        const string Long = "Ich zeige dir, wie man das wilde Korn isst, damit wir nicht allein sind und es uns gut geht.";
        _presenter.SayForCheck(rows[0].Id, Long);
        _presenter.SayForCheck(rows[1].Id, Long);
        _presenter.CloseLook(rows[0].Id, 14);
        for (var i = 0; i < 4; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        var shown = _presenter.BubblesShown();
        _presenter.SayForCheck(rows[0].Id, string.Empty);
        _presenter.SayForCheck(rows[1].Id, string.Empty);
        foreach (var (id, box) in shown)
        {
            if (!area.Grow(1).Encloses(box))
            {
                return $"bubble of {id} at {box} leaves the free area {area}";
            }

            if (shown.FirstOrDefault(o => o.Id != id && o.Box.Intersects(box)) is { Id: { } other })
            {
                return $"bubbles of {id} and {other} overlap";
            }
        }

        GD.Print($"BUBBLES_OK shown={shown.Count}");
        return string.Empty;
    }

    public async Task<string> CheckTalkAsync()
    {
        if (_presenter?.Host is not { } host || _feed?.Inhabitants() is not { Count: > 0 } rows)
        {
            return "no world";
        }

        var id = rows[0].Id;
        OpenCard(id);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_talkInput is null)
        {
            return "card has no talk field";
        }

        host.Conversation.Port ??= new Simulation.Player.MockConversationPort();
        var favor = host.Player.Favor;
        if (Say(id, "Ich will dich töten"))
        {
            return "a refused sentence was taken";
        }

        _talkInput.Text = Tr("ui.card.talk_send"); // any harmless sentence; a key keeps the code free of visible text
        _talkInput.EmitSignal(LineEdit.SignalName.TextSubmitted, _talkInput.Text);
        if (_talkInput.Text.Length > 0)
        {
            return "the field did not take the sentence";
        }

        var seen = host.Inhabitants.EventsSince(0).Count;
        for (var i = 0; i < 3; i++)
        {
            host.World.Step();
        }

        var reactions = host.Inhabitants.EventsSince(0).Skip(seen).Select(e => e.Event.Kind).ToHashSet(StringComparer.Ordinal);
        if (!reactions.Contains("conversation_started") || !reactions.Overlaps(["said", "gestured", "declined", "not_understood"]))
        {
            return "no reaction (" + string.Join(",", reactions) + ")";
        }

        return host.Player.Favor < favor ? string.Empty : "the sentence cost no favour";
    }

    /// <summary>
    /// Smoke (A1340): a gift placed through the screen is accepted by the core and lies at the spot after
    /// the next tick; the favour shows as text, not as a key.
    /// </summary>
    public async Task<string> CheckGiftAsync()
    {
        if (_presenter?.Host is not { } host || _favor is null)
        {
            return "gift check not set up";
        }

        RefreshHelp();
        if (_favor.Text.Length == 0 || _favor.Text.Contains("ui.favor", StringComparison.Ordinal))
        {
            return "favour not shown";
        }

        // A free spot well away from every inhabitant.
        var x = host.Inhabitants.All.Max(i => i.Position.X) + 10;
        var z = host.Inhabitants.All.Max(i => i.Position.Z) + 10;
        var type = host.Content.Objects.TypeIds.First();
        if (!PlaceGift(type, x, z))
        {
            return "the core refused a gift on a free spot";
        }

        var speed = _presenter.Speed;
        _presenter.Speed = 1;
        var start = host.World.Tick;
        for (var frame = 0; frame < 600 && host.World.Tick <= start; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        _presenter.Speed = speed;
        return host.Objects.All.Any(o => o.TypeId == type && Math.Abs(o.Position.X - x) < 1e-6 && Math.Abs(o.Position.Z - z) < 1e-6)
            ? string.Empty
            : "the gift is not in the world";
    }

    /// <summary>
    /// Smoke, island mode (TL #19505): a click on a figure selects it (the click point sits between feet and
    /// head), the gift ray under it lands on the drawn ground, and the question marks hang on the head anchor.
    /// </summary>
    public async Task<string> CheckIslandAsync()
    {
        if (_presenter?.Host is not { } host || _presenter.Mode != WorldLookMode.Island || _style is null)
        {
            return "island check not set up";
        }

        var id = host.Inhabitants.All.First().Id;
        _presenter.ChooseFigure(id);
        for (var frame = 0; frame < 120; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        try
        {
            if (GetViewport().GetCamera3D() is not { } camera || _presenter.PickPoint(id) is not { } point || camera.IsPositionBehind(point))
            {
                return $"{id} is not in view after choosing it";
            }

            var screen = camera.UnprojectPosition(point);
            if (PickAt(screen) != id)
            {
                return $"a click on {id} at {screen} does not select it";
            }

            if (WorldPoint(screen) is not { } ground)
            {
                return "the gift ray finds no ground behind the figure";
            }

            // The ray lands on the drawn ground, or on the sea surface above it, which is refused as water.
            var height = _presenter.GroundHeight(ground.X, ground.Z);
            var onSea = ground.Y > height + 0.01f;
            if (ground.Y < height - 0.01f || (onSea && _presenter.GiftRefusal(ground) != GiftSpot.OnWater))
            {
                return $"the gift point {ground} is neither on the drawn ground ({height:0.00} m) nor refused as water";
            }

            if (_presenter.GiftRefusal(new Vector3(-5, 0, -5)) != GiftSpot.OffIsland)
            {
                return "a gift off the island is not refused";
            }

            if (_presenter.MarkAnchor(id, 0) is not { } anchor || anchor.Parent.Name != "HeadAnchor")
            {
                return "question marks are not anchored to the head";
            }

            return string.Empty;
        }
        finally
        {
            _presenter.ClickedGround();
        }
    }

    /// <summary>
    /// Smoke (A1341): a knock trembles at the glass only when motion is allowed. With "reduce motion"
    /// (glass_tremor time 0) nothing is drawn; the mailbox shows open messages and "read" moves one to
    /// the history.
    /// </summary>
    public async Task<string> CheckKnockAsync()
    {
        if (_presenter?.Host is not { } host || _style is null)
        {
            return "knock check not set up";
        }

        var style = _style;
        var who = host.Inhabitants.All.First().Id;
        try
        {
            _style = style.With(style.Settings with { ReduceMotion = true });
            if (KnockAt(who) || _overlay.GetChildren().OfType<GlassTremor>().Any())
            {
                return "tremor shown although motion is reduced";
            }

            _style = style.With(style.Settings with { ReduceMotion = false });
            var shown = KnockAt(who);
            var visible = GetViewport().GetCamera3D() is { } camera && _presenter.PickPoint(who) is { } figure && !camera.IsPositionBehind(figure);
            if (visible && !shown)
            {
                return "no tremor with motion allowed";
            }
        }
        finally
        {
            _style = style;
        }

        // Let the tremor run out and free itself.
        for (var frame = 0; frame < 120 && _overlay.GetChildren().OfType<GlassTremor>().Any(); frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        if (_overlay.GetChildren().OfType<GlassTremor>().Any())
        {
            return "the tremor did not end";
        }

        RefreshMail();
        if (host.Mailbox.Open is [var open, ..])
        {
            if (!host.Decisions.Submit(new Simulation.Decisions.PlayerCommand(Simulation.Player.MailboxSystem.Acknowledge, null) { MessageId = open.Id }).Accepted)
            {
                return "the core refused to mark a message read";
            }
        }

        return string.Empty;
    }

    /// <summary>Smoke: every chronicle kind gives a full line (no placeholder or key left) in DE and EN.</summary>
    private string CheckChronicle()
    {
        if (_chronicle is null || _presenter?.Host is not { } host)
        {
            return "chronicle not set up";
        }

        var language = TranslationServer.GetLocale();
        try
        {
            foreach (var locale in Localization.TextCatalogs.SupportedLocales)
            {
                Localization.TextCatalogs.SetLanguage(locale);
                var book = new ChronicleBook(_kinds!, host.World.Calendar);
                book.Add(_kinds!.Kinds.Select((k, i) => new Simulation.Inventions.ChronicleRecord(i + 1, 1, k.Id, "inh-000001", "obj-000001", "eat")));
                foreach (var entry in book.Entries)
                {
                    var text = ChronicleText(entry);
                    if (text.Contains('{', StringComparison.Ordinal) || text.Split(' ', '\n').Any(w => TextKeys.LooksLikeKey(w.TrimEnd('.', ':'))))
                    {
                        return $"chronicle line '{text}' ({locale}) is not complete";
                    }
                }
            }
        }
        finally
        {
            Localization.TextCatalogs.SetLanguage(language);
        }

        return string.Empty;
    }

    /// <summary>A small square for an invention's icon (A1603), the size of the data, kept in proportion.</summary>
    private TextureRect IconRect() => new()
    {
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        CustomMinimumSize = new Vector2(InventionIconPx, InventionIconPx),
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore,
        Visible = true,
    };

    private int InventionIconPx => (int)Math.Round((_style?.Tokens.Window.InventionIconPx ?? 24) * (_style?.ContentScaleFactor ?? 1));

    /// <summary>
    /// The icon of an invention (A1603) by its type id or the id of one of its objects: the painted picture of the
    /// computing night, else stripes in the kit colours of its plan; null for anything else.
    /// </summary>
    private Texture2D? InventionIcon(string id)
    {
        if (_presenter?.Host is not { } host)
        {
            return null;
        }

        var type = host.Inventions.Invention(id) is not null ? id : host.Objects.TryGet(id, out var obj) ? obj.TypeId : null;
        var source = InventionIconSource.Of(host.Inventions, host.Content.Inventions.Build.Kit, _dreamFolder, _style?.Tokens.Window.InventionIconColors ?? 3);
        if (type is null || source.For(type) is not { } icon)
        {
            return null;
        }

        var made = icon.PicturePath ?? string.Join(',', icon.KitColors);
        if (_inventionIcons.TryGetValue(type, out var known) && known.Made == made)
        {
            return known.Texture;
        }

        Texture2D? texture = null;
        if (icon.PicturePath is { } path)
        {
            var picture = Image.LoadFromFile(path);
            if (picture is not null && !picture.IsEmpty())
            {
                texture = ImageTexture.CreateFromImage(picture);
            }
        }

        // No picture (or a broken one): one stripe per kit colour, the largest share first.
        if (texture is null && icon.KitColors.Count > 0)
        {
            const int Size = 32;
            var stripes = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
            for (var x = 0; x < Size; x++)
            {
                var color = Color.FromHtml(icon.KitColors[Math.Min(icon.KitColors.Count - 1, x * icon.KitColors.Count / Size)]);
                stripes.FillRect(new Rect2I(x, 0, 1, Size), color);
            }

            texture = ImageTexture.CreateFromImage(stripes);
        }

        if (texture is null)
        {
            return null;
        }

        _inventionIcons[type] = (made, texture);
        return texture;
    }

    private ChronicleNames ChronicleNamesNow() => new(
        NameOfId,
        id => _presenter?.Host is { } host
            ? ChronicleBook.InventionName(id, host.Inventions, o => host.Objects.TryGet(o, out var obj) ? obj.TypeId : null, TranslationServer.GetLocale())
            : null,
        VocabKeys.Action);

    private void RefreshChronicle()
    {
        if (_chronicle is not null && _presenter?.Host is { } host)
        {
            // The core keeps the chronicle (saved state, #75); take what is new.
            _chronicle.Sync(host.Chronicle);
        }

        if (_chronicle is null || _chronicle.Version == _chronicleShown)
        {
            return;
        }

        // A1354 (B8 R11): one game day per page. On the newest page new entries show up at once; an
        // older page stays where the player put it.
        _chronicleShown = _chronicle.Version;
        ShowChronicleDay(_chronicleDay);
    }

    /// <summary>Shows the chronicle page of a day (null = the newest day with entries).</summary>
    private void ShowChronicleDay(long? day)
    {
        if (_chronicle is null)
        {
            return;
        }

        foreach (var child in _chronicleList.GetChildren())
        {
            _chronicleList.RemoveChild(child);
            child.QueueFree();
        }

        foreach (var child in _timelineList.GetChildren())
        {
            _timelineList.RemoveChild(child);
            child.QueueFree();
        }

        var days = _chronicle.Days();
        if (days.Count == 0)
        {
            _chronicleList.AddChild(new Label { Text = "ui.chronicle.empty", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            _timelineList.AddChild(new Label { Text = "ui.timeline.empty", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            return;
        }

        var shown = day is { } wanted && days.Contains(wanted) ? wanted : days[0];
        _chronicleDay = shown == days[0] ? null : shown;
        var entries = _chronicle.On(shown);

        // Paging: earlier / later day with entries, the date in between.
        var paging = new HBoxContainer();
        var earlier = new Button { Text = "ui.chronicle.earlier", Disabled = _chronicle.Next(shown, -1) is null };
        var later = new Button { Text = "ui.chronicle.later", Disabled = _chronicle.Next(shown, +1) is null };
        earlier.Pressed += () => ShowChronicleDay(_chronicle.Next(shown, -1));
        later.Pressed += () => ShowChronicleDay(_chronicle.Next(shown, +1));
        paging.AddChild(earlier);
        paging.AddChild(new Label
        {
            Text = ChronicleDate(entries[0]),
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });
        paging.AddChild(later);
        _chronicleList.AddChild(paging);

        // The time line over seasons and years: a jump to the first day of a season with entries.
        var timeline = new OptionButton { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var seasons = _chronicle.Timeline();
        for (var i = 0; i < seasons.Count; i++)
        {
            var season = seasons[i];
            timeline.AddItem(TextFill.Fill(Tr("ui.chronicle.season_entry"), new Dictionary<string, string>
            {
                ["season"] = Tr(season.SeasonKey),
                ["year"] = Number(season.Year),
                ["count"] = Number(season.Count),
            }));
            if (entries[0].Year == season.Year && entries[0].SeasonKey == season.SeasonKey)
            {
                timeline.Selected = i;
            }
        }

        timeline.ItemSelected += index => ShowChronicleDay(seasons[(int)index].FirstDay);
        timeline.Visible = !_zones;
        _chronicleList.AddChild(timeline);

        // The same seasons as a list for the timeline tab of the zones layout: a click shows that season's
        // first day in the chronicle tab; the season of the page shown is marked.
        for (var i = 0; i < seasons.Count; i++)
        {
            var season = seasons[i];
            var jump = new Button
            {
                Text = timeline.GetItemText(i),
                ToggleMode = true,
                ButtonPressed = timeline.Selected == i,
                Alignment = HorizontalAlignment.Left,
                AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            jump.Pressed += () =>
            {
                ShowChronicleDay(season.FirstDay);
                _sideBar.Open("chronicle");
            };
            _timelineList.AddChild(jump);
        }

        // The entries of the day: a click opens the inhabitant's card (and the camera follows on the island).
        foreach (var entry in entries)
        {
            var line = new Button
            {
                Text = ChronicleLine(entry),
                Flat = true,
                Alignment = HorizontalAlignment.Left,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "ui.chronicle.jump",
            };

            // An invention named in the line shows its icon before it (A1603).
            if (entry.TargetId is { } target && InventionIcon(target) is { } icon)
            {
                line.Icon = icon;
                line.AddThemeConstantOverride("icon_max_width", InventionIconPx);
            }
            var who = entry.InhabitantId;
            line.Pressed += () =>
            {
                // One who has died has no card any more: its memory page opens (B8 R16).
                if (!Lives(who))
                {
                    OpenMemory(who);
                    return;
                }

                // Jump to the inhabitant: its card, and on the island the camera follows it.
                OpenCard(who);
                _presenter?.ChooseFigure(who);
            };
            _chronicleList.AddChild(line);
        }

        var export = new Button { Text = "ui.chronicle.export", TooltipText = "ui.chronicle.export_tip" };
        export.Pressed += () => ExportChronicle();
        _chronicleList.AddChild(export);
    }

    /// <summary>The chronicle as one web page in the current language (the shared world file carries it).</summary>
    public string ChronicleHtml()
    {
        if (_chronicle is null || _chronicle.Days().Count == 0)
        {
            return string.Empty;
        }

        string Day(long day) => ChronicleDate(_chronicle.On(day)[0]);
        return ChronicleExport.Html(_chronicle, Tr("ui.chronicle.title"), TranslationServer.GetLocale(), Day, ChronicleLine);
    }

    /// <summary>
    /// Writes the chronicle as a readable HTML page and a text file into the player's folder (B8 R11,
    /// F5234; local only) and names the folder in a calm hint. Returns the HTML path, or null.
    /// </summary>
    public string? ExportChronicle()
    {
        if (_chronicle is null || _chronicle.Days().Count == 0)
        {
            ShowHint(new ToastMessage("ui.chronicle.empty", new Dictionary<string, string>(), null));
            return null;
        }

        var folder = System.IO.Path.Combine(OS.GetUserDataDir(), "chronicle");
        System.IO.Directory.CreateDirectory(folder);
        var title = Tr("ui.chronicle.title").ToString();
        var stem = ChronicleExport.FileStem(_presenter?.SavePath is { } save ? System.IO.Path.GetFileNameWithoutExtension(save) : title, DateTimeOffset.Now);
        string Day(long day) => ChronicleDate(_chronicle.On(day)[0]);
        var html = System.IO.Path.Combine(folder, stem + ".html");
        try
        {
            System.IO.File.WriteAllText(html, ChronicleExport.Html(_chronicle, title, TranslationServer.GetLocale(), Day, ChronicleLine));
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, stem + ".txt"), ChronicleExport.Text(_chronicle, title, Day, ChronicleLine));
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            // Disk full, folder locked: a calm hint instead of a crash (review GW1 #136).
            GD.PushWarning($"chronicle export failed: {e.Message}");
            ShowHint(new ToastMessage("msg.hint.chronicle_export_failed", new Dictionary<string, string>(), null));
            return null;
        }

        ShowHint(new ToastMessage("msg.hint.chronicle_exported", new Dictionary<string, string> { ["folder"] = folder }, null));
        return html;
    }

    /// <summary>
    /// Places a gift (A1340): the core checks type, distance and favour; a refusal is a calm hint with
    /// the core's reason. Returns whether the core accepted it.
    /// </summary>
    public bool PlaceGift(string typeId, double x, double z)
    {
        _placing = null;
        if (_presenter?.Host is not { } host)
        {
            return false;
        }

        var result = host.Decisions.Submit(new Simulation.Decisions.PlayerCommand(Simulation.Player.PlayerSystem.PlaceGift, null) { TypeId = typeId, X = x, Z = z });
        ShowHint(new ToastMessage(result.Accepted ? "msg.hint.gift_placed" : result.ReasonKey ?? "msg.command.unknown", new Dictionary<string, string>(), null));
        return result.Accepted;
    }

    /// <summary>
    /// Point on the ground under a screen position (placeholder: y = 0, island: the drawn ground), or null
    /// if the camera looks away.
    /// </summary>
    private Vector3? WorldPoint(Vector2 screen) =>
        GetViewport().GetCamera3D() is { } camera ? _presenter?.GroundPoint(camera, screen) : null;

    /// <summary>
    /// The help window (A1340, F5238): one entry per call for help, with up to the data's number of
    /// gift suggestions. Without the AI the suggestions come from the data (GiftFallback); nothing
    /// happens on its own, the player chooses and places.
    /// </summary>
    private void RefreshHelp()
    {
        if (_feed is null || _presenter?.Host is not { } host || _favor is null)
        {
            return;
        }

        var (value, max) = _feed.Favor();
        _favor.Text = TextFill.Fill(Tr("ui.favor.value"), new Dictionary<string, string> { ["value"] = Number((long)Math.Floor(value)), ["max"] = Number((long)max) });

        var calls = _feed.HelpCalls();
        var locale = HelpLocale();
        foreach (var call in calls)
        {
            AskForHelp(host, call, locale);
        }

        var shape = locale + ";" + string.Join(";", calls.Select(c => HelpKey(c, locale) + (_aiCards.ContainsKey(HelpKey(c, locale)) ? "+ai" : string.Empty)));
        if (shape == _helpShape)
        {
            return;
        }

        _helpShape = shape;
        foreach (var child in _helpList.GetChildren())
        {
            _helpList.RemoveChild(child);
            child.QueueFree();
        }

        if (calls.Count == 0)
        {
            _helpList.AddChild(new Label { Text = "ui.help.empty", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            return;
        }

        var cost = Number((long)host.Player.CostOf("gift"));
        foreach (var call in calls)
        {
            _helpList.AddChild(new Label
            {
                Text = TextFill.Fill(Tr("ui.help.call"), new Dictionary<string, string> { ["number"] = Number(call.Number), ["name"] = NameOfId(call.InhabitantId), ["need"] = Tr(VocabKeys.Need(call.Need)) }),
                AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            });
            var cards = _aiCards.TryGetValue(HelpKey(call, locale), out var fromAi)
                ? fromAi
                : HelpSuggestions.Cards(null, HelpSuggestions.AllowedTypes(host, call), HelpSuggestions.MaxSuggestions(host));
            foreach (var card in cards)
            {
                if (card.Kind == HelpCard.Answer)
                {
                    // An encouraging sentence (own effort first, F5238); the AI text is in the display language.
                    _helpList.AddChild(new Label
                    {
                        Text = TextFill.Fill(Tr("ui.help.answer"), new Dictionary<string, string> { ["text"] = card.Text ?? string.Empty }),
                        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    });
                    continue;
                }

                var type = card.GiftType!;
                var button = new Button
                {
                    Text = TextFill.Fill(Tr("ui.help.give"), new Dictionary<string, string> { ["thing"] = Tr(VocabKeys.ObjectType(type)), ["cost"] = cost }),
                    AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                    TooltipText = card.Text ?? string.Empty,
                };
                button.Pressed += () =>
                {
                    _placing = type;
                    ShowHint(new ToastMessage("msg.hint.place_gift", new Dictionary<string, string>(), null));
                };
                _helpList.AddChild(button);
            }
        }
    }

    private static string HelpLocale() =>
        TranslationServer.GetLocale().StartsWith("en", StringComparison.Ordinal) ? "en" : "de";

    private static string HelpKey(HelpRow call, string locale) =>
        string.Create(CultureInfo.InvariantCulture, $"{call.InhabitantId}|{call.Need}|{call.Tick}|{locale}");

    /// <summary>
    /// Asks the AI once per call and language (A1341, F5238). The answer arrives later; until then, and
    /// whenever it is unusable, the window shows the suggestions from the data.
    /// </summary>
    private void AskForHelp(Simulation.SimulationHost host, HelpRow call, string locale)
    {
        var key = HelpKey(call, locale);
        if (_presenter?.HelpPort is not { } port || !_askedHelp.Add(key))
        {
            return;
        }

        var request = HelpSuggestions.Request(host, call, locale);
        var allowed = HelpSuggestions.AllowedTypes(host, call);
        var max = HelpSuggestions.MaxSuggestions(host);
        var guard = host.Content.Inventions.Guard;
        _ = Task.Run(async () =>
        {
            try
            {
                var answer = await port.SuggestAsync(request).ConfigureAwait(false);
                _helpAnswers.Enqueue(answer.Response is null ? (key, null, answer.Problem) : (key, HelpSuggestions.Cards(answer.Response, allowed, max, t => guard.Check(t).Allowed), null));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Never lost silently (review #86): reported on the main thread, the window keeps the data suggestions.
                _helpAnswers.Enqueue((key, null, "error: " + e.Message));
            }
        });
    }

    /// <summary>
    /// A knock at the glass (A1341, B6 R10): a short ring trembles at the inhabitant's place on the
    /// screen for the token's glass_tremor time. With "reduce motion" that time is 0 and nothing moves;
    /// the mailbox entry still arrives. Returns whether a tremor was shown.
    /// </summary>
    public bool KnockAt(string inhabitantId)
    {
        if (_style is null || _presenter?.PickPoint(inhabitantId) is not { } figure || GetViewport().GetCamera3D() is not { } camera)
        {
            return false;
        }

        var ms = _style.DurationMs("glass_tremor");
        if (ms <= 0 || camera.IsPositionBehind(figure))
        {
            return false;
        }

        var tremor = new GlassTremor { Name = "GlassTremor", Center = camera.UnprojectPosition(figure), Seconds = ms / 1000.0, Color = UiTheme.ToGodot(_style.Color(UiColorId.Accent)), Shape = _style.Tokens.Knock };
        _overlay.AddChild(tremor);
        return true;
    }

    /// <summary>The mailbox window (B6 R11): open messages, warmer the longer unread; read ones as history.</summary>
    private void RefreshMail()
    {
        if (_feed is null || _style is null)
        {
            return;
        }

        var (open, history) = _feed.Mail(10);
        var shape = string.Join(";", open.Select(m => m.Id + ":" + m.Urgency)) + "|" + string.Join(";", history.Select(m => m.Id));
        if (shape == _mailShape)
        {
            return;
        }

        _mailShape = shape;
        foreach (var child in _mailList.GetChildren())
        {
            _mailList.RemoveChild(child);
            child.QueueFree();
        }

        if (open.Count == 0)
        {
            _mailList.AddChild(new Label { Text = "ui.mailbox.empty", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        }

        var warm = UiTheme.ToGodot(_style.Color(UiColorId.Attention));
        foreach (var message in open)
        {
            var row = new HBoxContainer();

            // A builder's knock shows the invention's icon (A1603), like the chronicle and the card.
            if (message.SubjectType is { } subject && InventionIcon(subject) is { } subjectIcon)
            {
                var rect = IconRect();
                rect.Texture = subjectIcon;
                row.AddChild(rect);
            }

            var text = new Label
            {
                Text = MailText(message),
                AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(_style.Tokens.Window.CardBarWidth, 0),
            };

            // Urgency as a calm warm tint (B2466: never blinking, never a sound).
            text.AddThemeColorOverride("font_color", UiTheme.ToGodot(_style.Color(UiColorId.Text)).Lerp(warm, message.Urgency / 3f * (float)_style.Tokens.Knock.UrgencyTint));
            row.AddChild(text);
            var read = new Button { Text = "ui.mailbox.read" };
            var id = message.Id;
            read.Pressed += () => _presenter?.Host?.Decisions.Submit(new Simulation.Decisions.PlayerCommand(Simulation.Player.MailboxSystem.Acknowledge, null) { MessageId = id });
            row.AddChild(read);
            _mailList.AddChild(row);
        }

        if (history.Count > 0)
        {
            _mailList.AddChild(new Label { Text = "ui.mailbox.history", ThemeTypeVariation = UiTheme.SubheaderLabel });
            foreach (var message in history)
            {
                var old = new Label { Text = MailText(message), AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                old.AddThemeColorOverride("font_color", UiTheme.ToGodot(_style.Color(UiColorId.Text_muted)));
                _mailList.AddChild(old);
            }
        }
    }

    private string MailText(MailRow message) => TextFill.Fill(Tr(message.TextKey), new Dictionary<string, string>
    {
        ["number"] = Number(message.Number),
        ["name"] = message.InhabitantId is { } sender ? NameOfId(sender) : string.Empty,
        ["need"] = message.Detail is null || message.AboutBuilding ? string.Empty : Tr(VocabKeys.Need(message.Detail)),

        // A stuck builder's knock (A1602): the invention by its name, what it looks for as property words.
        ["thing"] = message.SubjectName is { } name
            ? TranslationServer.GetLocale().StartsWith("en", StringComparison.OrdinalIgnoreCase) ? name.En : name.De
            : Tr("ui.chronicle.something"),
        ["properties"] = string.Join(", ", message.PropertyKeys.Select(k => Tr(k).ToString())),
    });

    /// <summary>
    /// A text that speaks of an inhabitant, in the form for its sex (<see cref="AddressKeys"/>); the plain
    /// text if the catalogue has no such form.
    /// </summary>
    private string TrFor(string key, string? sex)
    {
        var sexed = AddressKeys.For(key, sex);
        var text = Tr(sexed).ToString();
        return text == sexed ? Tr(key).ToString() : text;
    }

    /// <summary>Shows a calm hint (e.g. saving failed).</summary>
    public void ShowHint(ToastMessage message) => _board?.Push(message, fastForward: false);

    /// <summary>
    /// A new thought: a calm hint with a "look" button, at most one at a time (at high speed thoughts come
    /// often; the next one shows when the last hint is gone).
    /// </summary>
    private void NoticeThought(string inhabitantId)
    {
        if (_board is null || _board.Waiting > 0 || _board.Visible.Any(t => t.Message.LookAt is not null))
        {
            return;
        }

        var name = NameOfId(inhabitantId);
        ShowHint(new ToastMessage("msg.hint.look_thought", new Dictionary<string, string> { ["name"] = name }, null, inhabitantId));
    }

    /// <summary>For screenshots: opens the inhabitants panel and the card of the first inhabitant.</summary>
    public void ShowForShots()
    {
        Refresh();
        if (_sideBar.OpenKey != "inhabitants")
        {
            _sideBar.Toggle("inhabitants");
        }

        var rows = _feed?.Inhabitants() ?? [];
        if (rows.Count > 0)
        {
            OpenCard(rows[0].Id);
        }

        Layout();
    }

    /// <summary>
    /// The open questions (Marcel W1696): per question who asks, the question, what brings the inhabitant to
    /// it, and a field to answer (a sentence to the inhabitant, checked by the core like every sentence).
    /// Rebuilt only when the set of questions changes; a half-typed answer is kept.
    /// </summary>
    private void RefreshQuestions(IReadOnlyList<InhabitantRow> rows)
    {
        if (_feed is null || _style is null)
        {
            return;
        }

        var locale = TranslationServer.GetLocale();
        if (locale != _questionShape)
        {
            // First build or a new language: start again (the texts are filled in).
            _questionShape = locale;
            foreach (var child in _questionList.GetChildren())
            {
                _questionList.RemoveChild(child);
                child.QueueFree();
            }

            _questionEntries.Clear();
            _questionsEmpty = new Label { Text = "ui.questions.empty_text", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _questionsLater = new Label { Text = "ui.question.later", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _questionList.AddChild(_questionsEmpty);
            _questionList.AddChild(_questionsLater);
        }

        var open = _feed.Questions().OrderBy(q => q.AskedTick).ThenBy(q => q.Id, StringComparer.Ordinal).ToList();
        SyncQuestions(_questionEntries, _questionList, open, rows, _style.Tokens.Window.QuestionsShown, withName: true, _sideBar.PanelOf("questions"));
        _questionList.MoveChild(_questionsLater!, -1);
        _questionsEmpty!.Visible = _questionEntries.Count == 0;
        _questionsLater!.Visible = _questionEntries.Count > 0;
    }

    /// <summary>
    /// Keeps a list of question entries in step with the open questions without ever moving what the player
    /// reads (Marcel W1726: "dann lese ich mir das durch ... dann ist das schon wieder weg"): new questions are
    /// appended at the end; a question that is over stays, marked as settled, and leaves only once the player
    /// is no longer at <paramref name="place"/> (mouse outside, no field of it focused). The oldest stay on top.
    /// </summary>
    private void SyncQuestions(Dictionary<string, QuestionEntry> entries, VBoxContainer list, IReadOnlyList<QuestionRow> open, IReadOnlyList<InhabitantRow> rows, int most, bool withName, Control place)
    {
        var busy = InUse(place);
        foreach (var (id, entry) in entries.Where(e => open.All(q => q.Id != e.Key)).ToList())
        {
            if (!entry.Settled)
            {
                entry.Settled = true;
                entry.Input.Editable = false;
                entry.Send.Disabled = true;
                entry.Box.AddChild(new Label { Text = "ui.question.settled", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            }

            if (!busy)
            {
                list.RemoveChild(entry.Box);
                entry.Box.QueueFree();
                entries.Remove(id);
            }
        }

        foreach (var question in open)
        {
            if (entries.Count >= most)
            {
                break;
            }

            if (!entries.ContainsKey(question.Id) && rows.FirstOrDefault(r => r.Id == question.InhabitantId) is { } row)
            {
                var entry = BuildQuestion(question, row, withName);
                list.AddChild(entry.Box);
                entries[question.Id] = entry;
            }
        }
    }

    /// <summary>Whether the player is at a control: the mouse is over it or one of its fields has the focus.</summary>
    private bool InUse(Control place) =>
        place.IsVisibleInTree()
        && (place.GetGlobalRect().HasPoint(place.GetGlobalMousePosition()) || (GetViewport().GuiGetFocusOwner() is { } focus && place.IsAncestorOf(focus)));

    /// <summary>
    /// One question as the inhabitant's own musing (Marcel W1727: "Hanno fragt sich"): who (clickable, when
    /// <paramref name="withName"/>), what it wonders about, what brought it there, and a field to whisper
    /// something to it - the player may step in but is not asked to.
    /// </summary>
    private QuestionEntry BuildQuestion(QuestionRow question, InhabitantRow row, bool withName)
    {
        var args = new Dictionary<string, string>
        {
            ["name"] = DisplayName(row),
            ["thing"] = question.TypeId is { } type ? Tr(VocabKeys.ObjectType(type)) : string.Empty,
        };

        // A kind without its own text gets the general one; each in the form for the inhabitant's sex.
        var sex = _feed?.SexOf(row.Id);
        string Text(string key, string other)
        {
            var text = TrFor(key, sex);
            return TextFill.Fill(text == key ? TrFor(other, sex) : text, args);
        }

        var box = new VBoxContainer();
        var id = row.Id;
        if (withName)
        {
            var who = new Button { Text = DisplayName(row), Flat = true, Alignment = HorizontalAlignment.Left, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
            who.Pressed += () => ShowInhabitant(id);
            box.AddChild(who);
        }

        box.AddChild(new Label { Text = Text(QuestionKeys.Ask(question.Kind), QuestionKeys.AskOther), ThemeTypeVariation = UiTheme.SubheaderLabel, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        box.AddChild(new Label { Text = Text(QuestionKeys.Reason(question.Kind), QuestionKeys.ReasonOther), AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart });

        var input = new LineEdit
        {
            PlaceholderText = TrFor("ui.question.answer_hint", sex),
            MaxLength = 280,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
        };
        var send = new Button { Text = "ui.question.answer" };
        void Answer()
        {
            if (Say(id, input.Text))
            {
                input.Clear();
            }
        }

        input.TextSubmitted += _ => Answer();
        send.Pressed += Answer;
        var answerRow = new HBoxContainer();
        answerRow.AddChild(input);
        answerRow.AddChild(send);
        box.AddChild(answerRow);
        box.AddChild(new HSeparator());
        return new QuestionEntry(box, input, send);
    }

    /// <summary>The controls of one question in a list.</summary>
    private sealed class QuestionEntry(VBoxContainer box, LineEdit input, Button send)
    {
        public VBoxContainer Box { get; } = box;

        public LineEdit Input { get; } = input;

        public Button Send { get; } = send;

        /// <summary>The question is over (answered by the AI or solved by the inhabitant); the entry waits to leave.</summary>
        public bool Settled { get; set; }
    }

    /// <summary>The conversation log (W1696): newest first, a click leads to the inhabitant.</summary>
    private void RefreshLog(IReadOnlyList<InhabitantRow> rows)
    {
        if (_log is null || _log.Version == _logShown)
        {
            return;
        }

        _logShown = _log.Version;
        foreach (var child in _logList.GetChildren())
        {
            _logList.RemoveChild(child);
            child.QueueFree();
        }

        var lines = _log.Newest();
        if (lines.Count == 0)
        {
            _logList.AddChild(new Label { Text = "ui.log.empty", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            return;
        }

        foreach (var line in lines)
        {
            if (rows.FirstOrDefault(r => r.Id == line.InhabitantId) is not { } row)
            {
                continue;
            }

            var key = line.Kind switch
            {
                LogLineKind.Thought => "ui.log.thought",
                LogLineKind.Said => "ui.log.said",
                LogLineKind.Player => "ui.log.player",
                LogLineKind.NoAnswer => "ui.log.no_answer",
                LogLineKind.Declined => "ui.log.declined",
                LogLineKind.Knock => "ui.log.knock",
                _ when line.Text.Length > 0 => "ui.log.not_understood_text",
                _ => "ui.log.not_understood",
            };
            var text = TextFill.Fill(Tr(key), new Dictionary<string, string> { ["name"] = DisplayName(row), ["text"] = line.Text });
            if (line.Count > 1)
            {
                text = TextFill.Fill(Tr("ui.log.again"), new Dictionary<string, string> { ["line"] = text, ["count"] = Number(line.Count) });
            }

            var button = new Button
            {
                Text = text,
                Flat = true,
                Alignment = HorizontalAlignment.Left,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            var id = row.Id;
            button.Pressed += () => ShowInhabitant(id);
            _logList.AddChild(button);
        }
    }

    /// <summary>
    /// The compute night's picture (B7, B2579/B2580, D637 R12/R14): darkens into the dream scene when the world's
    /// day phase turns to night, shows how far the service is, and lets the morning fade in once it is done.
    /// Real time only: a paused game holds the fade.
    /// </summary>
    /// <summary>
    /// Real seconds for the night scene this frame: while the night holds the world it runs on its own clock
    /// (the world's speed is 0 then), else only while the world runs; the pause menu stops it either way.
    /// </summary>
    private double NightDelta(double delta) =>
        _menuPaused ? 0 : _worldHeld ? delta : _presenter?.Speed > 0 ? delta : 0;

    /// <summary>
    /// B2680 (Marcel W1939): in the compute night everything sleeps, only the AI works. While the scene stands the
    /// world stands still (speed 0), the camera and the bars, side windows, card and hints rest; only Esc and the
    /// pause menu stay. When the morning has faded in, all comes back as it was.
    /// </summary>
    private void HoldWorld(bool hold)
    {
        if (_presenter is null)
        {
            return;
        }

        if (hold == _worldHeld)
        {
            // Nothing else may start the world while it is held (a check run, a late button event).
            if (hold && _presenter.Speed != 0 && !_menuPaused)
            {
                _presenter.Speed = 0;
            }

            return;
        }

        _worldHeld = hold;
        if (hold)
        {
            _speedBeforeNight = _presenter.Speed;
            _presenter.Speed = 0;
            _cameraHeldBeforeNight = _presenter.CameraHeld;
            _presenter.CameraHeld = true;
            _restingUi.Clear();
            foreach (var control in RestingControls().Where(c => c.Visible))
            {
                _restingUi.Add(control);
                control.Visible = false;
            }

            return;
        }

        _presenter.CameraHeld = _cameraHeldBeforeNight;
        if (!_menuPaused && _presenter.Speed == 0)
        {
            _presenter.Speed = _speedBeforeNight;
        }

        foreach (var control in _restingUi.Where(IsInstanceValid))
        {
            control.Visible = true;
        }

        _restingUi.Clear();
    }

    /// <summary>What rests in the compute night: the bars, the side windows (and the card) and the hints.</summary>
    private IEnumerable<Control> RestingControls()
    {
        if (_topBar is not null)
        {
            yield return _topBar;
        }

        yield return _sideBar;
        yield return _themeBar;
        foreach (var panel in _sideBar.Panels)
        {
            yield return panel;
        }

        yield return _toasts;
    }

    /// <summary>Whether the compute night holds the world now (B2680).</summary>
    public bool WorldHeldByNight => _worldHeld;

    private void UpdateNight(double delta)
    {
        if (_night is null || _feed is null || _presenter is null)
        {
            return;
        }

        // The core's own flag (GW1 #205): null until a new or loaded world has decided it.
        var night = _feed.Snapshot.ComputeNight;
        var setup = _presenter.SetupPort;
        if (_night.SceneShown && setup is not null && !_nightAsking && (_nightPoll -= delta) <= 0)
        {
            _nightPoll = _nightPollS;
            _nightAsking = true;
            AskNight(setup);
        }

        if (!_night.SceneShown)
        {
            _nightStatus = null;
            _nightUnanswered = 0;
        }

        var done = NightScene.ServiceDone(_nightLook!, setup is not null, _nightStatus, _nightUnanswered);

        // B2680: the scene waits until everyone sleeps, at the latest the data's hours after nightfall.
        var tick = _presenter.Host!.World.Tick;
        _nightfallTick = night == true ? _nightfallTick ?? tick : null;
        var hoursIntoNight = (tick - (_nightfallTick ?? tick)) / (double)Math.Max(1, _presenter.Host.World.Calendar.Settings.TicksPerHour);
        var allAsleep = _feed.Snapshot.Inhabitants.All(i => i.Action is { } a && _nightLook!.SleepActions.Contains(a));
        var sceneDelta = NightDelta(delta);
        _night.Update(night, done, sceneDelta, allAsleep, hoursIntoNight);
        HoldWorld(_night.HoldsWorld);
        _nightShade.Color = new Color(0, 0, 0, (float)_night.Darkness);

        // Only while the night is dark: by day the shade is invisible and the order does not matter, so the
        // screen's children are not walked every frame (review GW1 #258).
        if (_night.Darkness > 0)
        {
            RaiseNight();
        }

        _nightText.Visible = _night.SceneShown;
        _nightText.Modulate = new Color(1, 1, 1, (float)Math.Clamp(_night.Darkness / Math.Max(_nightDarkness, 0.01), 0, 1));
        ShowSleepers(sceneDelta);
        if (_dreams is not null && _style is not null)
        {
            if (!_night.SceneShown)
            {
                _dreams.Clear();
                _dreamFailed.Clear();
            }

            var scene = (float)Math.Clamp(_night.Darkness / Math.Max(_nightDarkness, 0.01), 0, 1);
            ShowDreams(_dreams.Update(_night.SceneShown ? sceneDelta : 0, _style.Settings.ReduceMotion), scene);
        }

        if (_nightText.Visible)
        {
            _nightProgress.Text = _nightStatus is { } s
                ? TextFill.Fill(Tr("ui.night.progress"), new Dictionary<string, string> { ["done"] = Number(s.DoneTonight), ["open"] = Number(s.Queued + s.Running) })
                : Tr("ui.night.progress_unknown");
        }
    }

    private async void AskNight(GlassWorld.AiClient.HttpSetupPort setup)
    {
        var answer = await setup.PhaseAsync();
        if (IsInstanceValid(this))
        {
            // A question the service did not answer counts; enough of them in a row end the scene (A1586).
            _nightUnanswered = answer.Status is null ? _nightUnanswered + 1 : 0;
            _nightStatus = answer.Status ?? _nightStatus;
        }

        // What the night's jobs are about (GW2 #206), page by page while full pages come.
        for (var pages = 0; pages < 8 && IsInstanceValid(this) && _dreams is { } dreams; pages++)
        {
            var list = await setup.NightItemsAsync(dreams.Since);
            if (list.Items is null || !IsInstanceValid(this) || !dreams.Take(list.Items))
            {
                break;
            }
        }

        _nightAsking = false;
    }

    /// <summary>
    /// Draws the dream images (B2579, D637 R11/R12): the shape of an object type in a small picture of its own,
    /// or the picture a job made; each at its place with its opacity, behind the bars and windows.
    /// </summary>
    private void ShowDreams(IReadOnlyList<DreamImage> images, float scene)
    {
        if (_style is null || _nightLook is null)
        {
            return;
        }

        var view = GetViewport().GetVisibleRect().Size;
        var size = (float)(_nightLook.Dreams.SizePx * _style.ContentScaleFactor);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            if (!_dreamNodes.TryGetValue(image.Key, out var node))
            {
                // An image that cannot be built (no shape, no picture) is tried once, not every frame (review GW1 #213).
                node = _dreamFailed.Contains(image.Key) ? null : DreamNode(image, size);
                if (node is null)
                {
                    _dreamFailed.Add(image.Key);
                    continue;
                }

                node.MouseFilter = Control.MouseFilterEnum.Ignore;
                _root.AddChild(node);
                _root.MoveChild(node, _nightShade.GetIndex() + 1);
                _dreamNodes[image.Key] = node;
            }

            seen.Add(image.Key);
            node.Position = new Vector2(((float)image.X * view.X) - (size / 2), ((float)image.Y * view.Y) - (size / 2)).Floor();
            node.Modulate = new Color(1, 1, 1, (float)image.Alpha * scene);
        }

        foreach (var (key, node) in _dreamNodes.Where(n => !seen.Contains(n.Key)).ToList())
        {
            node.QueueFree();
            _dreamNodes.Remove(key);
        }
    }

    /// <summary>The control of one dream image: the made picture if it can be read, else the object type's shape; null if neither exists.</summary>
    private Control? DreamNode(DreamImage image, float size)
    {
        // The file name is checked by the contract (a plain name, no path); a missing or broken file falls back to the shape.
        if (image.ImageFile is { } file && System.IO.File.Exists(System.IO.Path.Combine(_dreamFolder, file)))
        {
            var picture = Image.LoadFromFile(System.IO.Path.Combine(_dreamFolder, file));
            if (picture is not null && !picture.IsEmpty())
            {
                return new TextureRect
                {
                    Texture = ImageTexture.CreateFromImage(picture),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    Size = new Vector2(size, size),
                };
            }
        }

        if (image.ObjectType is not { } type || _presenter?.ShapeOfType(type) is not { } shape)
        {
            return null;
        }

        // The shape alone in a small world of its own: lit softly, turned a little, framed by its bounds.
        var box = shape.Mesh.GetAabb();
        var reach = Math.Max(0.05f, box.Size.Length() / 2);
        var viewport = new SubViewport { OwnWorld3D = true, TransparentBg = true, Size = new Vector2I((int)size, (int)size), RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        viewport.AddChild(new MeshInstance3D { Mesh = shape.Mesh, MaterialOverride = shape.Material, Position = -box.GetCenter(), RotationDegrees = new Vector3(0, 30, 0) });
        var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = reach * 2.2f, Position = new Vector3(0, reach * 0.6f, reach * 3), Near = 0.01f, Far = reach * 8 };
        viewport.AddChild(camera);
        camera.LookAtFromPosition(camera.Position, Vector3.Zero, Vector3.Up);
        viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45, -30, 0), LightEnergy = 1.1f });
        var container = new SubViewportContainer { Stretch = true, Size = new Vector2(size, size), CustomMinimumSize = new Vector2(size, size) };
        container.AddChild(viewport);
        return container;
    }

    /// <summary>
    /// Smoke only (D637 A8): a made-up night list with one job about a real object type shows one dream image
    /// with the type's shape; an empty list shows none. Leaves nothing behind.
    /// </summary>
    public string CheckDreams()
    {
        if (_nightLook is null || _style is null || _presenter?.Host is not { } host)
        {
            return "night scene not set up";
        }

        var type = host.Objects.All.Select(o => o.TypeId).FirstOrDefault(t => _presenter.ShapeOfType(t) is not null);
        var dreams = new DreamImages(_nightLook.Dreams);
        var list = new NightItems { ContractVersion = ContractInfo.Version, Session = "smoke", Phase = NightItemsPhase.Night };
        ShowDreams(dreams.Update(1, false), 1);
        if (_dreamNodes.Count != 0)
        {
            return "an empty night list shows a dream image";
        }

        if (type is null)
        {
            return string.Empty; // the placeholder world has no object shapes: nothing to show, nothing wrong
        }

        list.Items.Add(new NightItem { Seq = 1, JobId = "smoke-job-0001", Kind = JobKind.Question, State = JobState.Queued, Inhabitant = null, ObjectType = type, Image = null });
        dreams.Take(list);
        ShowDreams(dreams.Update(1, false), 1);
        var shown = _dreamNodes.Count == 1 && _dreamNodes.Values.Single() is SubViewportContainer;
        ShowDreams([], 1);
        return shown && dreams.Shown.SequenceEqual(["smoke-job-0001"]) ? string.Empty : "a job about an object type did not show its shape";
    }

    /// <summary>
    /// 'Zzz' above every sleeping inhabitant while the dream scene shows (D637 R12): screen text at the head,
    /// rising and fading slowly (still with 'reduce motion'); it fades in and out with the scene.
    /// </summary>
    private void ShowSleepers(double delta)
    {
        if (_night is null || _nightLook is null || _feed is null || _style is null)
        {
            return;
        }

        var camera = GetViewport().GetCamera3D();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        if (_night.SceneShown && camera is not null)
        {
            _nightClock += delta;
            var still = _style.Settings.ReduceMotion || _nightLook.ZzzSwayS <= 0;
            var cycle = still ? 0 : (_nightClock % _nightLook.ZzzSwayS) / _nightLook.ZzzSwayS;
            var alpha = (float)Math.Clamp(_night.Darkness / Math.Max(_nightDarkness, 0.01), 0, 1);
            foreach (var who in _feed.Snapshot.Inhabitants.Where(i => i.Action is { } a && _nightLook.SleepActions.Contains(a)))
            {
                if (_presenter?.MarkAnchor(who.Id, (float)_style.Tokens.Window.MarkHeightM) is not { } anchor)
                {
                    continue;
                }

                var head = anchor.Parent.GlobalPosition + new Vector3(0, anchor.HeightM + (float)_nightLook.ZzzAboveHeadM, 0);
                if (camera.IsPositionBehind(head))
                {
                    continue;
                }

                if (!_zzz.TryGetValue(who.Id, out var label))
                {
                    label = new Label { Text = "ui.night.zzz", ThemeTypeVariation = UiTheme.OnDarkHeaderLabel, MouseFilter = Control.MouseFilterEnum.Ignore };
                    _root.AddChild(label);
                    _root.MoveChild(label, _nightShade.GetIndex() + 1);
                    _zzz[who.Id] = label;
                }

                var rise = (float)(cycle * _nightLook.ZzzRisePx * _style.ContentScaleFactor);
                label.Position = (camera.UnprojectPosition(head) - new Vector2(label.Size.X / 2, label.Size.Y + rise)).Floor();
                label.Modulate = new Color(1, 1, 1, alpha * (float)(still ? 1 : 1 - (0.6 * cycle)));
                label.Visible = true;
                shown.Add(who.Id);
            }
        }

        foreach (var (id, label) in _zzz.Where(z => !shown.Contains(z.Key)).ToList())
        {
            label.QueueFree();
            _zzz.Remove(id);
        }
    }

    /// <summary>
    /// The whole scene fades into the dream scene (B2579/B2580, Marcel W1835): the shade lies over the bars,
    /// the log and every side window too; only the night's own things stay above it - its words, the "Zzz"
    /// and the dream images. Windows over the game (pause, settings) are another layer and stay as they are.
    /// Checked every frame because windows of the screen are added later (the card); it moves nothing while
    /// the order already holds. The shade takes no mouse input, so everything under it stays usable.
    /// </summary>
    private void RaiseNight()
    {
        bool OfTheNight(Node node) => node == _nightText || (node is Label label && _zzz.ContainsValue(label)) || (node is Control control && _dreamNodes.ContainsValue(control));
        var children = _root.GetChildren();
        var shade = _nightShade.GetIndex();
        if (!children.Skip(shade + 1).Any(c => !OfTheNight(c)) && _nightText.GetIndex() == children.Count - 1)
        {
            return;
        }

        var own = children.Where(OfTheNight).Where(c => c != _nightText).ToList();
        _root.MoveChild(_nightShade, -1);
        foreach (var node in own)
        {
            _root.MoveChild(node, -1);
        }

        _root.MoveChild(_nightText, -1);
    }

    /// <summary>Night pictures only: how many dream images are on screen.</summary>
    public int DreamsShownForShots => _dreamNodes.Count;

    /// <summary>Night pictures only: how many of them are pictures a job made (the rest are object shapes; A1586).</summary>
    public int DreamPicturesShownForShots => _dreamNodes.Values.Count(n => n is TextureRect);

    /// <summary>Night pictures only: the words of the night scene as shown (empty while hidden).</summary>
    public string NightTextForShots => _nightText.Visible ? _nightProgress.Text : string.Empty;

    /// <summary>Smoke only: the night's stage and darkness now.</summary>
    public (NightStage Stage, double Darkness) NightNow => _night is null ? (NightStage.Day, 0) : (_night.Stage, _night.Darkness);

    /// <summary>Rebuilds lists, clock, card and question marks from the current snapshot.</summary>
    public void Refresh()
    {
        if (_feed is null || _date is null || _time is null)
        {
            return;
        }

        RefreshChronicle();
        RefreshHelp();
        RefreshMail();

        var rows = _feed.Inhabitants();
        _activities = Activities(rows);
        // The activity column is a text column: GwTable sorts it by its text in the game's language (the 0 is never read).
        _inhabitants.SetRows([.. rows.Select(r => new GwRow(r.Id, [ListName(r), _activities[r.Id], Number(r.WillToLivePercent), Number(r.OpenQuestions)], [r.Number, 0, r.WillToLivePercent, r.OpenQuestions]))]);
        RefreshQuestions(rows);
        RefreshLog(rows);

        var clock = _feed.Clock();
        _date.Text = TextFill.Fill(Tr("ui.clock.date"), new Dictionary<string, string>
        {
            ["season"] = Tr(clock.SeasonKey),
            ["day"] = Number(clock.Day),
            ["year"] = Number(clock.Year),
        });
        _time.Text = clock.Time;

        // Each inhabitant's diary (W1726): the core keeps it (TL #20472); take what is new.
        if (_presenter?.Host is { } diaryHost)
        {
            _diary?.Sync(diaryHost.Diary);
        }

        foreach (var row in rows)
        {
            UpdateMark(row);
        }

        if (_cardId is not null)
        {
            FillCard(_cardId);
        }
    }

    /// <summary>
    /// Smoke run only: lists show every inhabitant, the clock is translated, a click opens the card and
    /// becomes a question the screen shows. Waits for the presenter's own ticks (never steps the world).
    /// </summary>
    /// <returns>Empty if fine, otherwise the problem.</returns>
    public async Task<string> SelfCheckAsync()
    {
        if (_feed is null || _date is null || _presenter?.Host is null)
        {
            return "screen not set up";
        }

        Refresh();
        var rows = _feed.Inhabitants();
        if (rows.Count == 0 || _date.Text.Length == 0 || _date.Text.Contains("ui.clock", StringComparison.Ordinal))
        {
            return "clock or inhabitant list empty";
        }

        var id = rows[0].Id;
        Encounter(id);
        var speed = _presenter.Speed;
        _presenter.Speed = 1;
        var startTick = _presenter.Host.World.Tick;
        for (var frame = 0; frame < 600 && _presenter.Host.World.Tick <= startTick; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        _presenter.Speed = speed;
        Refresh();
        if (_cardId != id || _card is null || !_card.IsInsideTree())
        {
            return "card did not open";
        }

        var mark = MarkOf(id);
        var puzzling = _feed.Inhabitants().First(r => r.Id == id).PuzzlingQuestions;
        if (puzzling > 0 && (mark is null || !mark.Visible))
        {
            return "question mark missing";
        }

        if (puzzling == 0 && mark is { Visible: true })
        {
            return "question mark shown although nothing puzzles the inhabitant";
        }

        // D705 B7: a modal window darkens the whole picture behind it, and the night's shade spans it too.
        var probe = new GwWindow();
        probe.Setup("ui.pause.title", _style!);
        ShowWindow(probe, modal: true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var view = GetViewport().GetVisibleRect().Size;
        var dim = _overlay.GetNodeOrNull<ColorRect>("ModalDim");
        var dimSize = dim?.Size ?? Vector2.Zero;
        var dimmed = dim is not null && dimSize.X >= view.X - 1 && dimSize.Y >= view.Y - 1 && dim.Color.A > 0;
        var shaded = _nightShade.Size.X >= view.X - 1 && _nightShade.Size.Y >= view.Y - 1;
        probe.Close();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        // The shade lies over the bars and side windows too, the night's words above it (Marcel W1835).
        RaiseNight();
        var above = _root.GetChildren().Where(c => c.GetIndex() > _nightShade.GetIndex()).ToList();
        if (_topBar is null || _nightShade.GetIndex() < _topBar.GetIndex() || _nightShade.GetIndex() < _sideBar.GetIndex() || above.Count == 0 || above[^1] != _nightText)
        {
            return $"the night shade does not lie over the bars with its words on top (shade at {_nightShade.GetIndex()} of {_root.GetChildCount()})";
        }

        // The words of the night scene stand on the shade: they must be light (seen dark on dark in run L854).
        foreach (var words in _nightText.GetChildren().OfType<Label>())
        {
            if (words.GetThemeColor("font_color").Luminance < 0.5f)
            {
                return $"the night scene's text '{words.Text}' is dark on the dark shade (luminance {words.GetThemeColor("font_color").Luminance:0.00})";
            }
        }

        if (!dimmed || !shaded)
        {
            return $"the picture behind a modal window is not dimmed (dim {dimSize}, night shade {_nightShade.Size}, view {view})";
        }

        // W1696: an open question shows its wording and an answer field in the questions panel.
        Refresh();
        if (_feed.Questions().Count > 0 && _questionEntries.Count == 0)
        {
            return "questions panel shows no question with an answer field";
        }

        var chronicle = CheckChronicle();
        if (chronicle.Length > 0)
        {
            return chronicle;
        }

        var hints = await CheckToastsInsideAsync();
        if (hints.Length > 0)
        {
            return hints;
        }

        // The theme must reach the screen below its CanvasLayer (frames, fonts, text size), not only the
        // root window (Godot does not pass themes through a CanvasLayer).
        if (UiSetup.CurrentTheme is { } theme && _card.GetThemeStylebox("panel", UiTheme.WindowPanel) != theme.GetStylebox("panel", UiTheme.WindowPanel))
        {
            return "the UI theme does not reach the game screen";
        }

        // Text size too, not only the frames (GW1 review #65 T3).
        var label = new Label();
        _card.Content.AddChild(label);
        var size = label.GetThemeFontSize("font_size");
        label.Free();
        if (_style is not null && size != _style.FontPx(UiFontSizeId.Body))
        {
            return $"label text size {size} px, theme says {_style.FontPx(UiFontSizeId.Body)} px";
        }

        return await CheckCardPlacementAsync();
    }

    /// <summary>
    /// W1934/W1744: hints that come after the layout stand wholly inside the picture (they hung below the
    /// bottom edge with only their top border to see). Pushes two hints, waits two frames, checks, closes them.
    /// </summary>
    private async Task<string> CheckToastsInsideAsync()
    {
        if (_board is null || _style is null)
        {
            return "hint board missing";
        }

        var before = _board.Visible.Select(t => t.Id).ToHashSet();
        _board.Push(new ToastMessage("msg.mail.kind.knock", new Dictionary<string, string>(), "check-1"), fastForward: false);
        _board.Push(new ToastMessage("msg.mail.kind.knock", new Dictionary<string, string>(), "check-2"), fastForward: false);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var view = GetViewport().GetVisibleRect().Size;
        var margin = _style.Tokens.Window.SafeMargin;
        var rect = _toasts.GetRect();
        var shown = _toasts.GetChildCount();
        foreach (var toast in _board.Visible.Where(t => !before.Contains(t.Id)))
        {
            _board.Dismiss(toast.Id);
        }

        if (shown == 0 || rect.Size.Y <= 0)
        {
            return "the hint area shows no hint";
        }

        return rect.Position.Y >= margin - 1 && rect.End.Y <= view.Y - margin + 1 && rect.Position.X >= margin - 1 && rect.End.X <= view.X - margin + 1
            ? string.Empty
            : $"the hints leave the picture: {rect.Position}+{rect.Size} in {view} (safe margin {margin})";
    }

    /// <summary>
    /// The card stays inside the safe area and beside the open side panel at the smallest and largest UI
    /// size (found in the 3840x1600 screenshots: the card kept its old place after a size change).
    /// </summary>
    /// <summary>
    /// Smoke run only: the zones preview (W1717, D642 step 1). The top bar spans the screen, the log is pinned
    /// on the left, a theme button opens its side window on the right, a tab switches inside the theme, the card
    /// and a side window never show together, nothing overlaps and the middle stays free. Leaves the layout
    /// as <paramref name="settings"/> say. Empty when all holds.
    /// </summary>
    public async Task<string> CheckZonesAsync(PlayerSettings settings)
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
            Layout();
        }

        static Rect2 Box(Control c) => new(c.Position, c.Size);

        _card?.Close();
        ApplySettings(settings with { ZonesPreview = true }, _style);
        await Frames();
        try
        {
            var view = GetViewport().GetVisibleRect().Size;
            var middle = new Vector2(view.X / 2, view.Y / 2);
            var log = _sideBar.PanelOf(ScreenZones.Pinned);
            if (!log.Visible || log.Position.X + log.Size.X > middle.X)
            {
                return "the log is not pinned on the left";
            }

            if (_topBar.Size.X < view.X - (2 * _style.Tokens.Window.SafeMargin) - 1 || _sideBar.Visible || !_themeBar.Visible)
            {
                return "top bar or theme buttons are not in the zones layout";
            }

            _themeButtons["contact"].EmitSignal(BaseButton.SignalName.Pressed);
            await Frames();
            if (_sideBar.OpenKey != "questions")
            {
                return $"the contact theme opened '{_sideBar.OpenKey}'";
            }

            _sideBar.PanelOf("questions").Tabs.GetChildren().OfType<Button>().Last().EmitSignal(BaseButton.SignalName.Pressed);
            await Frames();
            if (_sideBar.OpenKey != "mailbox" || !log.Visible)
            {
                return $"the last contact tab opened '{_sideBar.OpenKey}' (log visible: {log.Visible})";
            }

            var open = _sideBar.PanelOf("mailbox");
            var boxes = new (string Name, Rect2 Box)[] { ("top bar", Box(_topBar)), ("log", Box(log)), ("side window", Box(open)), ("theme buttons", Box(_themeBar)) };
            foreach (var (a, b) in boxes.SelectMany((a, i) => boxes.Skip(i + 1).Select(b => (a, b))))
            {
                if (a.Box.Intersects(b.Box))
                {
                    return $"{a.Name} {a.Box} overlaps {b.Name} {b.Box}";
                }
            }

            if (boxes.Any(b => b.Box.HasPoint(middle)) || open.Position.X < middle.X)
            {
                return "the middle of the picture is not free";
            }

            // Step 3: the world theme holds the chronicle and the timeline as tabs.
            _themeButtons["world"].EmitSignal(BaseButton.SignalName.Pressed);
            await Frames();
            var worldTabs = _sideBar.PanelOf("chronicle").Tabs.GetChildren().OfType<Button>().ToList();
            if (_sideBar.OpenKey != "chronicle" || worldTabs.Count != 2)
            {
                return $"the world theme opened '{_sideBar.OpenKey}' with {worldTabs.Count} tabs";
            }

            worldTabs[1].EmitSignal(BaseButton.SignalName.Pressed);
            await Frames();
            if (_sideBar.OpenKey != ScreenZones.Timeline || _timelineList.GetChildCount() == 0)
            {
                return "the timeline tab did not open or is empty";
            }

            // Step 2: the card is a tab of the people theme, in the side window's place, with the talk field.
            var who = _feed.Inhabitants()[0].Id;
            OpenCard(who);
            await Frames();
            var cardPanel = _sideBar.PanelOf(ScreenZones.Card);
            if (_card is not null || _sideBar.OpenKey != ScreenZones.Card || _cardId != who || _talkInput is null || !cardPanel.IsAncestorOf(_talkInput))
            {
                return "the card did not open as a tab of the people theme";
            }

            var card = Box(cardPanel);
            if (card.Intersects(Box(log)) || card.Intersects(Box(_topBar)) || card.HasPoint(middle) || _sideBar.Panels.Count(p => p.Visible && p != log) != 1)
            {
                return $"the card tab {card} is not alone in the side window's place";
            }

            cardPanel.Tabs.GetChildren().OfType<Button>().First().EmitSignal(BaseButton.SignalName.Pressed);
            await Frames();
            if (_sideBar.OpenKey != "inhabitants" || _cardId is not null)
            {
                return "the list tab did not replace the card";
            }

            // Back on the card tab: the inhabitant last shown is there again.
            _sideBar.PanelOf("inhabitants").Tabs.GetChildren().OfType<Button>().Last().EmitSignal(BaseButton.SignalName.Pressed);
            await Frames();
            return _sideBar.OpenKey == ScreenZones.Card && _cardId == who ? string.Empty : "the card tab did not bring back the inhabitant last shown";
        }
        finally
        {
            _sideBar.Open(null);
            ApplySettings(settings, _style);
            _lastCardId = null;
        }
    }

    private async Task<string> CheckCardPlacementAsync()
    {
        if (_style is null || _card is null)
        {
            return "card missing";
        }

        if (_sideBar.OpenKey != "inhabitants")
        {
            _sideBar.Toggle("inhabitants");
        }

        var original = _style;
        var problem = string.Empty;
        foreach (var scale in new[] { original.Tokens.UiScale.Max, original.Tokens.UiScale.Min })
        {
            UiSetup.Apply(GetTree().Root, original.With(original.Settings with { UiScale = scale }));
            for (var i = 0; i < 5; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            var area = GetViewport().GetVisibleRect().Size;
            var cardRect = new Rect2(_card.Position, _card.Size);
            var foreground = CardArea(area).X >= area.X;
            var overlap = (!foreground && _sideBar.Panels.Any(p => p.Visible && new Rect2(p.Position, p.Size).Intersects(cardRect)))
                || (_topBar is not null && new Rect2(_topBar.Position, _topBar.Size).Intersects(cardRect));
            if (!_card.IsInsideSafeArea(area) || overlap)
            {
                problem = $"card at UI {scale:0.##}: outside the safe area or over the side panel ({cardRect} in {area})";
                break;
            }
        }

        UiSetup.Apply(GetTree().Root, original);
        return problem;
    }

    /// <summary>The free screen area for the bubbles over the heads: below the top bar, left of the side panel.</summary>
    private Rect2? BubbleArea()
    {
        if (_style is null || _topBar is null || !_topBar.IsInsideTree())
        {
            return null;
        }

        var view = GetViewport().GetVisibleRect().Size;
        var margin = _style.Tokens.Window.SafeMargin;
        var gap = _style.Tokens.Window.Gap;
        var top = _topBar.Position.Y + _topBar.Size.Y + gap;
        if (_zones)
        {
            // Between the pinned log on the left and whatever stands on the right (theme buttons, side window, card).
            var log = _sideBar.PanelOf(ScreenZones.Pinned);
            var from = log.Position.X + log.Size.X + gap;
            var to = _themeBar.Position.X - gap;
            foreach (var panel in _sideBar.Panels.Where(p => p.Visible && p != log))
            {
                to = Math.Min(to, panel.Position.X - gap);
            }

            return new Rect2(from, top, Math.Max(0, to - from), Math.Max(0, view.Y - margin - top));
        }

        var right = _sideBar.Position.X - gap;
        return new Rect2(margin, top, Math.Max(0, right - margin), Math.Max(0, view.Y - margin - top));
    }

    private void Layout()
    {
        if (_style is null || _topBar is null)
        {
            return;
        }

        var area = GetViewport().GetVisibleRect().Size;
        var margin = _style.Tokens.Window.SafeMargin;
        var gap = _style.Tokens.Window.Gap;
        if (_zones)
        {
            LayoutZones(area, margin, gap);
            return;
        }

        _sideBar.Position = new Vector2(area.X - margin - _sideBar.GetCombinedMinimumSize().X, margin);
        _topBar.Position = new Vector2(margin, margin);
        _topBar.Size = new Vector2(area.X - (2 * margin) - _sideBar.GetCombinedMinimumSize().X - gap, 0);
        var list = _sideBar.PanelOf("inhabitants");
        foreach (var panel in _sideBar.Panels)
        {
            panel.Dock(area, _style, _sideBar.GetCombinedMinimumSize().X + gap, contentPx: panel == list ? _inhabitants.WantedWidth : 0);
        }

        PlaceToasts();

        // The card stays beside the side bar and its open panel and is re-fitted when the UI size changes.
        FitCard(area);
    }

    /// <summary>
    /// The zones layout (W1717, D642): A top bar over the full width, B theme buttons and one side window (or
    /// the card) on the right below it, C the log on the left, D hints at the bottom centre; the middle is free.
    /// </summary>
    private void LayoutZones(Vector2 area, float margin, float gap)
    {
        _topBar!.Position = new Vector2(margin, margin);
        _topBar.Size = new Vector2(area.X - (2 * margin), 0);
        var top = margin + _topBar.GetCombinedMinimumSize().Y + gap;
        var themes = _themeBar.GetCombinedMinimumSize();
        _themeBar.Size = themes;
        _themeBar.Position = new Vector2(area.X - margin - themes.X, top);
        var log = _sideBar.PanelOf(ScreenZones.Pinned);
        foreach (var panel in _sideBar.Panels)
        {
            if (panel == log)
            {
                panel.Dock(area, _style!, 0, top, left: true, heightShare: (float)_style!.Tokens.Zones.LogHeightShare, widthPx: _style.Tokens.Zones.LogWidth);
            }
            else
            {
                panel.Dock(area, _style!, themes.X + gap, top, widthPx: _style!.Tokens.Zones.SidePanelWidth, contentPx: panel == _sideBar.PanelOf("inhabitants") ? _inhabitants.WantedWidth : 0);
            }
        }

        PlaceToasts();
        FitCard(area);
    }

    /// <summary>
    /// The hints stand on the bottom edge inside the safe margin and grow upwards: at the bottom right in the
    /// layout so far, at the bottom centre in the zones layout (D642 zone D).
    /// </summary>
    private void PlaceToasts()
    {
        if (_style is null)
        {
            return;
        }

        var area = GetViewport().GetVisibleRect().Size;
        var margin = _style.Tokens.Window.SafeMargin;
        var width = Math.Min(_style.Tokens.Window.SidePanelWidth, area.X - (2 * margin));
        _toasts.Size = new Vector2(width, 0);
        var x = _zones ? MathF.Floor((area.X - width) / 2) : area.X - margin - width;
        _toasts.Position = new Vector2(x, area.Y - margin - _toasts.Size.Y);
    }

    /// <summary>Screen part the card is centred in (left of the side bar and its open panel, see <see cref="ScreenModel.CardRoom"/>).</summary>
    /// <summary>
    /// Fits the card beside the side bar and below the top bar (A1335 screenshots: at 175 % it covered
    /// clock and time buttons); content that does not fit scrolls.
    /// </summary>
    private void FitCard(Vector2 area)
    {
        if (_card is null || _style is null || _topBar is null)
        {
            return;
        }

        var window = _style.Tokens.Window;
        var top = Math.Max(0, _topBar.Position.Y + _topBar.Size.Y + window.Gap - window.SafeMargin);
        _card.FitInto(new Vector2(CardArea(area).X, area.Y - top));

        // At the left edge, not in the middle of the picture (W1695): the world stays free to watch.
        _card.Position = new Vector2(window.SafeMargin, top + window.SafeMargin);
    }

    private Vector2 CardArea(Vector2 area)
    {
        if (_style is null || _card is null)
        {
            return area;
        }

        var gap = _style.Tokens.Window.Gap;
        var free = _sideBar.Position.X - gap;
        foreach (var panel in _sideBar.Panels.Where(p => p.Visible))
        {
            free = Math.Min(free, panel.Position.X - gap);
        }

        var width = ScreenModel.CardRoom(area.X, free, _card.GetCombinedMinimumSize().X, _style.Tokens.Window.SafeMargin);
        return new Vector2((float)width, area.Y);
    }

    /// <summary>
    /// A row of the inhabitant or question list (B2569): the card opens and the camera glides to the
    /// inhabitant and follows it, as after a click on the figure (until the player moves the camera or
    /// presses Esc). The selected row stays marked.
    /// </summary>
    private void ShowInhabitant(string inhabitantId)
    {
        OpenCard(inhabitantId);
        _presenter?.ChooseFigure(inhabitantId);
    }

    private void OpenCard(string inhabitantId)
    {
        if (_style is null)
        {
            return;
        }

        _card?.Close();
        if (_zones)
        {
            // One place on the right (D642 step 2): the card is a tab of the people theme, no window of its own.
            _cardId = inhabitantId;
            _lastCardId = inhabitantId;
            _cardShape = string.Empty;
            _sideBar.Open(ScreenZones.Card);
            FillCard(inhabitantId);
            Layout();
            return;
        }

        _card = new GwWindow();
        _card.Setup("ui.inhabitants.title", _style);
        _card.Closed += () =>
        {
            _card = null;
            _cardId = null;
        };
        _cardId = inhabitantId;
        _cardShape = string.Empty;
        _root.AddChild(_card);
        FillCard(inhabitantId);
        FitCard(GetViewport().GetVisibleRect().Size);
    }

    /// <summary>Where the card's rows go: its own window, or in the zones layout its tab of the people theme; null while no card shows.</summary>
    private VBoxContainer? CardContent =>
        _card is not null ? _card.Content
        : _zones && _cardId is not null && _sideBar.OpenKey == ScreenZones.Card ? _sideBar.PanelOf(ScreenZones.Card).Content
        : null;

    /// <summary>Closes the card, wherever it lives.</summary>
    private void CloseCard()
    {
        _card?.Close();
        _cardId = null;
        if (_zones && _sideBar.OpenKey == ScreenZones.Card)
        {
            ShowNoCard();
        }
    }

    /// <summary>The card tab without an inhabitant: one sentence that says how to choose one.</summary>
    private void ShowNoCard()
    {
        var panel = _sideBar.PanelOf(ScreenZones.Card);
        panel.Setup(ScreenZones.TitleKey(ScreenZones.Card));
        foreach (var child in panel.Content.GetChildren())
        {
            panel.Content.RemoveChild(child);
            child.QueueFree();
        }

        _cardShape = string.Empty;
        panel.Content.AddChild(new Label { Text = "ui.card.none", AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }

    private void FillCard(string inhabitantId)
    {
        var card = _feed?.Card(inhabitantId);
        var content = CardContent;
        if (content is null || card is null || _style is null)
        {
            CloseCard();
            return;
        }

        if (_card is not null)
        {
            _card.SetTitleText(DisplayName(card.Row));
        }
        else
        {
            _sideBar.PanelOf(ScreenZones.Card).SetTitleText(DisplayName(card.Row));
        }

        var values = new List<(string Key, int Percent)> { ("ui.card.will_to_live", card.Row.WillToLivePercent) };
        values.AddRange(card.Needs.Select(v => (v.Key, v.Percent)));
        values.AddRange(card.Feelings.Select(v => (v.Key, v.Percent)));
        values.AddRange(card.Traits.Select(v => (v.Key, v.Percent)));
        var shape = $"{inhabitantId}|{string.Join(',', card.Needs.Select(v => v.Faded ? v.Key + "~" : v.Key))}|{string.Join(',', card.Feelings.Select(v => v.Key))}|{string.Join(',', card.Traits.Select(v => v.Key))}";
        if (shape != _cardShape)
        {
            // Build only when the set of values changes (other inhabitant, a feeling appears/fades).
            _cardShape = shape;
            _cardBars.Clear();
            foreach (var child in content.GetChildren())
            {
                content.RemoveChild(child);
                child.QueueFree();
            }

            // The player's favourite (B6 R19): a switch at the top; a logged command, the player's own mark.
            var favourite = new CheckButton { Text = "ui.card.favourite", ButtonPressed = card.Row.Favourite };
            favourite.Toggled += on => _presenter?.Host?.Decisions.Submit(new Simulation.Decisions.PlayerCommand(
                on ? Simulation.Player.FavouritesSystem.Mark : Simulation.Player.FavouritesSystem.Unmark, inhabitantId));
            content.AddChild(favourite);

            // Its memory page (B8 R16, B6 R19): what the world kept about it.
            var memories = new Button { Text = "ui.card.memory" };
            memories.Pressed += () => OpenMemory(inhabitantId);
            content.AddChild(memories);

            // Order of the card (W1688, TL #19983): first what the inhabitant does and the talk field, well
            // visible; the values below in sections the player can fold (the choice is kept while playing).
            Section("ui.card.doing");
            _cardAction = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _cardQuestions = new Label { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };

            // An invention the inhabitant is busy with shows its icon before the line (A1603).
            var doing = new HBoxContainer();
            _cardActionIcon = IconRect();
            doing.AddChild(_cardActionIcon);
            doing.AddChild(_cardAction);
            content.AddChild(doing);
            content.AddChild(_cardQuestions);

            // Talking (B6 R13): a sentence to this inhabitant; the core checks it and answers later.
            Section("ui.card.talk");
            var talkRow = new HBoxContainer();
            var input = new LineEdit
            {
                PlaceholderText = "ui.card.talk_hint",
                MaxLength = 280,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            };
            input.PlaceholderText = Tr("ui.card.talk_hint");
            _talkInput = input;
            var send = new Button { Text = "ui.card.talk_send" };
            void Submit()
            {
                if (Say(inhabitantId, input.Text, _pointedType))
                {
                    input.Clear();
                    _pointedType = null;
                    UpdatePointing();
                }
            }

            input.TextSubmitted += _ => Submit();
            send.Pressed += Submit;

            // A1617 (B2678): 'Zeigen' - the next click into the world picks a thing the sentence points at.
            _pointedType = null;
            _pointingFor = null;
            _pointButton = new Button { Text = "ui.card.point", TooltipText = "ui.card.point_tooltip", ToggleMode = true };
            _pointButton.Pressed += () =>
            {
                _pointingFor = _pointingFor is null ? inhabitantId : null;
                UpdatePointing();
            };
            talkRow.AddChild(input);
            talkRow.AddChild(_pointButton);
            talkRow.AddChild(send);
            content.AddChild(talkRow);
            _pointedChip = new HBoxContainer { Visible = false };
            _pointedLabel = new Label { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            var unpoint = new Button { Text = "×", TooltipText = "ui.common.close" };
            unpoint.Pressed += () =>
            {
                _pointedType = null;
                _pointingFor = null;
                UpdatePointing();
            };
            _pointedChip.AddChild(_pointedLabel);
            _pointedChip.AddChild(unpoint);
            content.AddChild(_pointedChip);

            // What occupies this inhabitant (W1726): its open questions, here too, with the whisper field.
            _cardQuestionEntries.Clear();
            _cardQuestionBox = new VBoxContainer { Visible = false };
            _cardQuestionBox.AddChild(new Label { Text = TrFor("ui.card.musing", _feed?.SexOf(inhabitantId)), ThemeTypeVariation = UiTheme.SubheaderLabel, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled });
            content.AddChild(_cardQuestionBox);

            var wellbeing = Fold("ui.card.wellbeing", folded: false);
            Bar(wellbeing, "ui.card.will_to_live");
            foreach (var need in card.Needs)
            {
                Bar(wellbeing, need.Key, need);
            }

            var feelings = Fold("ui.card.feelings", folded: true);
            foreach (var feeling in card.Feelings)
            {
                Bar(feelings, feeling.Key);
            }

            var character = Fold("ui.card.character", folded: true);
            foreach (var trait in card.Traits)
            {
                Bar(character, trait.Key);
            }

            // What it did, one after the other (W1726); folded until the player wants to read it.
            _cardDiaryBox = Fold("ui.card.diary", folded: true);
            _cardDiaryShown = default;
        }

        foreach (var (key, percent) in values)
        {
            if (_cardBars.TryGetValue(key, out var bar) && (int)bar.Value != percent)
            {
                bar.Value = percent;
            }
        }

        if (_talkInput is not null && _talkInput.PlaceholderText != Tr("ui.card.talk_hint"))
        {
            _talkInput.PlaceholderText = Tr("ui.card.talk_hint");
        }

        var action = (_activities ?? Activities(_feed!.Inhabitants())).GetValueOrDefault(card.Row.Id) ?? Tr(InhabitantActivity.StandingKey);
        if (_cardActionIcon is not null)
        {
            var target = _feed?.Snapshot?.Inhabitants.FirstOrDefault(i => i.Id == card.Row.Id)?.TargetId;
            var icon = target is null ? null : InventionIcon(target);
            _cardActionIcon.Texture = icon;
            _cardActionIcon.Visible = icon is not null;
        }

        if (_cardAction is not null && _cardAction.Text != action)
        {
            _cardAction.Text = action;
        }

        if (_cardQuestionBox is not null && _feed is not null)
        {
            var own = _feed.Questions().Where(q => q.InhabitantId == inhabitantId).OrderBy(q => q.AskedTick).ThenBy(q => q.Id, StringComparer.Ordinal).ToList();
            SyncQuestions(_cardQuestionEntries, _cardQuestionBox, own, [card.Row], _style.Tokens.Window.QuestionsShown, withName: false, _cardQuestionBox);
            _cardQuestionBox.Visible = _cardQuestionEntries.Count > 0;
        }

        if (_cardDiaryBox is not null && _diary is not null && _cardDiaryShown != (inhabitantId, _diary.Version))
        {
            _cardDiaryShown = (inhabitantId, _diary.Version);
            foreach (var child in _cardDiaryBox.GetChildren())
            {
                _cardDiaryBox.RemoveChild(child);
                child.QueueFree();
            }

            var lines = _diary.Of(inhabitantId, key => Tr(key), NameOfId);
            if (lines.Count == 0)
            {
                _cardDiaryBox.AddChild(new Label { Text = "ui.card.diary_empty", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            }

            foreach (var line in lines)
            {
                var text = new Label
                {
                    Text = TextFill.Fill(Tr("ui.card.diary_line"), new Dictionary<string, string> { ["day"] = Number(line.Day), ["time"] = line.Time, ["text"] = line.Text }),
                    AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };

                // A line about an invention shows its icon before it (A1603).
                if (line.TargetType is { } type && InventionIcon(type) is { } icon)
                {
                    var row = new HBoxContainer();
                    var rect = IconRect();
                    rect.Texture = icon;
                    row.AddChild(rect);
                    row.AddChild(text);
                    _cardDiaryBox.AddChild(row);
                }
                else
                {
                    _cardDiaryBox.AddChild(text);
                }
            }
        }

        var questions = TextFill.Fill(TrFor("ui.card.questions", _feed?.SexOf(card.Row.Id)), new Dictionary<string, string> { ["count"] = Number(card.Row.OpenQuestions) });
        if (_cardQuestions is not null)
        {
            // A line that only says "0" tells nothing (D705 B10).
            _cardQuestions.Visible = card.Row.OpenQuestions > 0;
            if (_cardQuestions.Text != questions)
            {
                _cardQuestions.Text = questions;
            }
        }

        void Section(string key) => content.AddChild(new Label { Text = key, ThemeTypeVariation = UiTheme.SubheaderLabel });

        // A foldable section; folded or open as the player last left it (default as given).
        VBoxContainer Fold(string key, bool folded)
        {
            var section = new FoldableContainer { Title = key, Folded = _cardFolded.GetValueOrDefault(key, folded) };
            section.FoldingChanged += isFolded =>
            {
                _cardFolded[key] = isFolded;
                FitCard(GetViewport().GetVisibleRect().Size);
            };
            var rows = new VBoxContainer();
            section.AddChild(rows);
            content.AddChild(section);
            return rows;
        }

        void Bar(VBoxContainer into, string key, CardValue? value = null)
        {
            var row = new HBoxContainer();

            // A need an invention brought has no text key: its name from the need register, in the player's
            // language (A1595); run empty it is shown greyed (not met lately - it can do no harm, #290).
            var label = value?.Name is { } name
                ? new Label { Text = TranslationServer.GetLocale().StartsWith("de", StringComparison.Ordinal) ? name.De : name.En, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled }
                : new Label { Text = key };
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(label);
            if (value?.Faded == true)
            {
                row.Modulate = new Color(1, 1, 1, (float)(_style!.Tokens.Window.FadedAlpha ?? 0.5));
            }
            var bar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = true, CustomMinimumSize = new Vector2(_style!.Tokens.Window.CardBarWidth, 0) };
            row.AddChild(bar);
            into.AddChild(row);
            _cardBars[key] = bar;
        }
    }

    /// <summary>The name of an object type in the player's language: an invention by its own name, else its vocabulary word.</summary>
    private string ThingName(string type) =>
        _presenter?.Host?.Inventions.Invention(type) is { } invention
            ? TranslationServer.GetLocale().StartsWith("en", StringComparison.OrdinalIgnoreCase) ? invention.Name.En : invention.Name.De
            : Tr(VocabKeys.ObjectType(type));

    /// <summary>The pointing button and chip as the state says (A1617): waiting for a click, or the thing pointed at.</summary>
    private void UpdatePointing()
    {
        if (_pointButton is not null && IsInstanceValid(_pointButton))
        {
            _pointButton.SetPressedNoSignal(_pointingFor is not null);
        }

        if (_pointedChip is null || !IsInstanceValid(_pointedChip) || _pointedLabel is null)
        {
            return;
        }

        _pointedChip.Visible = _pointingFor is not null || _pointedType is not null;
        _pointedLabel.Text = _pointingFor is not null
            ? Tr("ui.card.point_waiting")
            : _pointedType is { } type
                ? TextFill.Fill(Tr("ui.card.pointed"), new Dictionary<string, string> { ["thing"] = ThingName(type) })
                : string.Empty;
    }

    /// <summary>Screenshots only: the card scrolled to its talk field with a sentence in it.</summary>
    public void ShowTalkForShots(string sentence)
    {
        if (_talkInput is not null)
        {
            _talkInput.Text = sentence;
        }
    }

    /// <summary>
    /// The player says something to an inhabitant (send_text): the core checks it at once; a refusal or
    /// "no favour" shows its calm reason. Returns true when the sentence was taken.
    /// </summary>
    public bool Say(string inhabitantId, string text, string? pointedType = null)
    {
        if (_presenter?.Host is not { } host)
        {
            return false;
        }

        // A1617: the type pointed at goes with the sentence (TypeId); the core keeps it only if the inhabitant sees it.
        var result = host.Decisions.Submit(new PlayerCommand(Simulation.Player.ConversationSystem.SendText, inhabitantId) { Text = text, TypeId = pointedType });
        if (!result.Accepted)
        {
            ShowHint(new ToastMessage(result.ReasonKey ?? "msg.command.unknown", new Dictionary<string, string>(), null));
        }

        return result.Accepted;
    }

    /// <summary>The question mark of an inhabitant (on the head anchor on the island), or null.</summary>
    private Label3D? MarkOf(string inhabitantId) =>
        _style is not null && _presenter?.MarkAnchor(inhabitantId, (float)_style.Tokens.Window.MarkHeightM) is { } anchor
            ? anchor.Parent.GetNodeOrNull<Label3D>(MarkNode)
            : null;

    private void UpdateMark(InhabitantRow row)
    {
        if (_style is null || _presenter?.MarkAnchor(row.Id, (float)_style.Tokens.Window.MarkHeightM) is not { } anchor)
        {
            return;
        }

        var figure = anchor.Parent;
        var mark = figure.GetNodeOrNull<Label3D>(MarkNode);
        if (mark is null)
        {
            // One size for all head symbols (F5284): the mark is head_symbol.size_m tall in the world and keeps
            // its screen height between the data's floor and ceiling at every zoom.
            var sizeM = _style.Tokens.HeadSymbol.SizeM;
            mark = new GlassWorld.Game.World.HeadSymbolLabel
            {
                Name = MarkNode,
                SizeM = sizeM,
                BaseHeightM = anchor.HeightM,
                Style = () => _style,
                Position = new Vector3(0, anchor.HeightM, 0),
                PixelSize = (float)(sizeM / _style.Tokens.Window.MarkFontPx),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FontSize = _style.Tokens.Window.MarkFontPx,
                Modulate = UiTheme.ToGodot(_style.Color(UiColorId.Attention)),
                OutlineModulate = UiTheme.ToGodot(_style.Color(UiColorId.Panel)),
                AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
                NoDepthTest = true,
            };
            figure.AddChild(mark);
        }

        // Only questions that still puzzle the inhabitant show above the head (TL #19921): one about a thing it
        // has looked at since leaves only the effect open; the questions panel still lists every open question.
        mark.Text = ScreenModel.QuestionMarks(row.PuzzlingQuestions);
        mark.Visible = row.PuzzlingQuestions > 0;
    }

    private string? PickAt(Vector2 screen)
    {
        var camera = GetViewport().GetCamera3D();
        if (camera is null || _presenter?.Host is null || _style is null)
        {
            return null;
        }

        var figures = (_feed?.Inhabitants() ?? [])
            .Select(i => (i.Id, Point: _presenter.PickPoint(i.Id)))
            .Where(f => f.Point is { } p && !camera.IsPositionBehind(p))
            .Select(f =>
            {
                var p = camera.UnprojectPosition(f.Point!.Value);
                return (f.Id, (double)p.X, (double)p.Y);
            });
        return ScreenModel.Pick(figures, screen.X, screen.Y, _style.Tokens.Window.PickRadiusPx);
    }

    /// <summary>
    /// What each inhabitant does, as a readable line (W1687: never a bare question mark); lookups are built
    /// once per refresh and the names come from <see cref="DisplayName"/>.
    /// </summary>
    private Dictionary<string, string> Activities(IReadOnlyList<InhabitantRow> rows)
    {
        if (_feed?.Snapshot is not { } snapshot)
        {
            return [];
        }

        var index = new SnapshotIndex(snapshot);
        var byId = rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        string NameOf(string id) => byId.TryGetValue(id, out var row) ? DisplayName(row) : NameOfId(id);
        return rows.ToDictionary(
            r => r.Id,
            r => index.Inhabitants.TryGetValue(r.Id, out var inhabitant) ? InhabitantActivity.Describe(inhabitant, index, key => Tr(key), NameOf) : Tr(InhabitantActivity.StandingKey),
            StringComparer.Ordinal);
    }

    /// <summary>The one name function of the screen (W1698): the inhabitant's own name, "Inhabitant N" only while it has none.</summary>
    private string DisplayName(InhabitantRow row) => ScreenModel.DisplayName(row, Tr("ui.inhabitant.unnamed"));

    /// <summary>The name as the inhabitant list shows it: a favourite says so (B6 R19).</summary>
    private string ListName(InhabitantRow row) =>
        row.Favourite ? TextFill.Fill(Tr("ui.inhabitants.favourite_name"), new Dictionary<string, string> { ["name"] = DisplayName(row) }) : DisplayName(row);

    /// <summary>The shown name of an inhabitant known only by id (chronicle, hints, help calls, mail).</summary>
    private string NameOfId(string inhabitantId) =>
        DisplayName(new InhabitantRow(inhabitantId, WorldFeed.NumberOf(inhabitantId), 0, false, null, 0, Name: _feed?.OwnName(inhabitantId)));

    /// <summary>Whole numbers as shown (no digit grouping; identical in DE and EN, independent of the OS).</summary>
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
