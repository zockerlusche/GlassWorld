using System.Text.RegularExpressions;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B5 R2 (spec Bogen 30 v2, step B5-1): no request the core sends names a season - no season id of the
/// calendar as a value and no <c>time.season.*</c> key. The inhabitants feel the season only through its signs
/// (weather, temperature, comfort, the state of things). The prompt side is GW2's test (#319); the whole
/// year follows in the acceptance run (B5-9, acceptance 9).
/// </summary>
public sealed class NoSeasonInRequestsTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void NoDecisionRequestOfAShortRunNamesASeason()
    {
        var port = new RecordingPort();
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, port, "de", "start_island");
        host.World.Run(host.World.Calendar.TicksPerDay * 2);

        Assert.NotEmpty(port.Requests);
        var seasons = ContentSet.Load(ContentDirectory).Calendar.Settings.Seasons.Select(s => s.Id).ToList();
        Assert.NotEmpty(seasons);

        // A season id as a whole JSON string value or object key ("spring"), never inside another id such as
        // "spring_water", and no translation key of the season names.
        var pattern = new Regex(
            "\"(" + string.Join('|', seasons.Select(Regex.Escape)) + ")\"|time\\.season\\.",
            RegexOptions.CultureInvariant);
        var leaks = port.Requests.Where(r => pattern.IsMatch(r)).Select(r => pattern.Match(r).Value).Distinct().ToList();
        Assert.True(leaks.Count == 0, $"{port.Requests.Count} requests, season found: {string.Join(", ", leaks)}");
    }

    private sealed class RecordingPort : IDecisionPort
    {
        private readonly MockDecisionPort _mock = new();

        public List<string> Requests { get; } = [];

        public void Send(string requestId, string requestJson)
        {
            Requests.Add(requestJson);
            _mock.Send(requestId, requestJson);
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => _mock.Receive(tick);
    }
}
