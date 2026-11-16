using System.Text.Json;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1786 (Marcel, 15a; B2831): frustration stayed high for good. Feeling cold alone drives it slower than it returns, so it
/// stays below the storm cloud; cold together with a neglected need lifts it above; once warm and fed it falls back.
/// </summary>
public sealed class FrustrationTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void ColdAloneStaysBelowTheCloudColdAndHungryRiseAboveAndWarmItFallsBack()
    {
        var cloud = CloudThreshold();
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        var cold = true;
        var hungry = false;
        host.Inhabitants.Feels = (_, sensation) => cold && sensation == "too_cold";
        var ticksPerHour = host.World.Calendar.Settings.TicksPerHour;

        double Run(int hours)
        {
            var most = 0.0;
            for (var t = 0; t < hours * ticksPerHour; t++)
            {
                foreach (var inhabitant in host.Inhabitants.All)
                {
                    // Only the drivers under test: every other need stays met.
                    foreach (var need in inhabitant.Needs.Keys.ToList())
                    {
                        inhabitant.Needs[need] = need == "hunger" && hungry ? 0.1 : 1.0;
                    }
                }

                host.World.Step();
                most = Math.Max(most, host.Inhabitants.All.Max(i => i.Moods.GetValueOrDefault("frustration")));
            }

            return most;
        }

        Assert.True(Run(72) < cloud, "cold alone stays below the cloud");

        hungry = true;
        Assert.True(Run(72) > cloud, "cold and hungry rise above it");

        (cold, hungry) = (false, false);
        Run(24);
        Assert.All(host.Inhabitants.All, i => Assert.True(i.Moods.GetValueOrDefault("frustration") < cloud / 2, "warm and fed it falls back"));
    }

    /// <summary>The frustration level from which the storm cloud shows (content/ui/emotion_symbols.json).</summary>
    private static double CloudThreshold()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContentDirectory, "ui", "emotion_symbols.json")));
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in property.Value.EnumerateArray())
            {
                if (entry.TryGetProperty("mood", out var mood) && mood.GetString() == "frustration" && entry.TryGetProperty("threshold", out var threshold))
                {
                    return threshold.GetDouble();
                }
            }
        }

        throw new InvalidOperationException("no frustration symbol");
    }
}
