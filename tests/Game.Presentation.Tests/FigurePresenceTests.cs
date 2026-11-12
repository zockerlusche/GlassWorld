using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Tests;

/// <summary>Sitting on something, pause, the reaction to the player's voice and visible figures (W1688, W1689).</summary>
public sealed class FigurePresenceTests
{
    private static readonly LookFigures Figures =
        ContractSerializer.Deserialize<LookFigures>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "figures.json")));

    private static readonly AnimationLibrary Library =
        ContractSerializer.Deserialize<AnimationLibrary>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "animation", "library.json")));

    [Fact]
    public void TwoFiguresNeverShareOneSeat()
    {
        // A1570 (D843 b): the first takes the stone, the second the next free one or the ground.
        var sitting = Figures.Sitting;
        var book = new SeatBook();
        Seat stone = new(0.3, 0, sitting.Seats.First().SeatHeightM);
        Seat other = new(-0.5, 0, sitting.Seats.First().SeatHeightM);
        var first = SeatChoice.Choose(sitting, 0, 0, 1, [stone, other], s => book.TakenByOther("inh-000001", s));
        book.Sit("inh-000001", first);
        Assert.Equal((true, stone.X), (first.OnSeat, first.X));

        var second = SeatChoice.Choose(sitting, 0, 0, 1, [stone, other], s => book.TakenByOther("inh-000002", s));
        book.Sit("inh-000002", second);
        Assert.Equal((true, other.X), (second.OnSeat, second.X));

        var third = SeatChoice.Choose(sitting, 0, 0, 1, [stone, other], s => book.TakenByOther("inh-000003", s));
        Assert.False(third.OnSeat);

        // Its own seat stays its own; getting up or leaving frees it.
        Assert.Equal(stone.X, SeatChoice.Choose(sitting, 0, 0, 1, [stone, other], s => book.TakenByOther("inh-000001", s)).X);
        book.Sit("inh-000001", null);
        Assert.Equal(stone.X, SeatChoice.Choose(sitting, 0, 0, 1, [stone, other], s => book.TakenByOther("inh-000003", s)).X);
        book.Leave("inh-000002");
        Assert.False(book.TakenByOther("inh-000003", other));
    }

    [Fact]
    public void ChangingSeatGetsUpFirstAndNeverJumps()
    {
        // A1570 (D843 b): seated on one stone, told to sit on another, the figure eases up at the old place first.
        SeatPose a = new(true, 0, 0, -0.1, 0.2);
        SeatPose b = new(true, 0.9, 0, -0.1, 0.2);
        var (shown, share) = (a, 1.0);
        var lastX = shown.X * share;
        var reachedB = false;
        for (var frame = 0; frame < 600; frame++)
        {
            (shown, share) = SeatEasing.Step(b, shown, share, 2.0, 1.0 / 60);

            // While it is still (partly) seated it is seated at one place: it never slides along with the share.
            if (share > SeatEasing.Standing && !SeatEasing.SamePlace(shown, b))
            {
                Assert.True(SeatEasing.SamePlace(shown, a));
            }

            reachedB |= SeatEasing.SamePlace(shown, b) && share > 0.99;
            lastX = shown.X;
        }

        Assert.True(reachedB);
        Assert.Equal(b.X, lastX);

        // Sitting on the ground and nudged a few centimetres by the core, it stays seated (review GW3 #300).
        SeatPose ground = new(false, 3, 3, -0.4, 0.8);
        var (onGround, groundShare) = SeatEasing.Step(ground with { X = 3.05, Z = 2.97 }, ground, 1.0, 2.0, 0.1);
        Assert.Equal(1.0, groundShare);
        Assert.Equal(3.05, onGround.X);

        // Standing up keeps the place it got up from; sitting down again at the same place does not move it.
        (shown, share) = SeatEasing.Step(null, b, 1.0, 2.0, 0.1);
        Assert.True(SeatEasing.SamePlace(shown, b));
        Assert.True(share < 1.0);
    }

    [Fact]
    public void ASeatedClipSitsOnAStoneAtItsSeatHeight()
    {
        var sitting = Figures.Sitting;
        var stoneType = sitting.Seats.First().Types.First();
        var seats = SeatChoice.Seats(sitting, [new WorldObject("obj-1", stoneType, new Position(10.3, 5)), new WorldObject("obj-2", "apple", new Position(10, 5))]);
        var seat = Assert.Single(seats);
        Assert.Equal(sitting.Seats.First().SeatHeightM, seat.HeightM);

        var pose = SeatChoice.Choose(sitting, 10, 5, 1.0, seats);
        Assert.True(pose.OnSeat);
        Assert.Equal((10.3, 5.0), (pose.X, pose.Z));

        // The hips (clip seat height above the root) land exactly on the seat: root lift + clip seat = seat height.
        Assert.Equal(seat.HeightM, pose.LiftM + sitting.ClipSeatHeightM, 9);
        Assert.InRange(pose.KneeShare, 0, 1);
    }

    [Fact]
    public void WithoutASeatTheFigureSitsOnTheGroundNeverInTheAir()
    {
        var sitting = Figures.Sitting;
        var stone = new Seat(20, 20, 0.22);
        foreach (var scale in new[] { 0.7, 1.0 })
        {
            var pose = SeatChoice.Choose(sitting, 0, 0, scale, [stone]);
            Assert.False(pose.OnSeat);
            Assert.Equal((0.0, 0.0), (pose.X, pose.Z));

            // Hips on the terrain: the root goes down by the clip's seat height at this scale, legs straight.
            Assert.Equal(0, pose.LiftM + (sitting.ClipSeatHeightM * scale), 9);
            Assert.Equal(sitting.GroundKneeStraighten, pose.KneeShare);
        }
    }

    [Fact]
    public void TheNearestSeatWithinReachIsTaken()
    {
        var sitting = Figures.Sitting;
        var near = new Seat(0.3, 0, 0.2);
        var far = new Seat(0.6, 0, 0.3);
        Assert.Equal(near.X, SeatChoice.Choose(sitting, 0, 0, 1, [far, near]).X);
        Assert.False(SeatChoice.Choose(sitting, 0, 0, 1, [new Seat(sitting.SeatSearchM + 0.01, 0, 0.2)]).OnSeat);
    }

    [Fact]
    public void TheIdleSitAndTheSeatedClipsAreSeatedAndExist()
    {
        // The tired idle behaviour plays the seated loop (no stand-to-sit transition in the air).
        var idleSit = Library.Idle.Single(i => i.Id == "sit_down");
        Assert.True(SeatChoice.IsSeated(Figures.Sitting, idleSit.Clip));
        Assert.All(Figures.Sitting.SeatedClips, id => Assert.Contains(Library.Clips, c => c.Id == id && c.Source == AnimationClipSource.Asset));
        Assert.False(SeatChoice.IsSeated(Figures.Sitting, "stand"));
    }

    [Fact]
    public void PauseAndSpeedZeroFreezeAndResume()
    {
        var freeze = new PlaybackFreeze();
        Assert.False(freeze.Update(1));
        Assert.False(freeze.Frozen);
        Assert.True(freeze.Update(0));
        Assert.True(freeze.Frozen);
        Assert.False(freeze.Update(0));
        Assert.True(freeze.Update(4));
        Assert.False(freeze.Frozen);
        Assert.False(freeze.Update(16));
        Assert.True(freeze.Update(double.NaN));
        Assert.True(freeze.Frozen);
    }

    [Fact]
    public void ThePlayersVoiceIsHeardNearbyAndTheHearersTurnToIt()
    {
        var greeting = Figures.Greeting;
        var r = greeting.HearRadiusM;
        var hearers = GreetingReaction.Hearers(greeting, "inh-2", [("inh-1", 0, 0), ("inh-2", 1, 0), ("inh-3", 1 + r + 1, 0), ("inh-4", 1, r - 0.5)]);
        Assert.Equal(["inh-2", "inh-1", "inh-4"], hearers);
        Assert.Empty(GreetingReaction.Hearers(greeting, "inh-9", [("inh-1", 0, 0)]));

        // The voice is the camera: a figure at the origin turns to a camera straight in +x (yaw = atan2(x, z)).
        Assert.Equal(Math.PI / 2, GreetingReaction.YawTowards(0, 0, 10, 0), 9);
        Assert.Equal(0, GreetingReaction.YawTowards(0, 0, 0, 5), 9);
    }

    [Fact]
    public void AGreetingIsAnsweredVisiblyWithoutDucking()
    {
        var greeting = Figures.Greeting;
        var heard = GreetingReaction.For(greeting, "conversation_started", null, hasText: true)!.Value;
        Assert.True(heard.LookS > 0);
        Assert.Equal(0, heard.WaveS);

        // Marcel's test: reply "Hallo!" with the expression wave - the speaker looks, waves and talks.
        var answer = GreetingReaction.For(greeting, "said", "wave", hasText: true)!.Value;
        Assert.True(answer.LookS > 0 && answer.WaveS > 0 && answer.TalkS > 0);
        Assert.Equal(0, GreetingReaction.For(greeting, "gestured", "ponder", hasText: false)!.Value.WaveS);
        Assert.Null(GreetingReaction.For(greeting, "thought", null, hasText: true));
        Assert.Equal(greeting.TalkS, GreetingReaction.For(greeting, "not_understood", null, hasText: true)!.Value.TalkS); // W1701: asks back aloud
        Assert.Equal(0, GreetingReaction.For(greeting, "not_understood", null, hasText: false)!.Value.TalkS);
        var silent = GreetingReaction.For(greeting, "no_answer", null, hasText: true)!.Value;
        Assert.Equal((0.0, (string?)null), (silent.TalkS, silent.Expression)); // TL #21712: no fixed sentence, no mouth

        // TL #21712 (D763 Z26): an answer without words shows an expression - the model's, or the data's quiet default.
        Assert.Equal(greeting.DeclineExpression, GreetingReaction.For(greeting, "declined", null, hasText: false)!.Value.Expression);
        Assert.Equal(greeting.NotUnderstoodExpression, GreetingReaction.For(greeting, "not_understood", null, hasText: false)!.Value.Expression);
        Assert.Equal("ponder", GreetingReaction.For(greeting, "gestured", "ponder", hasText: false)!.Value.Expression);
        Assert.Null(GreetingReaction.For(greeting, "said", "wave", hasText: true)!.Value.Expression); // the wave has its own motion
        Assert.Null(GreetingReaction.For(greeting, "said", null, hasText: true)!.Value.Expression);
        Assert.Null(GreetingReaction.For(greeting, "no_answer", "ponder", hasText: false)!.Value.Expression); // no answer, no expression
        Assert.True(GreetingReaction.For(greeting, "declined", null, hasText: false)!.Value.ExpressS > 0);
        Assert.False(new AnimationChooser(Library).ForExpression(greeting.DeclineExpression!).FellBack);
        Assert.False(new AnimationChooser(Library).ForExpression(greeting.NotUnderstoodExpression!).FellBack);

        // Listening is a standing clip that exists, not the crouch or a placeholder.
        var chooser = new AnimationChooser(Library);
        var listen = chooser.ForClip(greeting.ListenClip);
        Assert.False(listen.FellBack);
        Assert.DoesNotContain("Crouch", listen.Asset, StringComparison.Ordinal);
        Assert.True(chooser.StandsIdle(null));
        Assert.True(chooser.StandsIdle("idle"));
        Assert.False(chooser.StandsIdle("eat"));
        Assert.Equal(0.5, GreetingReaction.Ease(0, 1, 2.5, 0.2), 9);
        Assert.Equal(1, GreetingReaction.Ease(0.9, 1, 2.5, 0.2), 9);
    }

    [Fact]
    public void TheAnswerShowsAsASpeechBubbleThatAThoughtDoesNotCover()
    {
        Assert.Equal(("Hallo!", 9.0), TalkBubble.For("said", "Hallo!", 9.0));
        Assert.Equal("…", TalkBubble.For("conversation_started", "Hallo", 9.0)!.Value.Text);
        Assert.Equal("?", TalkBubble.For("not_understood", null, 9.0)!.Value.Text);
        // W1701: asking back shows its sentence. No answer puts no fixed sentence in the mouth (TL #21712): only "…".
        Assert.Equal(("Feuer? Was ist das?", 9.0), TalkBubble.For("not_understood", "Feuer? Was ist das?", 9.0));
        Assert.Equal(("…", 2.0), TalkBubble.For("no_answer", "msg.converse.no_answer", 9.0)); // briefly: none is coming
        Assert.Equal(("…", 2.0), TalkBubble.For("no_answer", null, 9.0));
        Assert.Equal("…", TalkBubble.For("declined", null, 9.0)!.Value.Text);
        Assert.True(TalkBubble.IsSpeech("not_understood", "Feuer? Was ist das?"));
        Assert.False(TalkBubble.IsSpeech("no_answer", "msg.converse.no_answer"));
        Assert.False(TalkBubble.IsSpeech("not_understood", null));
        Assert.Null(TalkBubble.For("thought", "Ich habe Hunger.", 9.0));
        Assert.True(TalkBubble.IsSpeech("said", "Hallo!"));
        Assert.False(TalkBubble.IsSpeech("gestured", null));
    }

    [Fact]
    public void CrownsFadeOnlyNearTheLineBetweenCameraAndFigure()
    {
        var fade = Figures.CrownFade;
        var camera = new Point3(0, 20, -20);
        var figure = new Point3(0, 2, 0);
        var onLine = new Point3(0, 11, -10);
        Assert.Equal(fade.Alpha, CrownFade.Alpha(fade, onLine, camera, figure), 9);

        var aside = new Point3(fade.RadiusM / 2, 11, -10);
        var alpha = CrownFade.Alpha(fade, aside, camera, figure);
        Assert.InRange(alpha, fade.Alpha + 1e-6, 1 - 1e-6);

        Assert.Equal(1, CrownFade.Alpha(fade, new Point3(fade.RadiusM + 1, 11, -10), camera, figure));
        Assert.Equal(1, CrownFade.Alpha(fade, new Point3(0, 25, -30), camera, figure)); // behind the camera
        Assert.Equal(1, CrownFade.Alpha(fade, new Point3(0, -5, 10), camera, figure)); // behind the figure
        Assert.True(CrownFade.NearLine(fade, onLine, camera, figure));
        Assert.False(CrownFade.NearLine(fade, new Point3(50, 0, 0), camera, figure));
    }
}
