using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>W1695: a sleeping inhabitant lies on its back on the ground, never stands.</summary>
public sealed class LyingPoseTests
{
    private static T Read<T>(params string[] path)
        where T : class =>
        ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    [Fact]
    public void SleepPlaysAnApprovedClipLyingAndNoIdleBehaviour()
    {
        var library = Read<AnimationLibrary>("animation", "library.json");
        var lying = Read<LookFigures>("look", "figures.json").Lying;
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
        Assert.Equal(-90, lying.PitchDeg);
    }

    [Fact]
    public void TheBodyRestsOnTheGroundUnderItsHips()
    {
        var lying = Read<LookFigures>("look", "figures.json").Lying;

        // Facing +Z (yaw 0) it lies back towards -Z; facing +X (yaw 90°) towards -X.
        var (x, z) = LyingPose.Hips(lying, 10, 10, 0, 1);
        Assert.Equal((10.0, 10 - lying.HipM), (Math.Round(x, 9), Math.Round(z, 9)));
        (x, z) = LyingPose.Hips(lying, 10, 10, Math.PI / 2, 2);
        Assert.Equal((10 - (2 * lying.HipM), 10.0), (Math.Round(x, 9), Math.Round(z, 9)));

        // Standing on the feet's ground, lying on the hips' ground plus half the body's depth; in between eased.
        Assert.Equal(1.0, LyingPose.Height(lying, 1.0, 1.4, 1, 0));
        Assert.Equal(1.4 + lying.LiftM, LyingPose.Height(lying, 1.0, 1.4, 1, 1), 9);
        Assert.InRange(LyingPose.Height(lying, 1.0, 1.4, 1, 0.5), 1.0, 1.4 + lying.LiftM);
        Assert.Equal(-Math.PI / 2, LyingPose.Pitch(lying, 1), 9);
        Assert.Equal(0, LyingPose.Pitch(lying, 0));
    }

    [Fact]
    public void LyingIsAPoseOfItsOwnForTheCadence()
    {
        var library = Read<AnimationLibrary>("animation", "library.json");
        var lying = Read<LookFigures>("look", "figures.json").Lying;
        var chooser = new AnimationChooser(library);
        var values = new Dictionary<string, double>();
        var sleep = chooser.ForInhabitant("sleep", false, "inh-000001", 0, values);
        var stand = chooser.IdleById("inh-000001", "stand");
        Assert.NotEqual(PoseCadence.PoseKey(stand, lying), PoseCadence.PoseKey(sleep, lying));
    }
}
