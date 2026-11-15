using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Player;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1340 (Bogen 31 "Wetter beeinflussen"): the player wishes a weather of the climate for favour; the core checks it.</summary>
public sealed class WeatherWishesTests
{
    private static readonly string Content = Path.Combine(TestPaths.Root, "content");

    [Fact]
    public void EveryWeatherOfTheClimateButTheCurrentOneCanBeWishedAndTheCoreTakesTheWish()
    {
        var host = SimulationHost.CreateGeneratedWorld(Content, 7, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        var weather = host.WeatherSystem!;
        var wishes = WeatherWishes.For(host);
        Assert.Equal(weather.Weathers, wishes.Select(w => w.Weather));
        var now = Assert.Single(wishes, w => w.Current);
        Assert.Equal((weather.Current, WeatherIntervention.ReasonUnchanged), (now.Weather, now.Reason));
        Assert.All(wishes.Where(w => !w.Current), w => Assert.True(w.Possible == host.Player.CanAfford(WeatherWishes.Intervention), w.Weather));

        var other = wishes.First(w => w.Possible);
        var favour = host.Player.Favor;
        Assert.True(host.Decisions.Submit(WeatherWishes.Command(other.Weather)).Accepted);
        host.World.Run(1);
        Assert.Equal(other.Weather, weather.Current);
        // It cost the data's favour (favour grows back a little each tick, content/player/favor.json regrow_per_hour).
        var cost = host.Player.CostOf(WeatherWishes.Intervention);
        Assert.InRange(host.Player.Favor, favour - cost, favour - cost + 0.5);
    }

    [Fact]
    public void WithoutEnoughFavourNoWeatherCanBeWished()
    {
        var host = SimulationHost.CreateGeneratedWorld(Content, 7, new MockDecisionPort(), "de", "start_island");
        host.Player.SetStartFavor(25); // less than a weather wish costs (favor.json)
        Assert.True(host.Player.CostOf(WeatherWishes.Intervention) > 25);
        Assert.All(WeatherWishes.For(host), w => Assert.False(w.Possible));
        Assert.All(WeatherWishes.For(host).Where(w => !w.Current), w => Assert.Equal(PlayerSystem.ReasonNoFavor, w.Reason));
    }
}
