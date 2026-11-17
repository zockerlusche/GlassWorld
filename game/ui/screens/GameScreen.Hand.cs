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
    private ulong _handHoverAt;

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
            ? TextFill.Fill(Tr("ui.hand.here"), new Dictionary<string, string> { ["cost"] = Number((long)Math.Ceiling(host.Player.CostOf("move"))) })
            : Tr(answer.ReasonKey ?? "msg.command.unknown");
    }

    /// <summary>While the hand holds a thing, the line near the pointer follows it (a few times a second).</summary>
    private void HoverHand(Vector2 position)
    {
        if (_moving is null)
        {
            return;
        }

        var now = Time.GetTicksMsec();
        if (now - _handHoverAt < 100)
        {
            return;
        }

        _handHoverAt = now;
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

        var far = host.Player.CheckMove(MoveCommand(movable.Id, movable.Position.X + (hand.MaxMoveM * 2), movable.Position.Z));
        LetGo();
        if (far.ReasonKey != PlayerSystem.ReasonTooFar || Tr(far.ReasonKey) == far.ReasonKey)
        {
            return $"a spot beyond the reach gave '{far.ReasonKey}' (expected a translated too_far)";
        }

        var fixedThing = host.Objects.All.FirstOrDefault(o => !HandCanTake(o.Id));
        if (fixedThing is not null && OpenThing(fixedThing.Id) is { } other && other.FindChild(HandButtonName, true, false) is not null)
        {
            return $"{fixedThing.TypeId} cannot be moved but its window offers it";
        }

        CloseThingForShots();
        return string.Empty;
    }

    private const string HandButtonName = "MoveWithHand";
}
