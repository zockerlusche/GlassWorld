using System.Globalization;
using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>
/// One picture of the screenshot tool (B2 R32): a camera point in a projection and day phase, and
/// optionally an epoch, season and weather. <see cref="SeasonHasLook"/> and <see cref="WeatherHasLook"/>
/// say whether the presentation data can show that season or weather yet; if not, the picture is
/// rendered without it and its file name carries <see cref="ShotPlan.NoLookNote"/>.
/// </summary>
public sealed record Shot(
    ShotPoint Point,
    string Projection,
    string Phase,
    string? Epoch = null,
    string? Season = null,
    bool SeasonHasLook = true,
    string? Weather = null,
    bool WeatherHasLook = true)
{
    /// <summary>
    /// Deterministic file name: <c>island_{point}_{projection}_{phase}</c>, then <c>_{epoch}</c>,
    /// <c>_{season}</c> and <c>_{weather}</c> where the data lists them (each with
    /// <c>-nolook</c> while it has no look yet), then <c>_{width}x{height}.png</c>.
    /// </summary>
    public string FileName(int width, int height)
    {
        var name = $"island_{Point.Id}_{Projection}_{Phase}";
        name += Epoch is null ? string.Empty : $"_{Epoch}";
        name += Season is null ? string.Empty : $"_{Season}{(SeasonHasLook ? string.Empty : "-" + ShotPlan.NoLookNote)}";
        name += Weather is null ? string.Empty : $"_{Weather}{(WeatherHasLook ? string.Empty : "-" + ShotPlan.NoLookNote)}";
        return string.Create(CultureInfo.InvariantCulture, $"{name}_{width}x{height}.png");
    }
}

/// <summary>
/// The screenshot plan from <c>content/look/shots.json</c> (B2 R32): every point × projection × day phase,
/// and with the optional lists also × epoch × season × weather. Without those lists the plan and the file
/// names are exactly those of the tool before them. Pictures of one epoch come one after the other, so
/// the island is recoloured only when it changes. A point may force its season (<c>season</c>): it is then
/// shot once with it instead of once per season of the set. Presentation only.
/// </summary>
public static class ShotPlan
{
    /// <summary>File name note of a season or weather that has no look yet (B2-5 brings them).</summary>
    public const string NoLookNote = "nolook";

    /// <summary>
    /// Builds the plan. <paramref name="seasonsWithLook"/> and <paramref name="weatherWithLook"/> are the
    /// ids the presentation data can show (season palettes, weather looks).
    /// </summary>
    public static IReadOnlyList<Shot> Build(LookShots shots, IReadOnlySet<string> seasonsWithLook, IReadOnlySet<string> weatherWithLook)
    {
        ArgumentNullException.ThrowIfNull(shots);
        ArgumentNullException.ThrowIfNull(seasonsWithLook);
        ArgumentNullException.ThrowIfNull(weatherWithLook);
        IEnumerable<string?> Optional(ICollection<string>? ids) => ids is { Count: > 0 } ? ids.Select(id => (string?)id) : [null];
        var plan = new List<Shot>();
        var firstSeason = Optional(shots.Seasons).First();
        foreach (var epoch in Optional(shots.Epochs))
        {
            foreach (var listedSeason in Optional(shots.Seasons))
            {
                foreach (var weather in Optional(shots.Weather))
                {
                    foreach (var point in shots.Points)
                    {
                        // A point that forces its season is shot once (in the round of the first listed one).
                        if (point.Season is not null && listedSeason != firstSeason)
                        {
                            continue;
                        }

                        var season = point.Season ?? listedSeason;
                        foreach (var projection in shots.Projections)
                        {
                            // A point may name its one phase (e.g. the night set: overview at night, close view at dusk).
                            foreach (var phase in shots.Phases.Where(h => point.Phase is null || h == point.Phase))
                            {
                                plan.Add(new Shot(
                                    point,
                                    projection,
                                    phase,
                                    epoch,
                                    season,
                                    season is null || seasonsWithLook.Contains(season),
                                    weather,
                                    weather is null || weatherWithLook.Contains(weather)));
                            }
                        }
                    }
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// The palette of a picture (B2 R19): the base colours, then the season's overrides from
    /// <c>palette.json</c>, then the epoch's from <c>epochs.json</c>. Only colours change, never models or
    /// shaders. An unknown epoch is an error; a season without a palette leaves the colours as they are.
    /// </summary>
    public static LookPalette PaletteFor(LookPalette palette, LookEpochs epochs, string? epoch, string? season)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(epochs);
        var colors = palette.Colors.ToDictionary(c => c.Id, c => c.Color, StringComparer.Ordinal);
        void Override(IEnumerable<LookColor> overrides)
        {
            foreach (var c in overrides)
            {
                if (!colors.ContainsKey(c.Id))
                {
                    throw new InvalidDataException($"Override of unknown palette colour '{c.Id}'.");
                }

                colors[c.Id] = c.Color;
            }
        }

        if (season is not null && palette.Seasons.FirstOrDefault(s => s.Season == season) is { } seasonPalette)
        {
            Override(seasonPalette.Colors);
        }

        if (epoch is not null)
        {
            Override((epochs.Epochs.FirstOrDefault(e => e.Id == epoch) ?? throw new InvalidDataException($"Unknown epoch '{epoch}' in shots.json.")).Colors);
        }

        return new LookPalette
        {
            ContractVersion = palette.ContractVersion,
            Colors = [.. palette.Colors.Select(c => new LookColor { Id = c.Id, Color = colors[c.Id] })],
            Ramp = palette.Ramp,
            OutlineWidthPx = palette.OutlineWidthPx,
            OutlineColor = palette.OutlineColor,
            SeasonBlendRealSeconds = palette.SeasonBlendRealSeconds,
            Seasons = palette.Seasons,
        };
    }
}
