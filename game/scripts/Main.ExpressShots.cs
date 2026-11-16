using GlassWorld.Game.Localization;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Check runs only (A1542, A1732): pictures of a figure showing an expression of look/expressions.json - a knock at the
/// glass, a cheer - so a person can see the gesture. The camera follows one awake inhabitant near; the event is handed to
/// its figure directly (presentation only, the core is not touched), and pictures are taken at fixed real seconds while
/// the expression runs. Each event is "kind" or "kind:detail", e.g. mail_arrived:knock.
/// </summary>
public partial class Main
{
    private const string ExpressShotsArgument = "--express-shots=";
    private const string ExpressShotsEventsArgument = "--express-shots-events=";

    /// <summary>Camera distance for the pictures: near enough to see hands and face.</summary>
    private const double ExpressShotDistanceM = 3.5;

    /// <summary>Real seconds after the event at which a picture is taken: turning, rising, tapping, lowering.</summary>
    private static readonly double[] ExpressShotTimes = [0.3, 0.8, 1.2, 1.5, 1.9, 2.6];

    private bool _expressShots;
    private string[] _expressShotsEvents = ["mail_arrived:knock", "invented"];

    /// <summary>Reads the expression-picture arguments; true when the argument was one of them.</summary>
    private bool ReadExpressShotsArgument(string arg)
    {
        if (arg.StartsWith(ExpressShotsArgument, StringComparison.Ordinal))
        {
            _shotsFolder = arg[ExpressShotsArgument.Length..];
            _expressShots = true;
            return true;
        }

        if (arg.StartsWith(ExpressShotsEventsArgument, StringComparison.Ordinal))
        {
            _expressShotsEvents = arg[ExpressShotsEventsArgument.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return true;
        }

        return false;
    }

    private async Task ExpressShotsAsync()
    {
        var ok = false;
        try
        {
            TextCatalogs.SetLanguage("de");
            DirAccess.MakeDirRecursiveAbsolute(_shotsFolder!);
            var size = GetWindow().Size;
            await Frames(60);
            _world!.Speed = 1;
            var shots = 0;
            foreach (var (number, spec) in _expressShotsEvents.Select((s, i) => (i + 1, s)))
            {
                var parts = spec.Split(':', 2);
                var snapshot = _world.Host!.Snapshot();
                // Each event on another awake inhabitant: a moment's calm pause would hide the next one on the same figure.
                var awake = snapshot.Inhabitants.Where(i => !_world.LiesNow(i.Id)).ToList();
                var who = awake.Count == 0 ? null : awake[(number - 1) % awake.Count];
                if (spec == "day")
                {
                    // Check runs (A1383/A1796): a new test world starts before dawn - run fast to the late morning, then go on.
                    var fastest = ScreenModel.TimeChoices(_world.Host.World.Calendar.Settings).Max(c => c.Speed);
                    var began = Time.GetTicksMsec();
                    while (_world.Host.World.Calendar.At(_world.Host.World.Tick).Hour is < 10 or > 15 && (Time.GetTicksMsec() - began) / 1000.0 < 600)
                    {
                        _world.Speed = fastest;
                        await Frames(5);
                    }

                    _world.Speed = 1;
                    GD.Print($"EXPRESS_SHOT_DAY hour={_world.Host.World.Calendar.At(_world.Host.World.Tick).Hour}");
                    continue;
                }

                if (spec is "wet" or "up")
                {
                    // A1385: wait (fast) until someone awake is soaked (rain, wading) or up in a tree, then follow that one.
                    var quick = ScreenModel.TimeChoices(_world.Host.World.Calendar.Settings).Max(c => c.Speed);
                    var since = Time.GetTicksMsec();
                    who = null;
                    while (who is null && (Time.GetTicksMsec() - since) / 1000.0 < 600)
                    {
                        _world.Speed = quick;
                        await Frames(5);
                        who = _world.Host.Snapshot().Inhabitants.FirstOrDefault(i => !_world.LiesNow(i.Id)
                            && (spec == "wet" ? i.WetPermille >= 600 : i.ClimbedCm > 0));
                    }

                    _world.Speed = 1;
                    GD.Print($"EXPRESS_SHOT_{spec.ToUpperInvariant()} {(who is null ? "nobody" : $"{who.Name ?? who.Id} wet={who.WetPermille} up={who.ClimbedCm}")}");
                }

                if (spec == "held")
                {
                    // A1785: wait (fast) until someone awake carries a thing, then follow that one.
                    var fast = ScreenModel.TimeChoices(_world.Host.World.Calendar.Settings).Max(c => c.Speed);
                    var started = Time.GetTicksMsec();
                    who = null;
                    while (who is null && (Time.GetTicksMsec() - started) / 1000.0 < 600)
                    {
                        _world.Speed = fast;
                        await Frames(5);
                        who = _world.Host.Snapshot().Inhabitants.FirstOrDefault(i => i.Held is not null && !_world.LiesNow(i.Id));
                    }

                    _world.Speed = 1;
                    GD.Print($"EXPRESS_SHOT_HELD {(who is null ? "nobody carried anything" : $"{who.Name ?? who.Id} holds {who.Held}")}");
                }

                if (who is null || !_world.CloseLook(who.Id, ExpressShotDistanceM))
                {
                    GD.Print($"EXPRESS_SHOT_SKIPPED {spec}: nobody awake to follow");
                    continue;
                }

                // Let the calm pause of earlier moments run out, so the event shows (a knock skips it anyway).
                await ToSignal(GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);
                if (spec == "written")
                {
                    // A1356 (check runs only): a written belief on the nearest hard thing by the figure, its marks and its window.
                    var host = _world.Host;
                    var carrier = host.Snapshot().Objects
                        .Where(o => host.Objects.Catalog.Contains(o.TypeId) && host.Objects.Catalog.TrueProperties(o.TypeId).Contains("hard") && host.Writing.On(o.Id) is null)
                        .OrderBy(o => Math.Pow(o.Position.X - who.Position.X, 2) + Math.Pow(o.Position.Z - who.Position.Z, 2)).FirstOrDefault();
                    if (carrier is not null)
                    {
                        host.Writing.Place(new GlassWorld.Simulation.Writing.WrittenText(carrier.Id, "rock_painting", "belief:red_berry|poisonous|true", who.Id, host.World.Tick, 0.65));
                        _world.LookAt(carrier.Position.X, carrier.Position.Z, ExpressShotDistanceM);
                        await Frames(5);
                        _screen?.OpenThing(carrier.Id);
                    }

                    GD.Print($"EXPRESS_SHOT_WRITTEN {(carrier is null ? "no hard thing" : carrier.Id + " " + carrier.TypeId)}");
                }

                if (spec == "mark")
                {
                    // A1796: the calm place mark a thing just built gets - laid at the thing nearest the followed figure.
                    var near = _world.Host.Snapshot().Objects.OrderBy(o => Math.Pow(o.Position.X - who.Position.X, 2) + Math.Pow(o.Position.Z - who.Position.Z, 2)).FirstOrDefault();
                    var marked = near is not null && _world.MarkThing(new Simulation.Inhabitants.WorldEvent(Simulation.Inventions.InventionSystem.Built, _world.Host.World.Tick, who.Id, null, near.Id));
                    GD.Print($"EXPRESS_SHOT_MARK {(marked ? near!.Id + " " + near.TypeId : "none")}");
                }

                var shown = spec is "mark" or "written" ? null : _world.ExpressFor(who.Id, parts[0], parts.Length > 1 ? parts[1] : null);
                GD.Print($"EXPRESS_SHOT_EVENT {number} {spec} {who.Name ?? who.Id} shown={shown ?? "-"}");
                var waited = 0.0;
                foreach (var at in ExpressShotTimes)
                {
                    await ToSignal(GetTree().CreateTimer(at - waited), SceneTreeTimer.SignalName.Timeout);
                    waited = at;
                    var name = System.FormattableString.Invariant($"{size.X}x{size.Y}_{number:00}_{spec.Replace(':', '-')}_{at:0.0}s_{who.Name ?? who.Id}.png");
                    if (Shot(name))
                    {
                        shots++;
                    }
                }
            }

            GD.Print($"EXPRESS_SHOTS_DONE {shots}");
            ok = shots > 0;
        }
        catch (Exception e)
        {
            GD.PushError($"EXPRESS_SHOTS_FAIL {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            GetTree().Quit(ok ? 0 : 1);
        }
    }
}
