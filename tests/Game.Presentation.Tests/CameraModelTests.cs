using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

public sealed class CameraModelTests
{
    private static LookCamera Data() =>
        ContractSerializer.Deserialize<LookCamera>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "camera.json")));

    [Fact]
    public void StartsAtTheCentreWithTheDataValues()
    {
        var data = Data();
        var camera = new CameraModel(data, 0, 0, 192, 192);
        Assert.Equal((96d, 96d), camera.Target);
        Assert.Equal(data.DistanceM.Start, camera.Distance);
        Assert.Equal(data.StartYawDeg, camera.Yaw);
        var eye = camera.Eye;
        Assert.Equal(data.DistanceM.Start * Math.Sin(data.PitchDeg * Math.PI / 180), eye.Y, 6);
    }

    [Fact]
    public void ZoomStaysInsideTheDistanceRangeAndEases()
    {
        var data = Data();
        var camera = new CameraModel(data, 0, 0, 192, 192);
        camera.Zoom(-1000);
        Settle(camera);
        Assert.Equal(data.DistanceM.Max, camera.Distance, 3);
        camera.Zoom(1000);
        camera.Update(1 / 60d);
        Assert.True(camera.Distance > data.DistanceM.Min, "zoom must ease, not jump");
        Settle(camera);
        Assert.Equal(data.DistanceM.Min, camera.Distance, 3);
    }

    [Fact]
    public void PanningIsBoundedByTheAreaPlusMarginAndLeavesFollowMode()
    {
        var data = Data();
        var camera = new CameraModel(data, 0, 0, 192, 192);
        camera.Follow(() => (10, 10));
        camera.Pan(1, 0, 1000);
        Settle(camera);
        Assert.False(camera.Following);
        var (x, z) = camera.Target;
        Assert.InRange(x, -data.BoundsMarginM - 1e-6, 192 + data.BoundsMarginM + 1e-6);
        Assert.InRange(z, -data.BoundsMarginM - 1e-6, 192 + data.BoundsMarginM + 1e-6);
        Assert.True(Math.Abs(x - 96) > 50 || Math.Abs(z - 96) > 50, "panning far must reach the border");
    }

    [Fact]
    public void FollowingTracksTheFigureAtTheFollowDistance()
    {
        var data = Data();
        var camera = new CameraModel(data, 0, 0, 192, 192);
        var position = (X: 40d, Z: 150d);
        camera.Follow(() => position);
        Settle(camera);
        Assert.Equal(40, camera.Target.X, 2);
        Assert.Equal(150, camera.Target.Z, 2);
        Assert.Equal(Math.Clamp(data.FollowDistanceM, data.DistanceM.Min, data.DistanceM.Max), camera.Distance, 2);
        position = (60, 140);
        Settle(camera);
        Assert.Equal(60, camera.Target.X, 2);
        camera.StopFollowing();
        position = (0, 0);
        Settle(camera);
        Assert.Equal(60, camera.Target.X, 2);
    }

    [Fact]
    public void TheSameInputsGiveTheSameViewAtAnyFrameRate()
    {
        var a = new CameraModel(Data(), 0, 0, 192, 192);
        var b = new CameraModel(Data(), 0, 0, 192, 192);
        a.Drag(200);
        b.Drag(200);
        for (var i = 0; i < 300; i++)
        {
            a.Update(1 / 60d);
        }

        for (var i = 0; i < 600; i++)
        {
            b.Update(1 / 120d);
        }

        Assert.Equal(a.Yaw, b.Yaw, 6);
        Assert.Equal(Data().StartYawDeg + (200 * Data().RotateDegPerPx), a.Yaw, 3);
    }

    [Fact]
    public void SetJumpsWithoutEasing()
    {
        var camera = new CameraModel(Data(), 0, 0, 192, 192);
        camera.Set(20, 30, 45, 60);
        Assert.Equal((20d, 30d), camera.Target);
        Assert.Equal(45, camera.Yaw);
        Assert.Equal(60, camera.Distance);
    }

    [Fact]
    public void TheTargetHeightEasesOverAStepInTheGround()
    {
        // A cliff of 5 m at x = 100: following a figure across it glides up instead of jumping.
        var data = Data();
        var camera = new CameraModel(data, 0, 0, 192, 192, (x, _) => x < 100 ? 0 : 5);
        Assert.Equal(0, camera.TargetHeight);
        camera.Follow(() => (150, 96));
        var heights = new List<double>();
        for (var i = 0; i < 2000; i++)
        {
            camera.Update(1 / 60d);
            heights.Add(camera.TargetHeight);
        }

        Assert.Equal(5, camera.TargetHeight, 3);
        Assert.Equal(camera.TargetHeight + (camera.Distance * Math.Sin(data.PitchDeg * Math.PI / 180)), camera.Eye.Y, 6);
        var largestStep = heights.Zip(heights.Skip(1), (a, b) => Math.Abs(b - a)).Prepend(heights[0]).Max();
        Assert.True(largestStep < 1, $"the height must ease, not jump (largest step {largestStep:0.00} m per frame)");
        Assert.All(heights.Zip(heights.Skip(1)), p => Assert.True(p.Second >= p.First - 1e-9, "the height rises steadily"));
    }

    [Fact]
    public void AFixedViewTakesTheGroundHeightAtOnce()
    {
        var camera = new CameraModel(Data(), 0, 0, 192, 192, (x, z) => (x + z) / 100);
        camera.Set(40, 60, 0, 30);
        Assert.Equal(1, camera.TargetHeight, 9);
    }

    private static void Settle(CameraModel camera)
    {
        for (var i = 0; i < 2000; i++)
        {
            camera.Update(1 / 60d);
        }
    }
}
