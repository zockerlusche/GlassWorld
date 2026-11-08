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
    private readonly GwTable _questions = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly VBoxContainer _chronicleList = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private ChronicleBook? _chronicle;
    private Contracts.ChronicleKinds? _kinds;
    private int _chronicleShown = -1;
    private int _chronicleCount;
    private readonly List<(Button Button, TimeChoice Choice)> _timeButtons = [];
    private readonly Dictionary<string, ProgressBar> _cardBars = new(StringComparer.Ordinal);
    private Label? _cardAction;
    private Label? _cardQuestions;
    private LineEdit? _talkInput;
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
        _questions.CustomMinimumSize = new Vector2(0, style.Tokens.Window.TableMinHeight);
        _inhabitants.Setup(
            [new GwColumn("ui.inhabitants.column.name", false), new GwColumn("ui.inhabitants.column.will_to_live", true), new GwColumn("ui.inhabitants.column.questions", true)],
            "ui.inhabitants.empty");
        _inhabitants.RowSelected += OpenCard;
        _questions.Setup([new GwColumn("ui.inhabitants.column.name", false), new GwColumn("ui.questions.column.count", true)], "ui.questions.empty");
        _questions.RowSelected += OpenCard;
        _sideBar.Add("inhabitants", "ui.inhabitants.title").Content.AddChild(_inhabitants);
        _sideBar.Add("questions", "ui.questions.title").Content.AddChild(_questions);
        _sideBar.Add("chronicle", "ui.chronicle.title").Content.AddChild(_chronicleList);
        _sideBar.Add("help", "ui.help.title").Content.AddChild(_helpList);
        _sideBar.Add("mailbox", "ui.mailbox.title").Content.AddChild(_mailList);

        // Favour in the top bar (B6 R9): a number, the tooltip says what it is for.
        // The value is filled in code (no translation), the tooltip is a key: translate it (review #76).
        _favor = new Label { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, TooltipText = "ui.favor.tooltip", TooltipAutoTranslateMode = Node.AutoTranslateModeEnum.Always, MouseFilter = Control.MouseFilterEnum.Stop };
        _topBar.GetChild(0).AddChild(_favor);
        _root.AddChild(_sideBar);
        foreach (var panel in _sideBar.Panels)
        {
            _root.AddChild(panel);
        }

        _toasts.Board = _board;
        _root.AddChild(_toasts);
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
            foreach (var hint in ScreenModel.Hints(events, _feed.Inhabitants()))
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

        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } place && _placing is not null)
        {
            if (WorldPoint(place.Position) is { } point)
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

            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("ui_cancel"))
        {
            // Open windows close themselves on Esc first (GwWindow); only then the pause menu opens.
            PauseRequested?.Invoke();
            GetViewport().SetInputAsHandled();
            return;
        }

        // The screen owns the left click (gift placement above, otherwise figure selection), so one click
        // never does both; on the island it also tells the camera (follow the figure, or stop following).
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click && _style is not null)
        {
            var id = PickAt(click.Position);
            if (id is not null)
            {
                Encounter(id);
                _presenter?.ChooseFigure(id);
                GetViewport().SetInputAsHandled();
            }
            else
            {
                _presenter?.ClickedGround();
            }
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
            var blocker = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
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
    public string ChronicleText(ChronicleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var date = TextFill.Fill(Tr("ui.chronicle.day"), new Dictionary<string, string>
        {
            ["season"] = Tr(entry.SeasonKey),
            ["day"] = Number(entry.Day),
            ["year"] = Number(entry.Year),
        });

        // Names are resolved now (GW1 review #64 C2); parameters that are keys are translated now.
        var (key, raw) = ChronicleBook.Line(entry, ChronicleNamesNow());
        var args = raw.ToDictionary(a => a.Key, a => TextKeys.LooksLikeKey(a.Value) ? Tr(a.Value).ToString() : a.Value, StringComparer.Ordinal);
        return date + "\n" + TextFill.Fill(Tr(key), args);
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
        id => DisplayName(new InhabitantRow(id, WorldFeed.NumberOf(id), 0, false, null, 0)),
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

        // New entries are added on top (GW1 review #64 C3); everything is rebuilt only after a language
        // switch (_chronicleShown = -1). Paging comes with the core chronicle API of D5.
        var rebuild = _chronicleShown < 0;
        _chronicleShown = _chronicle.Version;
        if (rebuild || _chronicleCount == 0)
        {
            foreach (var child in _chronicleList.GetChildren())
            {
                _chronicleList.RemoveChild(child);
                child.QueueFree();
            }

            _chronicleCount = 0;
        }

        if (_chronicle.Entries.Count == 0)
        {
            _chronicleList.AddChild(new Label { Text = "ui.chronicle.empty", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            return;
        }

        var fresh = _chronicle.Entries.Count - _chronicleCount;
        for (var i = fresh - 1; i >= 0; i--)
        {
            var label = new Label
            {
                Text = ChronicleText(_chronicle.Entries[i]),
                AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            _chronicleList.AddChild(label);
            _chronicleList.MoveChild(label, 0);
        }

        _chronicleCount = _chronicle.Entries.Count;
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
                Text = TextFill.Fill(Tr("ui.help.call"), new Dictionary<string, string> { ["number"] = Number(call.Number), ["need"] = Tr(VocabKeys.Need(call.Need)) }),
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
        ["need"] = message.Detail is null ? string.Empty : Tr(VocabKeys.Need(message.Detail)),
    });

    /// <summary>Shows a calm hint (e.g. saving failed).</summary>
    public void ShowHint(ToastMessage message) => _board?.Push(message, fastForward: false);

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
        _inhabitants.SetRows([.. rows.Select(r => new GwRow(r.Id, [DisplayName(r), Number(r.WillToLivePercent), Number(r.OpenQuestions)], [r.Number, r.WillToLivePercent, r.OpenQuestions]))]);
        _questions.SetRows([.. _feed.OpenQuestions().Select(q => (Row: rows.First(r => r.Id == q.InhabitantId), q.Count)).Select(x => new GwRow(x.Row.Id, [DisplayName(x.Row), Number(x.Count)], [x.Row.Number, x.Count]))]);

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
        _card.Position += new Vector2(0, top);
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
            Section("ui.card.wellbeing");
            Bar("ui.card.will_to_live");
            foreach (var need in card.Needs)
            {
                Bar(need.Key);
            }

            Section("ui.card.feelings");
            foreach (var feeling in card.Feelings)
            {
                Bar(feeling.Key);
            }

            Section("ui.card.character");
            foreach (var trait in card.Traits)
            {
                Bar(trait.Key);
            }

            Section("ui.card.doing");
            _cardAction = new Label();
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
        }

        foreach (var (key, percent) in values)
        {
            if (_cardBars.TryGetValue(key, out var bar) && (int)bar.Value != percent)
            {
                bar.Value = percent;
            }
        }

        var action = card.Row.ActionKey ?? "ui.card.idle";
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

        void Bar(string key)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = key, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            var bar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = true, CustomMinimumSize = new Vector2(_style!.Tokens.Window.CardBarWidth, 0) };
            row.AddChild(bar);
            _card!.Content.AddChild(row);
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

        _card?.ScrollToEnd();
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
            mark = new Label3D
            {
                Name = MarkNode,
                Position = new Vector3(0, anchor.HeightM, 0),
                PixelSize = anchor.PixelSizeM ?? 0.005f,
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

    private string DisplayName(InhabitantRow row) => TextFill.Fill(Tr("ui.inhabitant.unnamed"), ScreenModel.NameArgs(row));

    /// <summary>Whole numbers as shown (no digit grouping; identical in DE and EN, independent of the OS).</summary>
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
