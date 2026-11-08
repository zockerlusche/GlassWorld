using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

public sealed class FigureMotionTests
{
    private static WorldTimeSettings Time() =>
        ContractSerializer.Deserialize<WorldTimeSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));

    [Fact]
    public void TickTimesComeFromTheCalendar()
    {
        var time = Time();
        Assert.Equal(3600.0 / time.TicksPerHour, FigureMotion.GameSecondsPerTick(time), 9);
        var real = time.NormalRealSecondsPerDay / (double)(time.TicksPerHour * time.HoursPerDay);
        Assert.Equal(real, FigureMotion.RealSecondsPerTick(time, 1), 9);
        Assert.Equal(real / 4, FigureMotion.RealSecondsPerTick(time, 4), 9);
    }

    [Fact]
    public void TheGaitSpeedIsInGameTimeAndThePlaybackFollowsTheScreen()
    {
        // 10 m in one tick of 60 game seconds is a stroll (16.7 cm/s); on screen it lasts 0.5 real seconds.
        var motion = new FigureMotion(new Point3(0, 0, 0), 360);
        motion.Retarget(new Point3(6, 0, 8), 1, 60, 0.5, 0.05);
        Assert.Equal(10, motion.StepM, 9);
        Assert.Equal(1000.0 / 60, motion.GameSpeedCmS, 9);
        Assert.Equal(2000, motion.ScreenSpeedCmS, 9);
        Assert.Equal(1.5, FigureMotion.GaitTimeScale(motion.ScreenSpeedCmS, 130, 1, 0.6, 1.5));
        Assert.Equal(1.0, FigureMotion.GaitTimeScale(130, 130, 1, 0.6, 1.5), 9);
        Assert.Equal(1.0, FigureMotion.GaitTimeScale(65, 130, 0.5, 0.6, 1.5), 9);
        Assert.Equal(0.6, FigureMotion.GaitTimeScale(0, 130, 1, 0.6, 1.5));
    }

    [Fact]
    public void TheFigureMovesInAStraightLineAtAnEvenPaceOverTheTick()
    {
        var motion = new FigureMotion(new Point3(0, 0, 0), 360);
        motion.Retarget(new Point3(10, 2, 0), 2, 60, 0.5, 0.05);
        motion.Advance(0.25);
        Assert.Equal(new Point3(2.5, 0.5, 0), motion.Position);
        motion.Advance(0.5);
        Assert.Equal(new Point3(7.5, 1.5, 0), motion.Position);
        motion.Advance(1);
        Assert.Equal(new Point3(10, 2, 0), motion.Position);

        // A new tick before the old stretch ended starts from where the figure is, not from the old target.
        var late = new FigureMotion(new Point3(0, 0, 0), 360);
        late.Retarget(new Point3(10, 0, 0), 1, 60, 1, 0.05);
        late.Advance(0.5);
        late.Retarget(new Point3(10, 0, 10), 1, 60, 1, 0.05);
        Assert.Equal(new Point3(5, 0, 0), late.Position);
        late.Advance(0.5);
        Assert.Equal(new Point3(7.5, 0, 5), late.Position);
        Assert.Equal(10, late.StepM, 9);
    }

    [Fact]
    public void FractionIsClampedAndZeroDurationMeansArrived()
    {
        Assert.Equal(0.5, FigureMotion.Fraction(1, 2));
        Assert.Equal(1, FigureMotion.Fraction(3, 2));
        Assert.Equal(0, FigureMotion.Fraction(-1, 2));
        Assert.Equal(1, FigureMotion.Fraction(0, 0));
    }

    [Fact]
    public void PlacingPutsTheFigureThereWithoutAWalk()
    {
        var motion = new FigureMotion(new Point3(0, 0, 0), 360);
        motion.Place(new Point3(40, 1, 30));
        motion.Advance(0.1);
        Assert.Equal(new Point3(40, 1, 30), motion.Position);
        Assert.Equal(0, motion.GameSpeedCmS);
        Assert.Equal(0, motion.StepM);
    }

    [Fact]
    public void TheFacingEasesAtTheTurnRateTheShortWayRound()
    {
        var motion = new FigureMotion(new Point3(0, 0, 0), 90);
        motion.Retarget(new Point3(10, 0, 0), 1, 60, 10, 0.05);
        motion.Advance(0.5);
        Assert.Equal(Math.PI / 4, motion.Yaw, 9);
        motion.Advance(1);
        Assert.Equal(Math.PI / 2, motion.Yaw, 9);

        // From +170° to −170° is 20° the short way, not 340°.
        var from = 170 * Math.PI / 180;
        var to = -170 * Math.PI / 180;
        var turned = FigureMotion.TurnTowards(from, to, 10 * Math.PI / 180);
        Assert.Equal(180 * Math.PI / 180, Math.Abs(turned), 9);

        // A tiny step (below the walk minimum) does not turn the figure.
        var still = new FigureMotion(new Point3(0, 0, 0), 360);
        still.Retarget(new Point3(0.01, 0, 0), 1, 60, 1, 0.05);
        still.Advance(1);
        Assert.Equal(0, still.Yaw, 9);
    }
}
