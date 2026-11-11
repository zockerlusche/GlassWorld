using System.Text.RegularExpressions;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// ADR-0009: what the core sends to the AI is validated against its schema in tests and check runs and not
/// in the running game; what comes back is validated in any case. These tests hold the switch, the
/// condition that every request form goes through it (and has its contract test), and the named outcome
/// of a request the service refused.
/// </summary>
public sealed partial class RequestValidationTests
{
    private const string Preset = "start_island";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void AHostValidatesItsRequestsUnlessItSaysOtherwise()
    {
        Assert.True(new SimulationHostOptions().ValidateRequests);
        Assert.True(new SimulationHostOptions(DebugCommands: true).ValidateRequests);

        var usual = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        Assert.True(usual.Options.ValidateRequests);
        Assert.True(usual.Decisions.ValidateRequests && usual.Inventions.ValidateRequests && usual.NightJobs.ValidateRequests && usual.Conversation.ValidateRequests);

        var game = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset, options: new SimulationHostOptions { ValidateRequests = false });
        Assert.False(game.Options.ValidateRequests);
        Assert.False(game.Decisions.ValidateRequests || game.Inventions.ValidateRequests || game.NightJobs.ValidateRequests || game.Conversation.ValidateRequests);
    }

    [Fact]
    public void TheSerializerValidatesOnlyWhenAsked()
    {
        // A request no schema would let through: no inhabitant, no options, an id that is none.
        var broken = new DecisionRequest { ContractVersion = ContractInfo.Version, RequestId = "not-an-id", Tick = -1 };
        Assert.Throws<ContractViolationException>(() => ContractSerializer.Serialize(broken));
        Assert.Throws<ContractViolationException>(() => ContractSerializer.Serialize(broken, validate: true));
        var json = ContractSerializer.Serialize(broken, validate: false);
        Assert.Contains("\"request_id\":\"not-an-id\"", json, StringComparison.Ordinal);
        Assert.NotEmpty(ContractValidator.Validate("decision-request", json));

        // What comes in is validated whatever the switch says: there is no way to read it unvalidated.
        Assert.Throws<ContractViolationException>(() => ContractSerializer.Deserialize<DecisionRequest>(json));
    }

    [Fact]
    public void TheSameRequestsLeaveAndTheSameWorldResultsWithAndWithoutValidation()
    {
        var checkedRun = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, MockDecisionPort.FromContent(ContentDirectory), "de", Preset, inhabitants: 12);
        var game = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, MockDecisionPort.FromContent(ContentDirectory), "de", Preset, options: new SimulationHostOptions { ValidateRequests = false }, inhabitants: 12);
        checkedRun.World.Run(400);
        game.World.Run(400);

        Assert.Equal(checkedRun.World.StateHash(), game.World.StateHash());
        var requests = game.Decisions.Log.Entries.Where(e => e.Kind == DecisionLogKind.Request).Select(e => e.Json).ToList();
        Assert.True(requests.Count > 20, $"{requests.Count} requests");
        Assert.Equal(checkedRun.Decisions.Log.Entries.Select(e => (e.Kind, e.RequestId, e.Tick, e.Json, e.Note)), game.Decisions.Log.Entries.Select(e => (e.Kind, e.RequestId, e.Tick, e.Json, e.Note)));

        // And what the game sent without looking is what the contract asks for.
        Assert.All(requests, json => Assert.Empty(ContractValidator.Validate("decision-request", json)));
        Assert.Equal(0, game.RejectedRequests);
    }

    /// <summary>
    /// The request forms of the core, each with the test that validates a really built request of that form
    /// against its schema. The condition of ADR-0009: whoever adds a request form adds its contract test and
    /// names both here - the scan below fails until the list fits the code.
    /// </summary>
    private static readonly (string File, int Sites, string ContractTest)[] RequestForms =
    [
        ("Decisions/DecisionsSystem.cs", 1, "DecisionTests.UnknownObjectBecomesAValidatedRequest (decision-request)"),
        ("Player/ConversationSystem.cs", 1, "ConversationTests (conversation-request)"),
        ("Night/NightJobsSystem.cs", 1, "NightJobsTests (generation-job: memory and question jobs)"),
        ("Inventions/InventionSystem.cs", 1, "InventionTests.ALastingLackBecomesAnInventionJobWithTheInhabitantsViewOfTheMaterials (generation-job: invention)"),
        ("Inventions/InventionSystem.Building.cs", 2, "BuildingTests: AnApprovedInventionAsksForAPlanWithTheConstructionKit (build plan), AFinishedInventionThatNeedsASoundGetsASoundJobAndKeepsTheFile (sound)"),
    ];

    [GeneratedRegex(@"ContractSerializer\.Serialize\((?<arguments>[^()]*(?:\([^()]*\)[^()]*)*)\)")]
    private static partial Regex SerializeCall();

    [Fact]
    public void EveryRequestTheCoreSendsGoesThroughTheSwitchAndIsListedWithItsContractTest()
    {
        var core = Path.Combine(TestData.Root, "src", "Simulation.Core");
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var name = Path.GetRelativePath(core, file).Replace('\\', '/');
            foreach (Match call in SerializeCall().Matches(File.ReadAllText(file)))
            {
                var arguments = call.Groups["arguments"].Value;
                if (arguments.EndsWith(", ValidateRequests", StringComparison.Ordinal))
                {
                    found[name] = found.GetValueOrDefault(name) + 1;
                }
                else
                {
                    // Without the switch only the core's own stand-ins for the AI serialize: the answers of
                    // the mock decider and of the mock conversation partner, which are validated as answers.
                    Assert.True(arguments == "response", $"{name}: ContractSerializer.Serialize({arguments}) sends something without the switch of ADR-0009");
                }
            }
        }

        Assert.Equal(RequestForms.ToDictionary(f => f.File, f => f.Sites), found);
        Assert.All(RequestForms, form => Assert.False(string.IsNullOrWhiteSpace(form.ContractTest)));
    }

    [Fact]
    public void ARequestTheServiceRefusesIsANamedAndCountedOutcome()
    {
        // The service refuses every request as invalid (code bad_request), as it would a request that broke the contract.
        var port = new AnsweringPort(request => Error(request, ServiceErrorCode.Bad_request, retryable: false));
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, port, "de", Preset, options: new SimulationHostOptions { ValidateRequests = false });
        var seen = host.Inhabitants.LastEventSeq;
        host.World.Run(30);

        var refused = host.Decisions.Log.Entries.Where(e => e.Kind == DecisionLogKind.Error).ToList();
        Assert.NotEmpty(refused);
        Assert.All(refused, e => Assert.Equal(ReplayNotes.RequestRejected, e.Note));
        Assert.Equal(refused.Count, host.RejectedRequests);
        Assert.Equal(refused.Count, host.Decisions.Log.CountOf(ReplayNotes.RequestRejected));

        // The question closes, and its event says why.
        var failed = host.Inhabitants.EventsSince(seen).Where(e => e.Event.Kind == "question_failed").ToList();
        Assert.Equal(refused.Count, failed.Count);
        Assert.All(failed, e => Assert.Equal(ReplayNotes.RequestRejected, e.Event.Detail));

        // A replay from the log takes the refusals as the errors they were: same world.
        var replay = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new ReplayDecisionPort(host.Decisions.Log), "de", Preset);
        replay.World.Run(30);
        Assert.Equal(host.World.StateHash(), replay.World.StateHash());
        Assert.Equal(host.RejectedRequests, replay.RejectedRequests);
    }

    [Fact]
    public void OtherErrorsOfTheServiceKeepTheirName()
    {
        var port = new AnsweringPort(request => Error(request, ServiceErrorCode.Overloaded, retryable: true));
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, port, "de", Preset);
        host.World.Run(30);

        var errors = host.Decisions.Log.Entries.Where(e => e.Kind == DecisionLogKind.Error).ToList();
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.Equal(ReplayNotes.ServiceError, e.Note));
        Assert.Equal(0, host.RejectedRequests);
    }

    [Theory]
    [InlineData("{\"code\":\"bad_request\",\"message\":\"x\"}", true)]
    [InlineData("{\"code\":\"overloaded\"}", false)]
    [InlineData("{\"code\":7}", false)]
    [InlineData("[]", false)]
    [InlineData("no json at all", false)]
    public void ARefusalIsReadFromTheErrorsCode(string errorJson, bool refusal)
    {
        Assert.Equal(refusal, ReplayNotes.IsRejection(errorJson));
        Assert.Equal(refusal ? ReplayNotes.RequestRejected : ReplayNotes.ServiceError, ReplayNotes.OfError(errorJson));
        Assert.True(ReplayNotes.IsServiceError(ReplayNotes.OfError(errorJson)));
        Assert.False(ReplayNotes.IsServiceError(ReplayNotes.UnknownOption));
        Assert.False(ReplayNotes.IsServiceError(null));
    }

    private static string Error(DecisionRequest request, ServiceErrorCode code, bool retryable) => ContractSerializer.Serialize(new ServiceError
    {
        ContractVersion = ContractInfo.Version,
        RequestId = request.RequestId,
        Tick = request.Tick,
        Code = code,
        Message = "from the test",
        Retryable = retryable,
    });

    /// <summary>Answers every request at the next tick with an error the test writes.</summary>
    private sealed class AnsweringPort(Func<DecisionRequest, string> error) : IDecisionPort
    {
        private readonly Queue<PortMessage> _ready = new();

        public void Send(string requestId, string requestJson) =>
            _ready.Enqueue(new PortMessage(requestId, error(ContractSerializer.Deserialize<DecisionRequest>(requestJson)), IsError: true));

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var messages = _ready.ToList();
            _ready.Clear();
            return messages;
        }
    }
}
