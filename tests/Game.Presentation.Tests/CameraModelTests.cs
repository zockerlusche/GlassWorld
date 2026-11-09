using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

public sealed class CameraModelTests
{
    private static readonly int[] FrameRates = [30, 60, 144];

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
        camera.Move(1, 0, 0, 1000);
        Settle(camera);
        Assert.False(camera.Following);
        var (x, z) = camera.Target;
        Assert.InRange(x, -data.BoundsMarginM - 1e-6, 192 + data.BoundsMarginM + 1e-6);
        Assert.InRange(z, -data.BoundsMarginM - 1e-6, 192 + data.BoundsMarginM + 1e-6);
        Assert.True(Math.Abs(x - 96) > 50 || Math.Abs(z - 96) > 50, "panning far must reach the border");
    }

    [Fact]
    public void FollowingCanKeepTheDistanceOfAShotPoint()
    {
        var data = Data();
        var camera = new CameraModel(data, 0, 0, 192, 192);
        var position = (X: 40d, Z: 150d);
        var distance = Math.Clamp(data.FollowDistanceM / 2, data.DistanceM.Min, data.DistanceM.Max);
        camera.Set(40, 150, 30, distance);
        camera.Follow(() => position, distance);
        position = (44, 152);
        Settle(camera);
        Assert.Equal((44, 152, distance), (Math.Round(camera.Target.X, 2), Math.Round(camera.Target.Z, 2), Math.Round(camera.Distance, 6)));
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

    [Fact]
    public void OrbitTurnsAllAroundAndThePitchStaysWithinItsRange()
    {
        var data = Data();
        var camera = new CameraModel(data, 0, 0, 192, 192);

        // Stepless and beyond a full turn: no snapping to the four directions.
        camera.Drag(1234, 0);
        Settle(camera);
        Assert.Equal(data.StartYawDeg + (1234 * data.RotateDegPerPx), camera.Yaw, 3);
        Assert.True(1234 * data.RotateDegPerPx > 360, "the test drag must go beyond a full turn");

        camera.Drag(0, 100000);
        Settle(camera);
        Assert.Equal(data.PitchRangeDeg.Max, camera.Pitch, 3);
        camera.Drag(0, -100000);
        Settle(camera);
        Assert.Equal(data.PitchRangeDeg.Min, camera.Pitch, 3);
        Assert.Equal(camera.TargetHeight + (camera.Distance * Math.Sin(data.PitchRangeDeg.Min * Math.PI / 180)), camera.Eye.Y, 6);

        // The range goes from flat to (nearly) top-down; the start view lies inside it.
        Assert.InRange(data.PitchDeg, data.PitchRangeDeg.Min, data.PitchRangeDeg.Max);
        Assert.True(data.PitchRangeDeg.Max >= 80, "the pitch must reach a top-down view");
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(false, -4)]
    [InlineData(true, 3)]
    public void ZoomTowardsTheCursorKeepsTheGroundPointUnderIt(bool orthographic, double steps)
    {
        var camera = new CameraModel(Data(), 0, 0, 192, 192) { OrthographicOverride = orthographic };
        camera.Set(80, 100, 35, 60);
        const double x = 0.45, y = -0.3, aspect = 16 / 9d;
        var before = camera.GroundAt(x, y, aspect) ?? throw new InvalidOperationException("no ground under the cursor");
        camera.ZoomAt(steps, x, y, aspect);
        for (var i = 0; i < 600; i++)
        {
            // Not only at the end: the point stays put while the zoom glides.
            camera.Update(1 / 60d);
            var now = camera.GroundAt(x, y, aspect)!.Value;
            Assert.True(Math.Abs(now.X - before.X) < 1e-6 && Math.Abs(now.Z - before.Z) < 1e-6, $"frame {i}: {now} moved away from {before}");
        }

        Assert.NotEqual(60, camera.Distance, 1);
    }

    [Fact]
    public void ZoomWhileFollowingStaysOnTheFigure()
    {
        var camera = new CameraModel(Data(), 0, 0, 192, 192);
        camera.Follow(() => (50, 60));
        Settle(camera);
        camera.ZoomAt(3, 0.8, 0.8, 16 / 9d);
        Settle(camera);
        Assert.Equal(50, camera.Target.X, 3);
        Assert.Equal(60, camera.Target.Z, 3);
        Assert.True(camera.Following);
    }

    [Fact]
    public void GrabbingTheGroundMovesThePointUnderTheCursorWithIt()
    {
        var camera = new CameraModel(Data(), 0, 0, 192, 192);
        camera.Set(96, 96, 20, 50);
        camera.Follow(() => (96, 96), 50);
        const double aspect = 16 / 9d;
        var grabbed = camera.GroundAt(-0.2, 0.1, aspect)!.Value;
        Assert.True(camera.Grab((-0.2, 0.1), (0.3, -0.25), aspect));
        var under = camera.GroundAt(0.3, -0.25, aspect)!.Value;
        Assert.Equal(grabbed.X, under.X, 6);
        Assert.Equal(grabbed.Z, under.Z, 6);
        Assert.False(camera.Following);

        // Above the horizon there is no ground to grab.
        camera.Drag(0, -100000);
        Settle(camera);
        Assert.False(camera.Grab((0, 0.99), (0, 0.5), aspect));
    }

    [Fact]
    public void KeyMovesEaseInAndOutWithoutOvershoot()
    {
        var data = Data();
        var camera = new CameraModel(data, 0, 0, 1000, 1000);
        const double dt = 1 / 60d;
        var speeds = new List<double>();
        var last = camera.Target.Z;

        // Forward at yaw 0 is towards -z.
        for (var i = 0; i < 180; i++)
        {
            camera.Move(0, 1, 0, dt);
            speeds.Add((last - camera.Target.Z) / dt);
            last = camera.Target.Z;
        }

        Assert.True(speeds[0] < data.PanSpeedMS * 0.2, $"no jump to full speed (first frame {speeds[0]:0.00} m/s)");
        Assert.All(speeds.Zip(speeds.Skip(1)), p => Assert.True(p.Second >= p.First - 1e-9, "speeding up is steady"));
        Assert.All(speeds, v => Assert.True(v <= data.PanSpeedMS + 1e-9, $"overshoot {v} m/s"));
        Assert.Equal(data.PanSpeedMS, speeds[^1], 1);

        var glide = new List<double>();
        for (var i = 0; i < 180; i++)
        {
            camera.Move(0, 0, 0, dt);
            glide.Add((last - camera.Target.Z) / dt);
            last = camera.Target.Z;
        }

        Assert.True(glide[0] > data.PanSpeedMS * 0.5, "letting go glides out instead of stopping dead");
        Assert.All(glide.Zip(glide.Skip(1)), p => Assert.True(p.Second <= p.First + 1e-9, "slowing down is steady"));
        Assert.All(glide, v => Assert.True(v >= 0, "the camera never moves back"));
        Assert.Equal(0, glide[^1]);
        Assert.False(camera.KeyMoving);
    }

    [Fact]
    public void TheTurnKeysEaseAsWell()
    {
        var data = Data();
        var camera = new CameraModel(data, 0, 0, 192, 192);
        var yaws = new List<double> { camera.Yaw };
        for (var i = 0; i < 120; i++)
        {
            camera.Move(0, 0, 1, 1 / 60d);
            camera.Update(1 / 60d);
            yaws.Add(camera.Yaw);
        }

        var steps = yaws.Zip(yaws.Skip(1), (a, b) => b - a).ToList();
        Assert.True(steps[0] < data.KeyTurnDegS / 60 * 0.2, "no jump to full turning speed");
        Assert.All(steps.Zip(steps.Skip(1)), p => Assert.True(p.Second >= p.First - 1e-9, "turning speeds up steadily"));
        Assert.Equal(data.KeyTurnDegS / 60, steps[^1], 2);
    }

    [Fact]
    public void KeyMovesGiveTheSameViewAt30_60And144Fps()
    {
        var views = FrameRates.Select(fps =>
        {
            var camera = new CameraModel(Data(), 0, 0, 1000, 1000);
            camera.Drag(150, 40);
            camera.ZoomAt(2, 0.3, -0.2, 16 / 9d);
            for (var i = 0; i < 4 * fps; i++)
            {
                camera.Update(1d / fps);
            }

            var dt = 1d / fps;
            for (var i = 0; i < fps; i++)
            {
                camera.Move(1, 1, 0.5, dt);
                camera.Update(dt);
            }

            for (var i = 0; i < 2 * fps; i++)
            {
                camera.Move(0, 0, 0, dt);
                camera.Update(dt);
            }

            return (camera.Target.X, camera.Target.Z, camera.Yaw, camera.Pitch, camera.Distance);
        }).ToList();

        foreach (var view in views.Skip(1))
        {
            // Within 1 cm after tens of metres of travel while turning at the same time.
            Assert.InRange(view.X - views[0].X, -0.01, 0.01);
            Assert.InRange(view.Z - views[0].Z, -0.01, 0.01);
            Assert.Equal(views[0].Yaw, view.Yaw, 2);
            Assert.Equal(views[0].Pitch, view.Pitch, 6);
            Assert.Equal(views[0].Distance, view.Distance, 6);
        }

        Assert.True(Math.Abs(views[0].X - 500) > 10, "the keys must have moved the camera");
    }

    [Fact]
    public void ResetGlidesBackToTheStartViewTheShortWayRound()
    {
        var data = Data();
        var camera = new CameraModel(data, 0, 0, 192, 192);
        camera.Drag(1100, 300);
        camera.Zoom(5);
        camera.Move(1, 1, 0, 2);
        Settle(camera);
        var yaw = camera.Yaw;
        camera.Reset();
        camera.Update(1 / 60d);
        Assert.NotEqual(data.PitchDeg, camera.Pitch, 3);
        Settle(camera);
        Assert.Equal((96, 96), (Math.Round(camera.Target.X, 3), Math.Round(camera.Target.Z, 3)));
        Assert.Equal(data.PitchDeg, camera.Pitch, 3);
        Assert.Equal(data.DistanceM.Start, camera.Distance, 3);
        Assert.Equal(1, Math.Cos((camera.Yaw - data.StartYawDeg) * Math.PI / 180), 6);
        Assert.True(Math.Abs(camera.Yaw - yaw) <= 180 + 1e-6, "reset turns at most half way round");
    }

    [Theory]
    [InlineData(0, 0, 100, true)]
    [InlineData(5, 3, 300, true)]
    [InlineData(12, 0, 100, false)]
    [InlineData(0, 0, 900, true)]
    [InlineData(2, 1, 900, false)]
    public void AShortPressIsAClickAndAMoveIsADrag(double dx, double dy, double ms, bool click)
    {
        var data = Data();
        var gate = new ClickGate(data.ClickMaxPx, data.ClickMaxMs);
        gate.Press(400, 300, 1000);
        gate.Move(400 + dx, 300 + dy);
        Assert.Equal(Math.Sqrt((dx * dx) + (dy * dy)) > data.ClickMaxPx, gate.Dragging);
        Assert.Equal(click, gate.Release(400 + dx, 300 + dy, 1000 + ms));
        Assert.False(gate.Pressed);
    }

    [Fact]
    public void ADragStaysADragEvenWhenItComesBack()
    {
        var gate = new ClickGate(6, 450);
        gate.Press(0, 0, 0);
        Assert.False(gate.Move(3, 0));
        Assert.True(gate.Move(40, 0));
        Assert.True(gate.Move(0, 0));
        Assert.False(gate.Release(0, 0, 100));

        // Without a press there is no click.
        Assert.False(gate.Release(0, 0, 100));
    }

    [Fact]
    public void AFollowedFigureStaysFollowedUntilThePlayerMovesTheCameraOrLetsGo()
    {
        var camera = new CameraModel(Data(), 0, 0, 192, 192);
        var position = (X: 30d, Z: 40d);
        void Again()
        {
            camera.Follow(() => position);
            Settle(camera);
            Assert.True(camera.Following);
        }

        // Looking around (orbit, tilt, zoom, turn keys) keeps following; the figure walks on and the camera with it.
        Again();
        camera.Drag(200, 40);
        camera.Zoom(2);
        camera.ZoomAt(-1, 0.5, 0.5, 16 / 9d);
        for (var i = 0; i < 60; i++)
        {
            camera.Move(0, 0, 1, 1 / 60d);
            camera.Update(1 / 60d);
        }

        position = (70, 90);
        Settle(camera);
        Assert.True(camera.Following);
        Assert.Equal(70, camera.Target.X, 3);
        Assert.Equal(90, camera.Target.Z, 3);

        // Panning with the keys, grabbing the ground or Esc (StopFollowing) lets go.
        camera.Move(0, 1, 0, 1 / 60d);
        Assert.False(camera.Following);
        Again();
        Assert.True(camera.Grab((0, 0), (0.1, 0), 16 / 9d));
        Assert.False(camera.Following);
        Again();
        camera.StopFollowing();
        position = (10, 10);
        Settle(camera);
        Assert.False(camera.Following);
        Assert.Equal(70, camera.Target.X, 3);
    }

    private static void Settle(CameraModel camera)
    {
        for (var i = 0; i < 2000; i++)
        {
            camera.Update(1 / 60d);
        }
    }
}
