using GlassWorld.Game.Localization;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Check runs only (W1936, TL #22540): pictures of inhabitants at the moment they look at, pick or eat something,
/// so a person can see whether the figure stands in reach in front of the thing and the thing can be recognised.
/// A fresh world runs fast; whenever an inhabitant performs one of the chosen actions at a target (it stands -
/// no gait - and has a target), the camera follows it, the world slows to normal speed, and after a short wait a
/// picture is taken. The log names action, target type and the distance figure-target in metres.
/// </summary>
public partial class Main
{
    private const string ActionShotsArgument = "--action-shots=";
    private const string ActionShotsCountArgument = "--action-shots-count=";
    private const string ActionShotsActionsArgument = "--action-shots-actions=";
    private const string ActionShotsMaxArgument = "--action-shots-max-s=";

    private bool _actionShots;
    private int _actionShotsCount = 10;
    private string[] _actionShotsActions = ["inspect", "eat"];
    private double _actionShotsMaxS = 900;

    /// <summary>Camera distance for a picture (metres): close enough to recognise a fruit, wide enough for the figure.</summary>
    private const double ActionShotDistanceM = 7;

    /// <summary>Reads the picture-mode arguments; true when the argument was one of them.</summary>
    private bool ReadActionShotsArgument(string arg)
    {
        if (arg.StartsWith(ActionShotsArgument, StringComparison.Ordinal))
        {
            _shotsFolder = arg[ActionShotsArgument.Length..];
            _actionShots = true;
            return true;
        }

        if (arg.StartsWith(ActionShotsCountArgument, StringComparison.Ordinal) && int.TryParse(arg.AsSpan(ActionShotsCountArgument.Length), out var count) && count > 0)
        {
            _actionShotsCount = count;
            return true;
        }

        if (arg.StartsWith(ActionShotsActionsArgument, StringComparison.Ordinal))
        {
            _actionShotsActions = arg[ActionShotsActionsArgument.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return true;
        }

        if (arg.StartsWith(ActionShotsMaxArgument, StringComparison.Ordinal)
            && double.TryParse(arg[ActionShotsMaxArgument.Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var max) && max > 0)
        {
            _actionShotsMaxS = max;
            return true;
        }

        return false;
    }

    private async Task ActionShotsAsync()
    {
        var ok = false;
        try
        {
            TextCatalogs.SetLanguage("de");
            DirAccess.MakeDirRecursiveAbsolute(_shotsFolder!);
            var size = GetWindow().Size;
            var wanted = _actionShotsActions.ToHashSet(StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var started = Time.GetTicksMsec();
            var shots = 0;
            await Frames(30);
            var fast = ScreenModel.TimeChoices(_world!.Host!.World.Calendar.Settings).Max(c => c.Speed);
            GD.Print($"ACTION_SHOTS_START actions={string.Join(',', wanted)} count={_actionShotsCount} max_s={_actionShotsMaxS} size={size.X}x{size.Y}");
            while (shots < _actionShotsCount && (Time.GetTicksMsec() - started) / 1000.0 < _actionShotsMaxS)
            {
                _world.Speed = fast;
                await Frames(1);
                var snapshot = _world.Host.Snapshot();
                var hit = snapshot.Inhabitants.FirstOrDefault(i =>
                    i.Action is { } a && wanted.Contains(a) && i.Gait is null && i.TargetId is { } t && !taken.Contains($"{i.Id}|{a}|{t}"));
                if (hit is null)
                {
                    continue;
                }

                taken.Add($"{hit.Id}|{hit.Action}|{hit.TargetId}");
                _world.Speed = 1;

                // Close enough to recognise the thing: the camera looks at the middle between figure and target.
                var aim = snapshot.Objects.FirstOrDefault(o => o.Id == hit.TargetId);
                if (aim is null || !_world.LookAt((hit.Position.X + aim.Position.X) / 2, (hit.Position.Z + aim.Position.Z) / 2, ActionShotDistanceM))
                {
                    _world.ChooseFigure(hit.Id);
                }

                await Frames(45);

                // The moment may have passed while the camera turned; take it only if the action still runs.
                var now = _world.Host.Snapshot();
                var still = now.Inhabitants.FirstOrDefault(i => i.Id == hit.Id);
                if (still is null || still.Action != hit.Action || still.TargetId != hit.TargetId)
                {
                    GD.Print($"ACTION_SHOT_MISSED {hit.Name ?? hit.Id} {hit.Action} {hit.TargetId}");
                    continue;
                }

                var target = now.Objects.FirstOrDefault(o => o.Id == hit.TargetId);
                var distance = target is null ? double.NaN : Math.Sqrt(Math.Pow(target.Position.X - still.Position.X, 2) + Math.Pow(target.Position.Z - still.Position.Z, 2));
                var type = target?.TypeId ?? "?";
                var name = $"{size.X}x{size.Y}_{shots + 1:00}_{hit.Action}_{type}_{hit.Name ?? hit.Id}.png";
                if (Shot(name))
                {
                    shots++;
                }

                GD.Print(System.FormattableString.Invariant($"ACTION_SHOT {shots} {hit.Name ?? hit.Id} action={hit.Action} target={type} distance_m={distance:0.00} day={now.Time.Day} {now.Time.Hour:00}:{now.Time.Minute:00}"));
            }

            GD.Print($"ACTION_SHOTS_DONE {shots} of {_actionShotsCount} in {(Time.GetTicksMsec() - started) / 1000} s");
            ok = shots > 0;
        }
        catch (Exception e)
        {
            GD.PushError($"ACTION_SHOTS_FAIL {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            GetTree().Quit(ok ? 0 : 1);
        }
    }
}
