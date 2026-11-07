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
    private readonly GwSideBar _sideBar = new();
    private readonly GwTable _inhabitants = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 300) };
    private readonly GwTable _questions = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 200) };
    private readonly GwToasts _toasts = new();
    private PanelContainer? _topBar;
    private Label? _date;
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
        (_topBar, _date, _time) = CreateTopBar(style, ScreenModel.TimeChoices(presenter.Host.World.Calendar.Settings), speed => presenter.Speed = speed);
        _root.AddChild(_topBar);

        _inhabitants.Setup(
            [new GwColumn("ui.inhabitants.column.name", false), new GwColumn("ui.inhabitants.column.will_to_live", true), new GwColumn("ui.inhabitants.column.questions", true)],
            "ui.inhabitants.empty");
        _inhabitants.RowSelected += OpenCard;
        _questions.Setup([new GwColumn("ui.inhabitants.column.name", false), new GwColumn("ui.questions.column.count", true)], "ui.questions.empty");
        _questions.RowSelected += OpenCard;
        _sideBar.Add("inhabitants", "ui.inhabitants.title").Content.AddChild(_inhabitants);
        _sideBar.Add("questions", "ui.questions.title").Content.AddChild(_questions);
        _root.AddChild(_sideBar);
        foreach (var panel in _sideBar.Panels)
        {
            _root.AddChild(panel);
        }

        _toasts.Board = _board;
        _root.AddChild(_toasts);
        GetViewport().SizeChanged += Layout;
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
        if (_feed is null || _board is null || _style is null || _presenter is null)
        {
            return;
        }

        var events = _feed.NewEvents();
        if (events.Count > 0)
        {
            foreach (var hint in ScreenModel.Hints(events, _feed.Inhabitants()))
            {
                _board.Push(hint, fastForward: _presenter.Speed > 1);
            }
        }

        _sinceRefreshMs += delta * 1000;
        if (_sinceRefreshMs >= _style.Tokens.Window.ListRefreshMs)
        {
            _sinceRefreshMs = 0;
            Refresh();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click && _style is not null)
        {
            var id = PickAt(click.Position);
            if (id is not null)
            {
                Encounter(id);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    /// <summary>The player clicks an inhabitant in the world: a perception for the core (R7) and the card.</summary>
    public void Encounter(string inhabitantId)
    {
        _presenter?.Host?.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, inhabitantId));
        OpenCard(inhabitantId);
    }

    /// <summary>Rebuilds lists, clock, card and question marks from the current snapshot.</summary>
    public void Refresh()
    {
        if (_feed is null || _date is null || _time is null)
        {
            return;
        }

        var rows = _feed.Inhabitants();
        _inhabitants.SetRows([.. rows.Select(r => new GwRow(r.Id, [DisplayName(r), Number(r.WillToLivePercent), Number(r.OpenQuestions)], [r.Number, r.WillToLivePercent, r.OpenQuestions]))]);
        _questions.SetRows([.. _feed.OpenQuestions().Select(q => (Row: rows.First(r => r.Id == q.InhabitantId), q.Count)).Select(x => new GwRow(x.Row.Id, [DisplayName(x.Row), Number(x.Count)], [x.Row.Number, x.Count]))]);

        var clock = _feed.Clock();
        _date.Text = TextFill.Fill(Tr("ui.clock.date"), new Dictionary<string, string>
        {
            ["season"] = Tr(clock.SeasonKey),
            ["day"] = Number(clock.Day),
            ["year"] = clock.Year.ToString(CultureInfo.CurrentCulture),
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
    /// becomes a question the screen shows.
    /// </summary>
    /// <returns>Empty if fine, otherwise the problem.</returns>
    public string SelfCheck()
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
        _presenter.Host.World.Step();
        Refresh();
        if (_cardId != id || _card is null || !_card.IsInsideTree())
        {
            return "card did not open";
        }

        var mark = _presenter.GetNodeOrNull<Node3D>(id)?.GetNodeOrNull<Label3D>(MarkNode);
        var open = _feed.Inhabitants().First(r => r.Id == id).OpenQuestions;
        return open > 0 && (mark is null || !mark.Visible) ? "question mark missing" : string.Empty;
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
        _root.AddChild(_card);
        FillCard(inhabitantId);
        _card.FitInto(GetViewport().GetVisibleRect().Size);
    }

    private void FillCard(string inhabitantId)
    {
        var card = _feed?.Card(inhabitantId);
        if (_card is null || card is null)
        {
            _card?.Close();
            return;
        }

        _card.SetTitleText(DisplayName(card.Row));
        _card.ClearContent();
        Section("ui.card.wellbeing");
        Bar("ui.card.will_to_live", card.Row.WillToLivePercent);
        foreach (var need in card.Needs)
        {
            Bar(need.Key, need.Percent);
        }

        Section("ui.card.feelings");
        foreach (var feeling in card.Feelings)
        {
            Bar(feeling.Key, feeling.Percent);
        }

        Section("ui.card.character");
        foreach (var trait in card.Traits)
        {
            Bar(trait.Key, trait.Percent);
        }

        Section("ui.card.doing");
        _card.Content.AddChild(new Label { Text = card.Row.ActionKey ?? "ui.card.idle" });
        _card.Content.AddChild(new Label
        {
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            Text = TextFill.Fill(Tr("ui.card.questions"), new Dictionary<string, string> { ["count"] = Number(card.Row.OpenQuestions) }),
        });

        void Section(string key) => _card!.Content.AddChild(new Label { Text = key, ThemeTypeVariation = UiTheme.SubheaderLabel });

        void Bar(string key, int percent)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = key, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            row.AddChild(new ProgressBar { MinValue = 0, MaxValue = 100, Value = percent, ShowPercentage = true, CustomMinimumSize = new Vector2(_style!.Tokens.Window.SidePanelWidth / 3f, 0) });
            _card!.Content.AddChild(row);
        }
    }

    private void UpdateMark(InhabitantRow row)
    {
        var figure = _presenter?.GetNodeOrNull<Node3D>(row.Id);
        if (figure is null || _style is null)
        {
            return;
        }

        var mark = figure.GetNodeOrNull<Label3D>(MarkNode);
        if (mark is null)
        {
            mark = new Label3D
            {
                Name = MarkNode,
                Position = new Vector3(0, 2.3f, 0),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FontSize = _style.FontPx(UiFontSizeId.Display) * 2,
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

        var figures = _presenter.Host.Inhabitants.All
            .Select(i => (i.Id, Node: _presenter.GetNodeOrNull<Node3D>(i.Id)))
            .Where(f => f.Node is not null && !camera.IsPositionBehind(f.Node!.GlobalPosition))
            .Select(f =>
            {
                var p = camera.UnprojectPosition(f.Node!.GlobalPosition);
                return (f.Id, (double)p.X, (double)p.Y);
            });
        return ScreenModel.Pick(figures, screen.X, screen.Y, _style.Tokens.Window.PickRadiusPx);
    }

    private string DisplayName(InhabitantRow row) => TextFill.Fill(Tr("ui.inhabitant.unnamed"), ScreenModel.NameArgs(row));

    private static string Number(long value) => value.ToString(CultureInfo.CurrentCulture);
}
