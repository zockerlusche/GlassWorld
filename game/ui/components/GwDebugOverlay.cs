using GlassWorld.Game.World;
using GlassWorld.Presentation.Ui;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Look;
using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// The debug view (B2-6 R31, until B6): a small panel at the bottom left with seed, world hash, state
/// hash, world time, season, weather, look epoch, frames per second, number of figures and the biome
/// under the mouse pointer. Off by default, shown and hidden with the action <c>gw_debug_overlay</c>
/// (F12; F1 to F3 are kept free for the theme buttons of the UI concept D642). It never takes the mouse, so the wheel still zooms over it. The lines come from
/// <see cref="DebugOverlayModel"/>; the panel and the label use the UI theme (KR6).
/// With debug commands on (<c>--debug</c>) and the view open, two more actions step the look epoch back
/// and forward through the data with the core command <c>debug_set_look_epoch</c>.
/// </summary>
public partial class GwDebugOverlay : PanelContainer
{
    /// <summary>Seconds between two refreshes of the lines (real time; a debug tool, no game value).</summary>
    private const double LinesSeconds = 0.25;

    /// <summary>The state hash is computed at most this often, in seconds: it hashes the whole world.</summary>
    private const double HashSeconds = 1.0;

    private readonly Label _label = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, MouseFilter = MouseFilterEnum.Ignore, ThemeTypeVariation = UiTheme.SmallLabel };
    private WorldPresenter? _presenter;
    private int _margin;
    private string _stateHash = string.Empty;
    private long _hashTick = -1;
    private double _sinceLines = double.MaxValue;
    private double _sinceHash = double.MaxValue;

    /// <summary>The lines shown at the last refresh.</summary>
    public IReadOnlyList<string> Lines { get; private set; } = [];

    /// <summary>Binds the view to the running world; it stays hidden until toggled.</summary>
    public void Setup(WorldPresenter presenter, UiStyle style)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        ArgumentNullException.ThrowIfNull(style);
        _margin = style.Tokens.Window.SafeMargin;
        ThemeTypeVariation = UiTheme.WindowPanel;
        MouseFilter = MouseFilterEnum.Ignore;
        GrowVertical = GrowDirection.Begin;
        Visible = false;
        AddChild(_label);
        UiSetup.Bind(this);
        EnsureDefaultInput();
    }

    /// <summary>Shows or hides the view.</summary>
    public void Toggle()
    {
        Visible = !Visible;
        if (Visible)
        {
            // Time does not run for the hidden view: a fresh state hash on opening.
            _sinceHash = double.MaxValue;
            Refresh();
        }
    }

    /// <summary>Reads the world and writes the lines (the state hash only when it is due).</summary>
    public void Refresh()
    {
        if (_presenter?.Host is not { } host)
        {
            return;
        }

        if (_stateHash.Length == 0 || (_sinceHash >= HashSeconds && host.World.Tick != _hashTick))
        {
            _stateHash = host.World.StateHash();
            _hashTick = host.World.Tick;
            _sinceHash = 0;
        }

        var viewport = GetViewport();
        (double X, double Z)? pointer = viewport?.GetCamera3D() is { } camera && _presenter.GroundPoint(camera, viewport.GetMousePosition()) is { } point
            ? (point.X, point.Z)
            : null;
        var data = DebugOverlayModel.Read(host, _stateHash, _hashTick, Engine.GetFramesPerSecond(), pointer, (KeyName(DebugOverlayModel.EpochBackAction), KeyName(DebugOverlayModel.EpochForwardAction)));
        Lines = DebugOverlayModel.Lines(data, key => Tr(key));
        _label.Text = string.Join('\n', Lines);
        _sinceLines = 0;

        // Bottom left above the safe margin, as large as the text: free in both screen layouts.
        SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft, LayoutPresetMode.Minsize, _margin);
    }

    /// <summary>
    /// Debug jump: one look epoch forward (+1) or back (-1) in the order of the data. The core accepts the
    /// command only with debug commands on and applies it at the next tick (not while paused).
    /// </summary>
    /// <returns>Whether the core accepted a step.</returns>
    public bool StepEpoch(int step)
    {
        if (_presenter?.Host is not { } host)
        {
            return false;
        }

        var next = DebugOverlayModel.StepEpoch([.. host.Content.LookEpochs.Epochs.Select(e => e.Id)], host.LookEpoch.Current, step);
        return next is not null && host.Decisions.Submit(new PlayerCommand(LookEpochSystem.DebugSetLookEpoch, null) { TypeId = next }).Accepted;
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (!Visible)
        {
            return;
        }

        _sinceLines += delta;
        _sinceHash += delta;
        if (_sinceLines >= LinesSeconds)
        {
            Refresh();
        }
    }

    /// <inheritdoc/>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (_presenter?.Host is not { } host)
        {
            return;
        }

        if (@event.IsActionPressed(DebugOverlayModel.ToggleAction))
        {
            Toggle();
            GetViewport().SetInputAsHandled();
            return;
        }

        // The jumps need the open view (no step by accident) and leave the keys to a focused list or text field.
        if (!Visible || !host.Options.DebugCommands || CameraRig.BlocksCameraKeys(GetViewport().GuiGetFocusOwner()))
        {
            return;
        }

        var step = @event.IsActionPressed(DebugOverlayModel.EpochForwardAction) ? 1 : @event.IsActionPressed(DebugOverlayModel.EpochBackAction) ? -1 : 0;
        if (step != 0)
        {
            StepEpoch(step);
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Name of the first key of an action, as printed on the key.</summary>
    private static string KeyName(string action) =>
        InputMap.ActionGetEvents(action).OfType<InputEventKey>().FirstOrDefault()?.AsTextPhysicalKeycode() ?? DebugOverlayModel.None;

    private static void EnsureDefaultInput()
    {
        static void Add(string action, Key key)
        {
            if (!InputMap.HasAction(action))
            {
                InputMap.AddAction(action);
                InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
            }
        }

        Add(DebugOverlayModel.ToggleAction, Key.F12);
        Add(DebugOverlayModel.EpochBackAction, Key.Pagedown);
        Add(DebugOverlayModel.EpochForwardAction, Key.Pageup);
    }
}
