using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>A1564 (B4b/B7 R7): every new invention gets a picture job; the picture is kept, a failure retried at night.</summary>
public sealed partial class BuildingTests
{
    private static ImageGeneration ImageData => ContractSerializer.Deserialize<ImageGeneration>(File.ReadAllText(Path.Combine(EagerContent.Value, "image", "generation.json")));

    [Fact]
    public void ANewInventionAsksForItsIconAndKeepsThePicture()
    {
        var (host, port, typeId) = Invented(34);
        var job = Assert.Single(port.Submitted, j => j.Kind == JobKind.Image);
        var image = job.Payload.Image!;
        var data = ImageData;
        Assert.Equal((ImagePayloadPurpose.Icon, data.InventionIcons!.Style, data.Sizes.Icon.Width, data.Sizes.Icon.Height, typeId), (image.Purpose, image.Style, image.Width, image.Height, image.ObjectType));
        Assert.Equal(data.Priority, job.Priority);
        var invention = host.Inventions.Invention(typeId)!;
        Assert.StartsWith(invention.Name.En, image.Subject, StringComparison.Ordinal);
        Assert.Contains("made of", image.Subject, StringComparison.Ordinal);
        Assert.Null(host.Inventions.ImageOf(typeId));

        var before = host.World.StateHash();
        var status = ContractSerializer.Deserialize<JobStatus>(File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.image-vessel.json")));
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        port.Answer(job, status);
        host.World.Step();
        var picture = host.Inventions.ImageOf(typeId);
        Assert.NotNull(picture);
        Assert.Equal(status.Result!.Image!.File, picture.File);
        Assert.NotEqual(before, host.World.StateHash());

        // Saved and loaded: the picture stays, the world hashes the same.
        var path = Path.Combine(_directory, "image.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(path, host);
        Assert.Equal(picture.File, loaded.Inventions.ImageOf(typeId)!.File);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void ARetryableImageFailureIsTriedAgainEachComputeNightUpToTheLimit()
    {
        var (host, port, typeId) = Invented(35);
        var limit = ImageData.InventionIcons!.MaxAttempts;
        var night = Content.Value.Inventions.Build.NightPhases;
        for (var attempt = 1; attempt <= limit; attempt++)
        {
            var job = port.Submitted.Last(j => j.Kind == JobKind.Image);
            Assert.Equal(attempt, port.Submitted.Count(j => j.Kind == JobKind.Image));
            var error = new ServiceError { ContractVersion = ContractInfo.Version, RequestId = job.RequestId, Tick = job.Tick, Code = ServiceErrorCode.Model_unavailable, Message = "image model busy", Retryable = true };
            port.AnswerError(job.RequestId, ContractSerializer.Serialize(error));
            host.World.Step();
            if (attempt == limit)
            {
                break;
            }

            Assert.True(host.Inventions.Images.Single().RetryAtNight);
            RunUntil(host, () => port.Submitted.Count(j => j.Kind == JobKind.Image) == attempt + 1, (int)host.World.Calendar.TicksPerDay * 2);
            var retry = port.Submitted.Last(j => j.Kind == JobKind.Image);
            var phase = host.World.Calendar.At(retry.Tick).Phase.ToString().ToLowerInvariant();
            Assert.Contains(night, n => n == phase);
            Assert.NotEqual(job.RequestId, retry.RequestId);
        }

        var final = host.Inventions.Images.Single();
        Assert.True(final.Failed);
        Assert.Equal(limit, final.Attempts);
        host.World.Run(host.World.Calendar.TicksPerDay * 2);
        Assert.Equal(limit, port.Submitted.Count(j => j.Kind == JobKind.Image));
        Assert.Null(host.Inventions.ImageOf(typeId));
    }
}
