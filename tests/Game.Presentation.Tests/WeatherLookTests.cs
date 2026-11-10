using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// The weather look (B2-5 R23): every weather id has a look that sets it apart, changes blend softly in
/// real time (also at x16), and no brightness ever flashes (B2466: at most three light-dark changes per
/// second, whatever the data asks for).
/// </summary>
public sealed class WeatherLookTests
{
    private static readonly LookWeather Weather = Read<LookWeather>("look", "weather.json");

    private static readonly LookPalette Palette = Read<LookPalette>("look", "palette.json");

    private static readonly SeasonSlots Slots = SeasonSlots.From(Palette);

    private static readonly LookDaylight Daylight = Read<LookDaylight>("look", "daylight.json");

    private static readonly WorldCalendar Calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));

    private static double RealSecondsPerGameMinute => Calendar.RealSecondsPerTickAtNormalSpeed * Calendar.Settings.TicksPerHour / 60.0;

    private static WeatherMix Of(string weather, params string[] conditions) => WeatherMix.Of(Weather, Slots, weather, conditions);

    [Fact]
    public void EveryWeatherOfTheVocabularyAndTheClimateHasALookOfItsOwn()
    {
        var vocabulary = Read<Vocabulary>("vocabulary", "weather.json").Entries.Select(e => e.Id).ToList();
        var climate = Read<ClimateSettings>("climate", "temperate.json").Weather;
        var looks = Weather.Looks.Select(l => l.Weather).ToList();
        Assert.Equal(vocabulary.Order(StringComparer.Ordinal), looks.Order(StringComparer.Ordinal));
        Assert.All(climate.States, s => Assert.Contains(s.Weather, looks));

        // Recognisable: no two weathers show the same look.
        var mixes = looks.Select(w => Of(w)).ToList();
        Assert.Equal(mixes.Count, mixes.Distinct().Count());
        Assert.Equal(WeatherMix.Neutral(Slots), Of("clear"));
        Assert.Equal(WeatherMix.Neutral(Slots), WeatherMix.Of(Weather, Slots, null));
        Assert.Equal(WeatherMix.Neutral(Slots), Of("meteor_shower"));

        // What sets each apart (the task's list): clouds, rain, storm, fog, frost, snow with cover, heat, drought.
        Assert.True(Of("cloudy").Overcast > 0.5 && Of("cloudy").Rain == 0);
        Assert.True(Of("rain").Rain > 0 && Of("rain").Snow == 0);
        Assert.True(Of("storm").Rain > Of("rain").Rain && Of("storm").Light < Of("rain").Light && Of("storm").Sway > 1 && Of("storm").Glow > 0);
        Assert.True(Of("fog").FogDensity > 2 * looks.Where(w => w != "fog").Max(w => Of(w).FogDensity));
        Assert.True(Of("frost").SnowCover > 0 && Of("frost").Snow == 0);
        Assert.True(Of("snow").Snow > 0 && Of("snow").SnowCover > 0.5 && Of("snow").Rain == 0);
        Assert.True(Of("heat").Light > 1 && Of("heat").Colors.Any(c => c.Weight > 0));
        Assert.True(Of("drought").Colors.Count(c => c.Weight > 0) >= Of("heat").Colors.Count(c => c.Weight > 0));
        Assert.All(looks.Where(w => w != "storm"), w => Assert.Equal(0, Of(w).Glow));

        // Only colours a season changes too can be moved by the shader; anything else is a data error.
        Assert.All(Weather.Looks.SelectMany(l => l.Colors), c => Assert.Contains(c.Id, Slots.Ids));
        var bad = Read<LookWeather>("look", "weather.json");
        bad.Looks.First().Colors = [new LookColor { Id = "rock", Color = "#000000" }];
        Assert.Throws<InvalidDataException>(() => WeatherMix.Of(bad, Slots, bad.Looks.First().Weather));
        var palette = Palette.Colors.Select(c => c.Id).ToList();
        Assert.All(new[] { Weather.Clouds.Color, Weather.Rain.Color, Weather.Snow.Color }, id => Assert.Contains(id, palette));
    }

    [Fact]
    public void MixesAreEqualByValueAndTheBlendersHandOutTheSameMixOutsideABlend()
    {
        // The view writes shader parameters, sky and fog only when the mix changed (review GW3 #181).
        var rain = Of("rain");
        var same = rain with { Colors = [.. rain.Colors] };
        Assert.NotSame(rain.Colors, same.Colors);
        Assert.True(rain.Equals(same) && rain == same);
        Assert.Equal(rain.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(rain, same with { Rain = rain.Rain + 0.01 });
        Assert.NotEqual(rain, same with { Tint = new Rgb(0.5f, 0.5f, 0.5f) });
        Assert.NotEqual(Of("heat"), Of("heat") with { Colors = [.. Of("heat").Colors.Select(c => (c.Color, c.Weight / 2))] });

        // Laid over a season twice: equal results, so the season parameters are not written again.
        var summer = SeasonMix.Of(Palette, Slots, "summer");
        Assert.Equal(Of("drought").Over(summer), Of("drought").Over(summer));

        // Outside a blend the blenders return the very same mix every frame (nothing is composed then).
        var weather = new WeatherBlender(Weather, Slots);
        var season = new SeasonBlender(Palette, Slots);
        var (w, s) = (weather.Update("rain", ["rain"], 60, RealSecondsPerGameMinute, 1, 0.016), season.Update("summer", 0.016));
        Assert.Same(w, weather.Update("rain", ["rain"], 60, RealSecondsPerGameMinute, 1, 0.016));
        Assert.Same(s, season.Update("summer", 0.016));
        Assert.NotSame(w, weather.Update("storm", ["storm"], 60, RealSecondsPerGameMinute, 1, 0.016));

        // The shaded light and the sky of an unchanged moment are equal too.
        var day = DaylightMix.Of(Daylight, "day");
        var cloud = Rgb.Parse(Palette.Colors.Single(c => c.Id == Weather.Clouds.Color).Color);
        Assert.Equal(rain.Shade(day, Daylight.NightMinBrightness), same.Shade(day, Daylight.NightMinBrightness));
        Assert.Equal(rain.Sky(day, cloud), same.Sky(day, cloud));

        // The data keeps the cloud edges soft: equal step edges are undefined in the shader (review GW3 #183).
        Assert.True(Weather.Clouds.Softness >= 0.01);
    }

    [Fact]
    public void DroughtAsAConditionLaysItsDryColoursOverTheMainWeather()
    {
        // The reading: main weather clear, conditions clear + drought (after the dry days).
        var clear = Of("clear", "clear");
        var dry = Of("clear", "clear", "drought");
        var drought = Weather.Looks.Single(l => l.Weather == "drought");
        Assert.NotEqual(clear, dry);
        foreach (var c in drought.Colors)
        {
            Assert.Equal((Rgb.Parse(c.Color), 1.0), dry.Colors[Slots.IndexOf(c.Id)]);
        }

        // Everything but colours and tint stays the main weather's.
        Assert.Equal(clear with { Colors = dry.Colors, Tint = dry.Tint }, dry);
        Assert.Equal(Of("rain", "rain") with { Colors = Of("rain", "drought", "rain").Colors, Tint = Of("rain", "drought", "rain").Tint }, Of("rain", "drought", "rain"));

        // Over a season: the dry grass replaces the season's grass, the snow cover is at least the weather's.
        var summer = SeasonMix.Of(Palette, Slots, "summer");
        var shown = dry.Over(summer);
        var grass = Slots.IndexOf("grass");
        Assert.Equal(Rgb.Parse(drought.Colors.Single(c => c.Id == "grass").Color), shown.Colors[grass]);
        Assert.Equal(summer, clear.Over(summer));
        var winter = SeasonMix.Of(Palette, Slots, "winter");
        Assert.Equal(Math.Max(winter.SnowCover, Of("snow").SnowCover), Of("snow").Over(winter).SnowCover);
        Assert.Equal(Of("snow").SnowCover, Of("snow").Over(summer).SnowCover);
        Assert.Equal(winter.SnowCover, clear.Over(winter).SnowCover);
    }

    [Fact]
    public void AChangeBlendsOverTheTransitionMinutesButNeverFasterThanTheMinimum()
    {
        var blender = new WeatherBlender(Weather, Slots);
        var minute = RealSecondsPerGameMinute;

        // 60 game minutes: 50 real seconds at normal speed, a little over 3 at x16 - raised to the minimum.
        Assert.Equal(60 * minute, blender.DurationSeconds(60, minute, 1), 6);
        Assert.Equal(Weather.MinTransitionRealSeconds, blender.DurationSeconds(60, minute, 16));
        Assert.True(60 * minute / 16 < Weather.MinTransitionRealSeconds);
        Assert.Equal(Weather.MinTransitionRealSeconds, blender.DurationSeconds(0, minute, 1));

        // The first weather shows at once; a change starts from it.
        Assert.Equal(Of("clear"), blender.Update("clear", ["clear"], 60, minute, 1, 0.016));
        Assert.Equal(Of("clear"), blender.Update("rain", ["rain"], 60, minute, 1, 0));
        var half = blender.Update("rain", ["rain"], 60, minute, 1, 30 * minute);
        Assert.Equal(0.5, blender.T, 6);
        Assert.Equal(Of("rain").Rain / 2, half.Rain, 6);
        Assert.Equal(Of("rain").Overcast / 2, half.Overcast, 6);

        // Paused: nothing moves. Then to the end.
        Assert.Equal(half, blender.Update("rain", ["rain"], 60, minute, 0, 0));
        Assert.Equal(Of("rain"), blender.Update("rain", ["rain"], 60, minute, 1, 31 * minute));
        Assert.Equal(1, blender.T);

        // A new condition (drought) is a change too; a jump shows a weather at once.
        blender.Update("rain", ["drought", "rain"], 60, minute, 1, 0.016);
        Assert.True(blender.T < 1);
        Assert.Equal(Of("snow"), blender.Jump("snow", ["snow"]));
        Assert.Equal(1, blender.T);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(2000)]
    public void TheWeatherNeverJumpsWhateverTheSpeed(double speed)
    {
        // Frames of 1/60 s; the weather changes every six game hours (and here every few frames at an
        // absurd speed, in the middle of a blend). No value may move more than the steepest blend allows.
        const double Frame = 1.0 / 60;
        var ids = Weather.Looks.Select(l => l.Weather).ToList();
        var blender = new WeatherBlender(Weather, Slots);
        var minute = RealSecondsPerGameMinute;
        var changeEverySeconds = 6 * 60 * minute / speed;
        var previous = blender.Update(ids[0], [ids[0]], 60, minute, speed, Frame);
        var perFrame = (1.5 * Frame / Weather.MinTransitionRealSeconds) + 1e-9;
        var frames = (int)Math.Min(ids.Count * 2 * changeEverySeconds / Frame, 300_000);
        for (var f = 1; f <= frames; f++)
        {
            var id = ids[(int)(f * Frame / changeEverySeconds) % ids.Count];
            var mix = blender.Update(id, [id], 60, minute, speed, Frame);
            foreach (var (now, before, range) in new[]
            {
                (mix.Overcast, previous.Overcast, 1.0), (mix.CloudShade, previous.CloudShade, 1.0), (mix.FogDensity, previous.FogDensity, 0.2),
                (mix.FogSky, previous.FogSky, 1.0), (mix.Rain, previous.Rain, 1.0), (mix.Snow, previous.Snow, 1.0), (mix.Light, previous.Light, 2.0),
                (mix.Ambient, previous.Ambient, 2.0), (mix.Shadow, previous.Shadow, 1.0), (mix.SnowCover, previous.SnowCover, 1.0), (mix.Sway, previous.Sway, 2.0),
                (mix.Glow, previous.Glow, 1.0), (mix.Tint.R, previous.Tint.R, 1.0), (mix.Tint.B, previous.Tint.B, 1.0),
            })
            {
                Assert.InRange(Math.Abs(now - before), 0, perFrame * range);
            }

            for (var i = 0; i < mix.Colors.Count; i++)
            {
                Assert.InRange(Math.Abs(mix.Colors[i].Weight - previous.Colors[i].Weight), 0, perFrame);
            }

            previous = mix;
        }
    }

    [Fact]
    public void TheWeatherShadesTheDayLightAndKeepsTheNightReadable()
    {
        var day = DaylightMix.Of(Daylight, "day");
        var night = DaylightMix.Of(Daylight, "night");
        var floor = Daylight.NightMinBrightness;
        Assert.Equal(day, Of("clear").Shade(day, floor));

        var storm = Of("storm").Shade(day, floor);
        Assert.True(storm.LightEnergy < day.LightEnergy * 0.6 && storm.ShadowOpacity < day.ShadowOpacity * 0.5);
        Assert.True(storm.AmbientEnergy < day.AmbientEnergy && storm.WaterLight.R < day.WaterLight.R);
        Assert.Equal(day.LightElevationDeg, storm.LightElevationDeg);
        Assert.True(Of("heat").Shade(day, floor).LightEnergy > day.LightEnergy);
        Assert.True(Of("heat").Shade(day, floor).LightColor.B < day.LightColor.B, "heat tints the light warm");

        // A storm at night never darkens the ambient light below the night floor of the daylight data.
        foreach (var weather in Weather.Looks.Select(l => l.Weather))
        {
            Assert.True(Of(weather).Shade(night, floor).AmbientEnergy >= Math.Min(night.AmbientEnergy, floor) - 1e-9, weather);
        }

        // Clouds and fog: the cover and density of the data, clouds dark at night, fog in the horizon's tone.
        var cloud = Rgb.Parse(Palette.Colors.Single(c => c.Id == Weather.Clouds.Color).Color);
        var clearSky = Of("clear").Sky(day, cloud);
        Assert.Equal((0.0, 0.0), (clearSky.Clouds, clearSky.FogDensity));
        Assert.Equal(day.SkyHorizon, clearSky.FogColor);
        var stormDay = Of("storm").Sky(day, cloud);
        var stormNight = Of("storm").Sky(night, cloud);
        Assert.Equal((Of("storm").Overcast, Of("storm").FogDensity, Of("storm").FogSky), (stormDay.Clouds, stormDay.FogDensity, stormDay.FogSky));
        Assert.True(stormNight.CloudColor.R < stormDay.CloudColor.R && stormDay.CloudColor.R < Of("cloudy").Sky(day, cloud).CloudColor.R);
        Assert.Equal(stormDay.CloudColor.R, stormDay.FogColor.R, 5);

        // The glow lifts ambient light and clouds a little, nothing else.
        var glowing = Of("storm").Shade(day, floor, 1, Weather.Glow.AmbientBoost);
        Assert.Equal(storm.AmbientEnergy + Weather.Glow.AmbientBoost, glowing.AmbientEnergy, 9);
        Assert.Equal(storm with { AmbientEnergy = glowing.AmbientEnergy }, glowing);
        Assert.True(Of("storm").Sky(day, cloud, 1, Weather.Glow.SkyBoost).CloudColor.R > stormDay.CloudColor.R);
    }

    [Theory]
    [InlineData(3, 0.05, 0.05)]
    [InlineData(2, 0.3, 0.6)]
    [InlineData(1, 0.8, 1.8)]
    [InlineData(50, 0.01, 0.01)]
    public void TheFlickerLimiterNeverTurnsMoreOftenThanAllowedAndNeverJumps(double maxChanges, double rise, double fall)
    {
        // The worst inputs: a square wave at 30 Hz, then random values every frame, at 60 and 144 fps.
        foreach (var fps in new[] { 60, 144 })
        {
            var limiter = new FlickerLimiter(maxChanges, rise, fall);
            Assert.InRange(limiter.MaxChangesPerSecond, 0, FlickerLimiter.AllowedChangesPerSecond);
            var dt = 1.0 / fps;
            var turns = new List<double>();
            var (value, direction) = (0.0, 0);
            var random = new Random(7);
            for (var f = 0; f < fps * 40; f++)
            {
                var wanted = f < fps * 20 ? f * 30 / fps % 2 : random.NextDouble();
                var next = limiter.Update(wanted, dt);
                var step = next - value;

                // Soft: never faster than a full rise or fall over the given (at least 0.05 s) times.
                Assert.InRange(step, -dt / Math.Max(fall, 0.05) - 1e-9, (dt / Math.Max(rise, 0.05)) + 1e-9);
                Assert.InRange(next, 0, 1);
                if (step != 0 && Math.Sign(step) != direction)
                {
                    if (direction != 0)
                    {
                        turns.Add(f * dt);
                    }

                    direction = Math.Sign(step);
                }

                value = next;
            }

            // In every window of one second: at most the allowed number of light-dark changes (B2466: three).
            Assert.NotEmpty(turns);
            for (var i = 0; i < turns.Count; i++)
            {
                var inWindow = turns.Skip(i).TakeWhile(t => t < turns[i] + 1).Count();
                Assert.InRange(inWindow, 1, (int)Math.Floor(limiter.MaxChangesPerSecond));
                Assert.InRange(inWindow, 1, 3);
            }
        }
    }

    [Fact]
    public void TheStormGlowIsSoftRareAndStopsWithTheStorm()
    {
        // The data may not ask for more than the accessibility rule allows; the game caps it anyway.
        var data = Weather.Glow;
        Assert.InRange(data.MaxChangesPerSecond, 0.01, FlickerLimiter.AllowedChangesPerSecond);
        Assert.True(data.IntervalS.Min >= data.RiseS + data.HoldS + data.FallS, "a glow has faded before the next begins");

        const double Frame = 1.0 / 60;
        var glow = new StormGlow(data);
        var (value, direction, turns, peak) = (0.0, 0, new List<double>(), 0.0);
        for (var f = 0; f < 60 * 300; f++)
        {
            var next = glow.Update(1, Frame);
            var step = next - value;
            Assert.InRange(step, -Frame / data.FallS - 1e-9, (Frame / data.RiseS) + 1e-9);
            if (step != 0 && Math.Sign(step) != direction)
            {
                turns.Add(f * Frame);
                direction = Math.Sign(step);
            }

            (value, peak) = (next, Math.Max(peak, next));
        }

        // It glowed several times in five minutes, fully, and never turned more than three times in a second.
        Assert.True(peak > 0.95, $"peak {peak:0.00}");
        Assert.InRange(turns.Count / 2, 300 / (int)Math.Ceiling(data.IntervalS.Max + data.RiseS + data.HoldS + 1), 300 / (int)data.IntervalS.Min);
        for (var i = 0; i < turns.Count; i++)
        {
            Assert.InRange(turns.Skip(i).TakeWhile(t => t < turns[i] + 1).Count(), 1, 3);
        }

        // Paused: it holds. No storm: it fades out softly and stays dark.
        var held = glow.Update(1, 0);
        Assert.Equal(held, glow.Update(1, 0));
        for (var f = 0; f < 60 * 60; f++)
        {
            var next = glow.Update(0, Frame);
            Assert.True(next <= value + 1e-12, "without a storm the glow only fades");
            value = next;
        }

        Assert.Equal(0, glow.Value);
    }

    private static T Read<T>(string folder, string file)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", folder, file)));
}
