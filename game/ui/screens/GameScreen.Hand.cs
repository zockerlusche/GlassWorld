using GlassWorld.Presentation.Ui;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Player;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>
/// A1832 (B6-16, B6 R22): the player's hand. The thing's window offers to move it when the core would let the hand take it;
/// then a line near the pointer says what the core answers for the spot under it (here, or why not: too far, taken, nobody
/// gets there, too close to someone, not enough favour), and a click puts it there with the logged command move_object.
/// Esc lets go. The controls have no rule of their own - every answer is the core's (B2462).
/// </summary>
public partial class GameScreen
{
    private string? _moving; // the thing the hand holds, or null
    private Label? _handHover;
    private Vector2? _handPointer; // where the pointer is now (set on motion)
    private Vector2? _handShownAt; // where the line was last worked out

    /// <summary>Whether the core would let the hand take this thing at all (anything but 'not movable' means yes).</summary>
    private bool HandCanTake(string objectId) =>
        _presenter?.Host is { } host && host.Objects.TryGet(objectId, out var thing)
        && host.Player.CheckMove(MoveCommand(objectId, thing.Position.X, thing.Position.Z)).ReasonKey != PlayerSystem.ReasonNotMovable;

    private static PlayerCommand MoveCommand(string objectId, double x, double z) => new(PlayerSystem.MoveObject, null) { ObjectId = objectId, X = x, Z = z };

    /// <summary>The hand takes a thing: the window closes, the pointer now shows where it may go.</summary>
    private void TakeWithHand(string objectId)
    {
        _placing = null;
        _pointingFor = null;
        _moving = objectId;
        CloseThingForShots();
        ShowHint(new ToastMessage("msg.hint.move_object", new Dictionary<string, string>(), null));
    }

    /// <summary>The hand lets go without moving anything (Esc).</summary>
    private void LetGo()
    {
        _moving = null;
        _handShownAt = null;
        if (_handHover is not null)
        {
            _handHover.Visible = false;
        }
    }

    /// <summary>The core's answer for the spot under the pointer, in words: where it goes, or why not.</summary>
    private string? HandLine(Vector2 position)
    {
        if (_moving is not { } id || _presenter?.Host is not { } host || WorldPoint(position) is not { } spot)
        {
            return null;
        }

        var answer = host.Player.CheckMove(MoveCommand(id, spot.X, spot.Z));
        return answer.Accepted
            ? TextFill.Fill(Tr("ui.hand.here"), new Dictionary<string, string> { ["cost"] = Number((long)Math.Ceiling(host.Player.CostOf(PlayerSystem.MoveIntervention))) })
            : Tr(answer.ReasonKey ?? "msg.command.unknown");
    }

    /// <summary>
    /// While the hand holds a thing, the line near the pointer follows it: worked out once per frame when the pointer has
    /// moved (review GW1 #593: never the line of an earlier spot, also when the pointer rests after a quick move).
    /// </summary>
    private void HoverHand()
    {
        if (_moving is null || _handPointer is not { } position || position == _handShownAt)
        {
            return;
        }

        _handShownAt = position;
        if (_handHover is null)
        {
            _handHover = new Label { Name = "HandHover", AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, ThemeTypeVariation = UiTheme.SmallLabel, MouseFilter = Control.MouseFilterEnum.Ignore };
            AddChild(_handHover);
        }

        var line = HandLine(position);
        _handHover.Visible = line is not null;
        if (line is not null)
        {
            _handHover.Text = line;
            _handHover.Position = position + new Vector2(16, 16);
        }
    }

    /// <summary>A click while the hand holds a thing: the core moves it there, or says calmly why not (the hand keeps it).</summary>
    private void PutDown(Vector2 position)
    {
        if (_moving is not { } id || _presenter?.Host is not { } host || WorldPoint(position) is not { } spot)
        {
            return;
        }

        var result = host.Decisions.Submit(MoveCommand(id, spot.X, spot.Z));
        ShowHint(new ToastMessage(result.Accepted ? "msg.hint.object_moved" : result.ReasonKey ?? "msg.command.unknown", new Dictionary<string, string>(), null));
        if (result.Accepted)
        {
            LetGo();
        }
    }

    /// <summary>
    /// Smoke run (A1832): a thing the hand may take has the button, taking it shows the core's answer for a spot far away
    /// (too far) and puts it down nearby; a thing it may not take has no button. Returns empty when fine.
    /// </summary>
    public string CheckHand()
    {
        if (_presenter?.Host is not { } host || _style is null || host.Content.GiftRules.Hand is not { } hand)
        {
            return "hand not set up";
        }

        var movable = host.Objects.All.Where(o => HandCanTake(o.Id)).OrderBy(o => o.Id, StringComparer.Ordinal).FirstOrDefault();
        if (movable is null)
        {
            return "no thing the hand may take";
        }

        if (OpenThing(movable.Id) is not { } window || window.FindChild(HandButtonName, true, false) is not Button take)
        {
            return "the thing's window has no move button";
        }

        take.EmitSignal(BaseButton.SignalName.Pressed);
        if (_moving != movable.Id)
        {
            return "the button did not hand the thing over";
        }

        // Through the controls (review GW1 #593): the line for a spot beside the thing, then a click there.
        if (GetViewport().GetCamera3D() is not { } camera)
        {
            LetGo();
            return "no camera for the hand's line";
        }

        var far = camera.UnprojectPosition(new Vector3((float)(movable.Position.X + (hand.MaxMoveM * 2)), 0, (float)movable.Position.Z));
        // Off the picture there is no ground under the pointer and no line; on it, a refusal in words (not a key, not 'here').
        var farLine = HandLine(far);
        var here = TextFill.Fill(Tr("ui.hand.here"), new Dictionary<string, string> { ["cost"] = Number((long)Math.Ceiling(host.Player.CostOf(PlayerSystem.MoveIntervention))) });
        if (farLine is not null && (farLine == here || farLine.StartsWith("msg.", StringComparison.Ordinal)))
        {
            LetGo();
            return $"the line for a spot beyond the reach was '{farLine}'";
        }

        var near = camera.UnprojectPosition(new Vector3((float)movable.Position.X + 2, 0, (float)movable.Position.Z + 2));
        var nearLine = HandLine(near);
        var favour = host.Player.Favor;
        PutDown(near);
        var answered = _moving is null ? "taken" : "kept";
        LetGo();
        if (nearLine is null || nearLine.StartsWith("msg.", StringComparison.Ordinal) || nearLine.StartsWith("ui.", StringComparison.Ordinal))
        {
            return $"the line for a spot beside the thing was '{nearLine}'";
        }

        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"HAND_LINES far='{farLine}' near='{nearLine}' put_down={answered} favour_before={favour:0.#}"));

        var fixedThing = host.Objects.All.FirstOrDefault(o => !HandCanTake(o.Id));
        if (fixedThing is null)
        {
            return "no thing the hand may not take (the check would pass for nothing)";
        }

        if (OpenThing(fixedThing.Id) is { } other && other.FindChild(HandButtonName, true, false) is not null)
        {
            return $"{fixedThing.TypeId} cannot be moved but its window offers it";
        }

        CloseThingForShots();
        return string.Empty;
    }

    private const string HandButtonName = "MoveWithHand";
}
