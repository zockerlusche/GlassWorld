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
    private readonly GwTable _inhabitants = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _questionList = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _logList = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly HashSet<string> _hintedQuestions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _answerDrafts = new(StringComparer.Ordinal);
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
    private Dictionary<string, string>? _activities;
    private Label? _cardQuestions;
    private LineEdit? _talkInput;
    private readonly Dictionary<string, bool> _cardFolded = new(StringComparer.Ordinal);
    private string _cardShape = string.Empty;
    private readonly GwToasts _toasts = new();
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
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        // Windows over the game (pause, settings) stay visible when the UI is hidden ("watch only").
        AddChild(_overlay);
        _overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
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

        _toasts.Board = _board;
        _root.AddChild(_toasts);

        // Events never move the camera by themselves (TL #19976): a new thought is a hint, and only its
        // "look" button makes the camera follow that inhabitant (until the player moves it or presses Esc).
        presenter.Noticed += NoticeThought;
        _toasts.LookRequested += id => _presenter?.ChooseFigure(id);
        GetViewport().SizeChanged += Layout;

        // A new theme (text size, UI size) changes the top bar's height after it has wrapped again;
        // lay out once more two frames later, so the card stays below the bar (CI found it).
        _root.ThemeChanged += () => _relayoutFrames = 2;
        _sideBar.Toggled += Layout;
        Layout();
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
        // still a drag; nothing is consumed here.
        if (_click is not { Pressed: true } click)
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
            var blocker = new ColorRect { MouseFilter = Control.MouseFilterEnum.Stop, Color = new Color(0, 0, 0, (float)(_style?.Tokens.Window.ModalDim ?? 0)) };
            blocker.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _overlay.AddChild(blocker);
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

        var days = _chronicle.Days();
        if (days.Count == 0)
        {
            _chronicleList.AddChild(new Label { Text = "ui.chronicle.empty", AutowrapMode = TextServer.AutowrapMode.WordSmart });
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
        _chronicleList.AddChild(timeline);

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
            var who = entry.InhabitantId;
            line.Pressed += () =>
            {
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
        ["need"] = message.Detail is null ? string.Empty : Tr(VocabKeys.Need(message.Detail)),
    });

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

        var questions = _feed.Questions().Take(_style.Tokens.Window.QuestionsShown).ToList();
        var shape = TranslationServer.GetLocale() + ";" + string.Join(";", questions.Select(q => q.Id));
        if (shape == _questionShape)
        {
            return;
        }

        _questionShape = shape;
        foreach (var child in _questionList.GetChildren())
        {
            _questionList.RemoveChild(child);
            child.QueueFree();
        }

        foreach (var gone in _answerDrafts.Keys.Where(id => questions.All(q => q.Id != id)).ToList())
        {
            _answerDrafts.Remove(gone);
        }

        if (questions.Count == 0)
        {
            _questionList.AddChild(new Label { Text = "ui.questions.empty_text", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            return;
        }

        foreach (var question in questions)
        {
            if (rows.FirstOrDefault(r => r.Id == question.InhabitantId) is { } row)
            {
                AddQuestion(question, row);
            }
        }

        _questionList.AddChild(new Label { Text = "ui.question.later", AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }

    private void AddQuestion(QuestionRow question, InhabitantRow row)
    {
        var args = new Dictionary<string, string>
        {
            ["name"] = DisplayName(row),
            ["thing"] = question.TypeId is { } type ? Tr(VocabKeys.ObjectType(type)) : string.Empty,
        };

        // A kind without its own text gets the general one.
        string Text(string key, string other)
        {
            var text = Tr(key).ToString();
            return TextFill.Fill(text == key ? Tr(other) : text, args);
        }

        var id = row.Id;
        var who = new Button { Text = DisplayName(row), Flat = true, Alignment = HorizontalAlignment.Left, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        who.Pressed += () => ShowInhabitant(id);
        _questionList.AddChild(who);
        _questionList.AddChild(new Label { Text = Text(QuestionKeys.Ask(question.Kind), QuestionKeys.AskOther), ThemeTypeVariation = UiTheme.SubheaderLabel, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        _questionList.AddChild(new Label { Text = Text(QuestionKeys.Reason(question.Kind), QuestionKeys.ReasonOther), AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart });

        var qid = question.Id;
        var input = new LineEdit
        {
            PlaceholderText = Tr("ui.question.answer_hint"),
            MaxLength = 280,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            Text = _answerDrafts.GetValueOrDefault(qid, string.Empty),
        };
        input.TextChanged += t => _answerDrafts[qid] = t;
        var send = new Button { Text = "ui.question.answer" };
        void Answer()
        {
            if (Say(id, input.Text))
            {
                input.Clear();
                _answerDrafts.Remove(qid);
            }
        }

        input.TextSubmitted += _ => Answer();
        send.Pressed += Answer;
        var answerRow = new HBoxContainer();
        answerRow.AddChild(input);
        answerRow.AddChild(send);
        _questionList.AddChild(answerRow);
        _questionList.AddChild(new HSeparator());
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
        _inhabitants.SetRows([.. rows.Select(r => new GwRow(r.Id, [DisplayName(r), _activities[r.Id], Number(r.WillToLivePercent), Number(r.OpenQuestions)], [r.Number, 0, r.WillToLivePercent, r.OpenQuestions]))]);
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
        var open = _feed.Inhabitants().First(r => r.Id == id).OpenQuestions;
        if (open > 0 && (mark is null || !mark.Visible))
        {
            return "question mark missing";
        }

        // W1696: an open question shows its wording and an answer field in the questions panel.
        Refresh();
        if (_feed.Questions().Count > 0 && !_questionList.GetChildren().OfType<HBoxContainer>().Any(r => r.GetChildren().OfType<LineEdit>().Any()))
        {
            return "questions panel shows no question with an answer field";
        }

        var chronicle = CheckChronicle();
        if (chronicle.Length > 0)
        {
            return chronicle;
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
    /// The card stays inside the safe area and beside the open side panel at the smallest and largest UI
    /// size (found in the 3840x1600 screenshots: the card kept its old place after a size change).
    /// </summary>
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
        _sideBar.Position = new Vector2(area.X - margin - _sideBar.GetCombinedMinimumSize().X, margin);
        _topBar.Position = new Vector2(margin, margin);
        _topBar.Size = new Vector2(area.X - (2 * margin) - _sideBar.GetCombinedMinimumSize().X - gap, 0);
        foreach (var panel in _sideBar.Panels)
        {
            panel.Dock(area, _style, _sideBar.GetCombinedMinimumSize().X + gap);
        }

        var width = Math.Min(_style.Tokens.Window.SidePanelWidth, area.X - (2 * margin));
        _toasts.Size = new Vector2(width, 0);
        _toasts.Position = new Vector2(area.X - margin - width, area.Y - margin - _toasts.GetCombinedMinimumSize().Y);

        // The card stays beside the side bar and its open panel and is re-fitted when the UI size changes.
        FitCard(area);
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

    private void FillCard(string inhabitantId)
    {
        var card = _feed?.Card(inhabitantId);
        if (_card is null || card is null || _style is null)
        {
            _card?.Close();
            return;
        }

        _card.SetTitleText(DisplayName(card.Row));
        var values = new List<(string Key, int Percent)> { ("ui.card.will_to_live", card.Row.WillToLivePercent) };
        values.AddRange(card.Needs.Select(v => (v.Key, v.Percent)));
        values.AddRange(card.Feelings.Select(v => (v.Key, v.Percent)));
        values.AddRange(card.Traits.Select(v => (v.Key, v.Percent)));
        var shape = $"{inhabitantId}|{string.Join(',', card.Needs.Select(v => v.Key))}|{string.Join(',', card.Feelings.Select(v => v.Key))}|{string.Join(',', card.Traits.Select(v => v.Key))}";
        if (shape != _cardShape)
        {
            // Build only when the set of values changes (other inhabitant, a feeling appears/fades).
            _cardShape = shape;
            _cardBars.Clear();
            _card.ClearContent();

            // Order of the card (W1688, TL #19983): first what the inhabitant does and the talk field, well
            // visible; the values below in sections the player can fold (the choice is kept while playing).
            Section("ui.card.doing");
            _cardAction = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _cardQuestions = new Label { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
            _card.Content.AddChild(_cardAction);
            _card.Content.AddChild(_cardQuestions);

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
                if (Say(inhabitantId, input.Text))
                {
                    input.Clear();
                }
            }

            input.TextSubmitted += _ => Submit();
            send.Pressed += Submit;
            talkRow.AddChild(input);
            talkRow.AddChild(send);
            _card.Content.AddChild(talkRow);

            var wellbeing = Fold("ui.card.wellbeing", folded: false);
            Bar(wellbeing, "ui.card.will_to_live");
            foreach (var need in card.Needs)
            {
                Bar(wellbeing, need.Key);
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
        if (_cardAction is not null && _cardAction.Text != action)
        {
            _cardAction.Text = action;
        }

        var questions = TextFill.Fill(Tr("ui.card.questions"), new Dictionary<string, string> { ["count"] = Number(card.Row.OpenQuestions) });
        if (_cardQuestions is not null && _cardQuestions.Text != questions)
        {
            _cardQuestions.Text = questions;
        }

        void Section(string key) => _card!.Content.AddChild(new Label { Text = key, ThemeTypeVariation = UiTheme.SubheaderLabel });

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
            _card!.Content.AddChild(section);
            return rows;
        }

        void Bar(VBoxContainer into, string key)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = key, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            var bar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = true, CustomMinimumSize = new Vector2(_style!.Tokens.Window.CardBarWidth, 0) };
            row.AddChild(bar);
            into.AddChild(row);
            _cardBars[key] = bar;
        }
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
    public bool Say(string inhabitantId, string text)
    {
        if (_presenter?.Host is not { } host)
        {
            return false;
        }

        var result = host.Decisions.Submit(new PlayerCommand(Simulation.Player.ConversationSystem.SendText, inhabitantId) { Text = text });
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

        mark.Text = ScreenModel.QuestionMarks(row.OpenQuestions);
        mark.Visible = row.OpenQuestions > 0;
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

    /// <summary>The shown name of an inhabitant known only by id (chronicle, hints, help calls, mail).</summary>
    private string NameOfId(string inhabitantId) =>
        DisplayName(new InhabitantRow(inhabitantId, WorldFeed.NumberOf(inhabitantId), 0, false, null, 0, Name: _feed?.OwnName(inhabitantId)));

    /// <summary>Whole numbers as shown (no digit grouping; identical in DE and EN, independent of the OS).</summary>
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
