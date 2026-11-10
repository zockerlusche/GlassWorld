using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// W1695, W1729: a sleeping inhabitant sits down, rolls onto its side and lies fitted to the ground, never
/// stands, never sinks into the ground and never turns its body while it is down.
/// </summary>
public sealed class LyingPoseTests
{
    /// <summary>A body lying on its left side with the head to +X and the legs to +Z, about its hips.</summary>
    private static readonly BodyPoint[] Body =
    [
        new(0, 0, 0, 0.15),
        new(0.45, 0, -0.02, 0.14),
        new(0.8, 0, 0.05, 0.11),
        new(0.5, -0.17, 0, 0.06),
        new(-0.25, -0.07, 0.3, 0.06),
        new(-0.5, -0.07, 0.15, 0.05),
        new(-0.45, 0.07, 0.25, 0.05),
    ];

    private static LookFigures Figures => Read<LookFigures>("look", "figures.json");

    private static T Read<T>(params string[] path)
        where T : class =>
        ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    [Fact]
    public void SleepPlaysAnApprovedClipLyingAndNoIdleBehaviour()
    {
        var library = Read<AnimationLibrary>("animation", "library.json");
        var lying = Figures.Lying;
        var chooser = new AnimationChooser(library);
        var values = new Dictionary<string, double> { ["sleep"] = 0.1 };
        for (long slot = 0; slot < 50; slot++)
        {
            var clip = chooser.ForInhabitant("sleep", false, "inh-000001", slot, values);
            Assert.False(clip.Clip.FellBack);
            Assert.Null(clip.Missing);
            Assert.True(LyingPose.IsLying(lying, clip.Clip.ClipId), clip.Clip.ClipId);
        }

        Assert.False(chooser.StandsIdle("sleep"));
    }

    [Fact]
    public void LyingDownUsesTheLibrarysSitTransitionsAndASeatedLoop()
    {
        // The placeholder is built from approved clips only: stand → sit, the seated loop on its side, sit → stand.
        var library = Read<AnimationLibrary>("animation", "library.json");
        var figures = Figures;
        var chooser = new AnimationChooser(library);
        foreach (var id in new[] { figures.Lying.SitClip, figures.Lying.RiseClip })
        {
            var clip = chooser.ForClip(id);
            Assert.False(clip.FellBack, id);
            Assert.False(clip.Loop, id);
        }

        var seated = figures.Sitting.SeatedClips.Select(id => chooser.ForClip(id).Asset).ToHashSet();
        foreach (var id in figures.Lying.Clips)
        {
            Assert.Contains(chooser.ForClip(id).Asset, seated);
        }

        Assert.True(figures.Lying.SinkFrom < figures.Lying.SinkTo);
    }

    [Fact]
    public void ItSitsDownFirstThenRollsOntoItsSideAndGetsUpInReverse()
    {
        var lying = Figures.Lying;
        var total = LyingPose.TotalS(lying);
        Assert.Equal(lying.SitS + lying.RollS, total);

        // Standing: nothing of it. While it sits down it does not roll; the hips reach the ground before the roll.
        Assert.Equal(new LieStage(0, 0, LieClip.Sit), LyingPose.Stage(lying, 0, down: true));
        var sitting = LyingPose.Stage(lying, lying.SitS * 0.6, down: true);
        Assert.Equal((0.0, LieClip.Sit), (sitting.Roll, sitting.Clip));
        Assert.InRange(sitting.Sit, 0.01, 0.99);
        Assert.Equal(new LieStage(1, 0, LieClip.Lie), LyingPose.Stage(lying, lying.SitS, down: true));
        Assert.Equal(new LieStage(1, 1, LieClip.Lie), LyingPose.Stage(lying, total, down: true));

        // The roll is eased: slow at both ends, never backwards.
        var last = 0.0;
        for (var i = 0; i <= 100; i++)
        {
            var roll = LyingPose.Stage(lying, lying.SitS + (lying.RollS * i / 100.0), down: true).Roll;
            Assert.True(roll >= last, $"roll goes back at {i}");
            last = roll;
        }

        Assert.True(LyingPose.Stage(lying, lying.SitS + (lying.RollS * 0.1), down: true).Roll < 0.05);
        Assert.Equal(0.5, LyingPose.Stage(lying, lying.SitS + (lying.RollS * 0.5), down: true).Roll, 9);

        // Getting up: the same shares on the way back, rolling up in the seated loop, then the rise transition.
        Assert.Equal(LieClip.Lie, LyingPose.Stage(lying, total - 0.1, down: false).Clip);
        Assert.Equal(LieClip.Rise, LyingPose.Stage(lying, lying.SitS, down: false).Clip);
        Assert.Equal(sitting.Sit, LyingPose.Stage(lying, lying.SitS * 0.6, down: false).Sit);
        Assert.Equal(LieClip.Rise, LyingPose.Stage(lying, 0.01, down: false).Clip);

        // The seconds run towards lying and back and stop at both ends; the transitions fit the sitting time.
        Assert.Equal(total, LyingPose.Advance(lying, total - 0.01, down: true, 0.5));
        Assert.Equal(0.25, LyingPose.Advance(lying, 0, down: true, 0.25));
        Assert.Equal(0, LyingPose.Advance(lying, 0.1, down: false, 0.5));
        Assert.Equal(1, LyingPose.ClipSpeed(lying, lying.SitS), 9);
        Assert.Equal(0.5, LyingPose.ClipSpeed(lying, lying.SitS / 2), 9);
    }

    [Fact]
    public void ItLiesOnItsSideAndTheHipsSlideAwayFromTheHead()
    {
        var figures = Figures;
        var lying = figures.Lying;

        // A quarter turn about the facing axis: on its side, not on its back like a falling plank.
        Assert.Equal(90, Math.Abs(lying.RollDeg));
        Assert.Equal(0, LyingPose.RollRad(lying, 0));
        Assert.Equal(lying.RollDeg * Math.PI / 180, LyingPose.RollRad(lying, 1), 9);

        // The roll turns about the seated hips; the head goes down to the figure's left (+X) for a negative
        // roll, so the hips slide to its right.
        var (y, z) = LyingPose.Pivot(lying, figures.Sitting, 2);
        Assert.Equal((2 * figures.Sitting.ClipSeatHeightM, -2 * lying.HipBackM), (y, z));
        Assert.Equal(0, LyingPose.Slide(lying, 1, 0));
        Assert.Equal(Math.Sign(lying.RollDeg) * lying.HipSlideM * 2, LyingPose.Slide(lying, 2, 1), 9);
        Assert.InRange(lying.HipOpen, 0.2, 0.9);
    }

    [Fact]
    public void TheLegsLeaveTheSeatedPoseOnlyLateInTheRoll()
    {
        // Opened earlier, while the body is still upright, the legs would swing down into the ground.
        var lying = Figures.Lying;
        Assert.True(lying.LegsFrom >= 0.5);
        Assert.Equal(0, LyingPose.Legs(lying, 0));
        Assert.Equal(0, LyingPose.Legs(lying, lying.LegsFrom));
        Assert.Equal(0.5, LyingPose.Legs(lying, (1 + lying.LegsFrom) / 2), 9);
        Assert.Equal(1, LyingPose.Legs(lying, 1));
    }

    [Fact]
    public void OnTheWayDownTheBodyIsLiftedAsFarAsItsLowestPointNeeds()
    {
        // Seated on the ground as the seated clip has it: hips and feet reach into the ground.
        BodyPoint[] seated = [new(0, 0.4, -0.3, 0.15), new(0.2, 0.33, 0.4, 0.05), new(0, 1.0, -0.2, 0.1)];
        Assert.Equal(0.15, LyingPose.Lift(seated, 5, 1.6, 5, (_, _) => 2.0), 9);

        // On a slope the ground is read under each point: here the feet are uphill.
        Assert.Equal(0.12 + (0.5 * 0.4), LyingPose.Lift(seated, 5, 1.6, 5, (_, z) => 2.0 + (0.5 * (z - 5))), 9);

        // Clear of the ground nothing is lifted, and it is never pulled down; no points, no lift.
        Assert.Equal(0, LyingPose.Lift(seated, 5, 1.9, 5, (_, _) => 2.0));
        Assert.Equal(0, LyingPose.Lift([], 5, 0, 5, (_, _) => 2.0));
    }

    [Fact]
    public void OnFlatGroundTheLyingBodyIsLevelAndRestsOnTheGround()
    {
        var lying = Figures.Lying;
        var fit = LyingPose.Fit(lying, Body, 10, 20, 1, (_, _) => 3.0);
        Assert.Equal(new Point3(0, 1, 0), fit.Up);

        // The lowest point (the shoulder underneath) touches the ground; nothing is below it.
        Assert.Equal(3.0 + 0.17 + 0.06, fit.HipY, 9);
        Assert.Equal(0, LyingPose.Clearance(fit, Body, 10, 20, (_, _) => 3.0), 9);

        // A figure not drawn yet has no points: its hips lie lift_m above the ground.
        var bare = LyingPose.Fit(lying, [], 10, 20, 2, (_, _) => 3.0);
        Assert.Equal((new Point3(0, 1, 0), 3.0 + (2 * lying.LiftM)), (bare.Up, bare.HipY));
    }

    [Theory]
    [InlineData(0.2, 0.0)]
    [InlineData(0.0, -0.3)]
    [InlineData(0.15, 0.25)]
    [InlineData(-0.1, 0.05)]
    public void OnASlopeTheBodyFollowsTheGroundAndNothingIsBelowIt(double slopeX, double slopeZ)
    {
        var lying = Figures.Lying;
        double Ground(double x, double z) => 5 + (slopeX * x) + (slopeZ * z);
        var fit = LyingPose.Fit(lying, Body, 10, 20, 1, Ground);

        // The body's up axis is the ground normal (the slope is inside the data's limit).
        var length = Math.Sqrt((slopeX * slopeX) + 1 + (slopeZ * slopeZ));
        Assert.Equal(-slopeX / length, fit.Up.X, 6);
        Assert.Equal(1 / length, fit.Up.Y, 6);
        Assert.Equal(-slopeZ / length, fit.Up.Z, 6);

        // It rests on the slope: the lowest point touches, none is below; level it would sink in or float.
        var clearance = LyingPose.Clearance(fit, Body, 10, 20, Ground);
        Assert.InRange(clearance, 0, 1e-6);
        var gaps = Body.Select(p => LyingPose.Tilt(fit.Up, new Point3(p.X, p.Y, p.Z)))
            .Zip(Body, (p, b) => fit.HipY + p.Y - b.RadiusM - Ground(10 + p.X, 20 + p.Z)).ToList();
        Assert.True(gaps.Max() < 0.27, $"a part floats {gaps.Max():0.00} m above the slope");
    }

    [Fact]
    public void OnGroundSteeperThanTheLimitItTiltsToTheLimitAndIsLifted()
    {
        var lying = Figures.Lying;
        static double Ground(double x, double z) => 5 + (1.2 * x) - (0.4 * z);
        var fit = LyingPose.Fit(lying, Body, 10, 20, 1, Ground);
        Assert.Equal(lying.MaxTiltDeg, Math.Acos(fit.Up.Y) * 180 / Math.PI, 6);

        // Tilted towards the slope (downhill is -X and +Z), and still nowhere below the ground.
        Assert.True(fit.Up.X < 0 && fit.Up.Z > 0);
        Assert.InRange(LyingPose.Clearance(fit, Body, 10, 20, Ground), 0, 1e-6);
    }

    [Fact]
    public void OverASmallBumpTheBodyIsLiftedSoNothingIsBelowTheGround()
    {
        var lying = Figures.Lying;

        // A bump of 15 cm under the chest, a dip under the feet.
        static double Ground(double x, double z)
        {
            var bump = Math.Max(0, 1 - (Math.Sqrt(((x - 10.45) * (x - 10.45)) + ((z - 20) * (z - 20))) / 0.3)) * 0.15;
            var dip = Math.Max(0, 1 - (Math.Sqrt(((x - 9.5) * (x - 9.5)) + ((z - 20.15) * (z - 20.15))) / 0.3)) * 0.1;
            return 2 + bump - dip;
        }

        var fit = LyingPose.Fit(lying, Body, 10, 20, 1, Ground);
        Assert.InRange(LyingPose.Clearance(fit, Body, 10, 20, Ground), 0, 1e-6);
        Assert.InRange(Math.Acos(fit.Up.Y) * 180 / Math.PI, 0, lying.MaxTiltDeg + 1e-6);

        // Whatever the ground and the facing, no sampled point is ever below it.
        foreach (var turn in Enumerable.Range(0, 12).Select(i => i * Math.PI / 6))
        {
            var turned = Body.Select(p => new BodyPoint((p.X * Math.Cos(turn)) + (p.Z * Math.Sin(turn)), p.Y, (p.Z * Math.Cos(turn)) - (p.X * Math.Sin(turn)), p.RadiusM)).ToList();
            double Rough(double x, double z) => (0.2 * Math.Sin(x * 3)) + (0.15 * Math.Cos(z * 4)) + (0.3 * x);
            var rough = LyingPose.Fit(lying, turned, 3, 4, 1, Rough);
            Assert.True(LyingPose.Clearance(rough, turned, 3, 4, Rough) >= -1e-9, $"below the ground at turn {turn:0.00}");
            Assert.True(Math.Acos(rough.Up.Y) * 180 / Math.PI <= lying.MaxTiltDeg + 1e-6);
        }
    }

    [Fact]
    public void TheTiltIsTheShortestTurnFromStraightUp()
    {
        var up = new Point3(-0.6, 0.8, 0);
        var tilted = LyingPose.Tilt(up, new Point3(0, 1, 0));
        Assert.Equal((up.X, up.Y, up.Z), (Math.Round(tilted.X, 9), Math.Round(tilted.Y, 9), Math.Round(tilted.Z, 9)));

        // The axis of the turn stays where it is, lengths are kept.
        var axis = LyingPose.Tilt(up, new Point3(0, 0, 2));
        Assert.Equal((0.0, 0.0, 2.0), (Math.Round(axis.X, 9), Math.Round(axis.Y, 9), Math.Round(axis.Z, 9)));
        var along = LyingPose.Tilt(up, new Point3(1, 0, 0));
        Assert.Equal((0.8, 0.6, 0.0), (Math.Round(along.X, 9), Math.Round(along.Y, 9), Math.Round(along.Z, 9)));
        Assert.Equal(new Point3(1, 2, 3), LyingPose.Tilt(new Point3(0, 1, 0), new Point3(1, 2, 3)));
    }

    [Fact]
    public void OnlyAnUprightFigureTurnsItsBody()
    {
        // Standing it may turn to a voice or to another inhabitant.
        Assert.True(LyingPose.MayTurn(lying: false, lieSeconds: 0, seated: false, seatShare: 0));

        // Lying, lying down, getting up, sitting or still on the way out of a seat it does not swivel.
        Assert.False(LyingPose.MayTurn(lying: true, lieSeconds: 2.9, seated: false, seatShare: 0));
        Assert.False(LyingPose.MayTurn(lying: true, lieSeconds: 0, seated: false, seatShare: 0));
        Assert.False(LyingPose.MayTurn(lying: false, lieSeconds: 0.4, seated: false, seatShare: 0));
        Assert.False(LyingPose.MayTurn(lying: false, lieSeconds: 0, seated: true, seatShare: 1));
        Assert.False(LyingPose.MayTurn(lying: false, lieSeconds: 0, seated: false, seatShare: 0.3));
    }

    [Fact]
    public void LyingIsAPoseOfItsOwnForTheCadence()
    {
        var library = Read<AnimationLibrary>("animation", "library.json");
        var lying = Figures.Lying;
        var chooser = new AnimationChooser(library);
        var values = new Dictionary<string, double>();
        var sleep = chooser.ForInhabitant("sleep", false, "inh-000001", 0, values);
        var stand = chooser.IdleById("inh-000001", "stand");
        Assert.NotEqual(PoseCadence.PoseKey(stand, lying), PoseCadence.PoseKey(sleep, lying));

        // The lying clip is a seated loop: lying on the side is still a pose of its own, not sitting.
        var sit = new FigureClip(chooser.ForClip(Figures.Sitting.SeatedClips.First()), Walking: false, null);
        Assert.NotEqual(PoseCadence.PoseKey(sit, lying), PoseCadence.PoseKey(sleep, lying));
    }
}
