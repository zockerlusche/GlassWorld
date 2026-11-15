using GlassWorld.Game.Localization;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Check runs only (A1712, D1112 #1): pictures of an inhabitant standing behind a cover (rock niche, bush, tree), so a
/// person can see whether its see-through outline shows through the cover. The world runs fast; when an inhabitant
/// stands near a thing of one of the given object types, the world pauses and the camera looks at the figure across
/// the thing - the thing between camera and figure - and takes a picture, then one from the open side for comparison.
/// The log names the cover type and the distance figure-cover in metres; COVER_SHOTS_SEEK says how many covers there
/// are and how near the nearest inhabitant is. Use it with --load-save=&lt;a save of the start island&gt;: --seed alone
/// makes a small world without the island preset, which has no covers.
/// </summary>
public partial class Main
{
    private const string CoverShotsArgument = "--cover-shots=";
    private const string CoverShotsTypesArgument = "--cover-shots-types=";
    private const string CoverShotsCountArgument = "--cover-shots-count=";

    /// <summary>Camera distance and pitch for a picture across the cover: low, so the cover stands in front of the figure.</summary>
    private const double CoverShotDistanceM = 6;
    private const double CoverShotPitchDeg = 12;

    private bool _coverShots;
    private int _coverShotsCount = 6;
    private string[] _coverShotsTypes = ["rock_niche", "berry_bush", "hazel_bush", "thorn_bush", "deciduous_tree", "birch_tree", "conifer_tree", "fruit_tree", "palm_tree", "young_tree"];

    /// <summary>Reads the cover-picture arguments; true when the argument was one of them.</summary>
    private bool ReadCoverShotsArgument(string arg)
    {
        if (arg.StartsWith(CoverShotsArgument, StringComparison.Ordinal))
        {
            _shotsFolder = arg[CoverShotsArgument.Length..];
            _coverShots = true;
            return true;
        }

        if (arg.StartsWith(CoverShotsTypesArgument, StringComparison.Ordinal))
        {
            _coverShotsTypes = arg[CoverShotsTypesArgument.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return true;
        }

        if (arg.StartsWith(CoverShotsCountArgument, StringComparison.Ordinal) && int.TryParse(arg.AsSpan(CoverShotsCountArgument.Length), out var count) && count > 0)
        {
            _coverShotsCount = count;
            return true;
        }

        return false;
    }

    private async Task CoverShotsAsync()
    {
        var ok = false;
        try
        {
            TextCatalogs.SetLanguage("de");
            DirAccess.MakeDirRecursiveAbsolute(_shotsFolder!);
            var size = GetWindow().Size;
            var types = _coverShotsTypes.ToHashSet(StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var started = Time.GetTicksMsec();
            var shots = 0;
            var looks = 0;
            await Frames(30);
            var fast = ScreenModel.TimeChoices(_world!.Host!.World.Calendar.Settings).Max(c => c.Speed);
            GD.Print($"COVER_SHOTS_START types={string.Join(',', types)} count={_coverShotsCount} size={size.X}x{size.Y}");
            var all = _world.Host.Snapshot().Objects.GroupBy(o => o.TypeId).OrderByDescending(g => g.Count()).Take(15).Select(g => g.Key + "=" + g.Count());
            GD.Print($"COVER_SHOTS_WORLD objects={_world.Host.Snapshot().Objects.Count} top={string.Join(',', all)}");
            while (shots < _coverShotsCount && (Time.GetTicksMsec() - started) / 1000.0 < 900)
            {
                _world.Speed = fast;
                await Frames(10);
                var snapshot = _world.Host.Snapshot();
                var pairs = snapshot.Inhabitants
                    .SelectMany(who => snapshot.Objects
                        .Where(o => types.Contains(o.TypeId))
                        .Select(o => (Who: who, Cover: o, Distance: Math.Sqrt(Math.Pow(o.Position.X - who.Position.X, 2) + Math.Pow(o.Position.Z - who.Position.Z, 2)))))
                    .Where(p => !taken.Contains(p.Who.Id + "|" + p.Cover.Id))
                    .OrderBy(p => p.Distance)
                    .ToList();
                if (++looks % 30 == 1)
                {
                    // Why no picture comes: how many covers there are and how near the nearest inhabitant is.
                    var counts = string.Join(',', snapshot.Objects.Where(o => types.Contains(o.TypeId)).GroupBy(o => o.TypeId).Select(g => g.Key + "=" + g.Count()));
                    GD.Print(System.FormattableString.Invariant($"COVER_SHOTS_SEEK day={snapshot.Time.Day} covers={counts} nearest_m={(pairs.Count > 0 ? pairs[0].Distance : -1):0.0}"));
                }

                var pair = pairs.FirstOrDefault(p => p.Distance is >= 1.0 and <= 3.5);
                if (pair.Who is null)
                {
                    continue;
                }

                taken.Add(pair.Who.Id + "|" + pair.Cover.Id);
                _world.Speed = 0;

                // Across the cover: the camera stands beyond the thing as seen from the figure, looking back at the figure.
                var yaw = Math.Atan2(pair.Cover.Position.X - pair.Who.Position.X, pair.Cover.Position.Z - pair.Who.Position.Z) * 180 / Math.PI;
                // Both pictures of a pair carry the pair's number (the counter rises with the 'behind' picture).
                var number = shots + 1;
                foreach (var (side, turn) in new[] { ("behind", 0.0), ("open", 180.0) })
                {
                    if (!_world.LookFrom(pair.Who.Position.X, pair.Who.Position.Z, yaw + turn, CoverShotDistanceM, CoverShotPitchDeg))
                    {
                        break;
                    }

                    await Frames(20);
                    var name = System.FormattableString.Invariant($"{size.X}x{size.Y}_{number:00}_{side}_{pair.Cover.TypeId}_{pair.Who.Name ?? pair.Who.Id}.png");
                    if (Shot(name) && side == "behind")
                    {
                        shots++;
                    }

                    GD.Print(System.FormattableString.Invariant($"COVER_SHOT {number} {side} {pair.Who.Name ?? pair.Who.Id} cover={pair.Cover.TypeId} distance_m={pair.Distance:0.00} yaw={yaw + turn:0}"));
                }
            }

            GD.Print($"COVER_SHOTS_DONE {shots} of {_coverShotsCount} in {(Time.GetTicksMsec() - started) / 1000} s");
            ok = shots > 0;
        }
        catch (Exception e)
        {
            GD.PushError($"COVER_SHOTS_FAIL {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            GetTree().Quit(ok ? 0 : 1);
        }
    }
}
