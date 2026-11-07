using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Simulation.Tests;

public sealed class WorldCalendarTests
{
    private static readonly WorldCalendar Calendar = TestData.Calendar();

    [Fact]
    public void WorldYearHas28GameDays()
    {
        // F5226 = A: 7 game days per season, four seasons.
        Assert.Equal(28 * Calendar.TicksPerDay, Calendar.TicksPerYear);
    }

    [Fact]
    public void FoundingIsYearOneSpringDayOneMidnight()
    {
        var t = Calendar.At(0);
        Assert.Equal((1L, "spring", 1, 1L, 0, 0, DayPhase.Night), (t.Year, t.SeasonId, t.DayOfSeason, t.Day, t.Hour, t.Minute, t.Phase));
    }

    [Fact]
    public void DayBoundary()
    {
        var last = Calendar.At(Calendar.TicksPerDay - 1);
        var next = Calendar.At(Calendar.TicksPerDay);
        Assert.Equal((1L, 23, 59), (last.Day, last.Hour, last.Minute));
        Assert.Equal((2L, 0, 0, 2), (next.Day, next.Hour, next.Minute, next.DayOfSeason));
    }

    [Fact]
    public void SeasonAndYearBoundaries()
    {
        var lastSpring = Calendar.At(Calendar.TicksPerSeason - 1);
        var firstSummer = Calendar.At(Calendar.TicksPerSeason);
        Assert.Equal(("spring", 7), (lastSpring.SeasonId, lastSpring.DayOfSeason));
        Assert.Equal(("summer", 1, 8L), (firstSummer.SeasonId, firstSummer.DayOfSeason, firstSummer.Day));

        var lastOfYear = Calendar.At(Calendar.TicksPerYear - 1);
        var newYear = Calendar.At(Calendar.TicksPerYear);
        Assert.Equal((1L, "winter", 28L), (lastOfYear.Year, lastOfYear.SeasonId, lastOfYear.Day));
        Assert.Equal((2L, "spring", 1, 29L), (newYear.Year, newYear.SeasonId, newYear.DayOfSeason, newYear.Day));
    }

    [Theory]
    [InlineData(0, 359, DayPhase.Night)]
    [InlineData(0, 360, DayPhase.Dawn)]
    [InlineData(0, 404, DayPhase.Dawn)]
    [InlineData(0, 405, DayPhase.Day)]
    [InlineData(0, 1139, DayPhase.Day)]
    [InlineData(0, 1140, DayPhase.Dusk)]
    [InlineData(0, 1185, DayPhase.Night)]
    [InlineData(3, 479, DayPhase.Night)]
    [InlineData(3, 480, DayPhase.Dawn)]
    [InlineData(3, 1020, DayPhase.Dusk)]
    public void DayPhaseFollowsSeasonalSunriseAndSunset(int seasonIndex, int minuteOfDay, DayPhase expected)
    {
        var tick = (seasonIndex * Calendar.TicksPerSeason) + (minuteOfDay * Calendar.Settings.TicksPerHour / 60);
        Assert.Equal(expected, Calendar.At(tick).Phase);
    }

    [Fact]
    public void WinterDaysAreShorterThanSummerDays()
    {
        static int DaylightMinutes(int season) =>
            Enumerable.Range(0, 1440).Count(m => Calendar.At((season * Calendar.TicksPerSeason) + (m * Calendar.Settings.TicksPerHour / 60)).Phase != DayPhase.Night);
        Assert.True(DaylightMinutes(3) < DaylightMinutes(1));
    }

    [Fact]
    public void NormalSpeedIs20RealMinutesPerGameDay()
    {
        Assert.Equal(1200, Calendar.RealSecondsPerTickAtNormalSpeed * Calendar.TicksPerDay, 9);
    }

    [Fact]
    public void InvalidSettingsAreRejected()
    {
        var json = File.ReadAllText(Path.Combine(TestData.Root, "content", "world", "time.json"));
        Assert.Throws<ContractViolationException>(() => WorldCalendar.FromJson(json.Replace("\"days_per_season\": 7", "\"days_per_season\": 0", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => WorldCalendar.FromJson(json.Replace("\"sunset_minute\": 1140", "\"sunset_minute\": 380", StringComparison.Ordinal)));
    }

    [Fact]
    public void NegativeTickIsRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => Calendar.At(-1));
}
