using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.Simulation.Decisions;

/// <summary>
/// Test double for the decider (CI has no GPU, ADR-0006): answers every request at the next tick with an
/// option picked by the generic mock rule shared with the AI service (Bogen 34 R26,
/// <c>ai-service/src/glassworld_ai/decider/mock.py</c>, vectors in <c>tests/mock/cases.json</c>). Weights per
/// action come from <c>content/ai/mock-weights.json</c>. Deterministic and independent of the world's
/// streams: the only randomness is the request's own seed. It knows nothing about the world and never
/// invents options.
/// </summary>
public sealed class MockDecisionPort : IDecisionPort
{
    /// <summary>The weights file below the content folder.</summary>
    public const string WeightsFile = "ai/mock-weights.json";

    private readonly Queue<PortMessage> _ready = new();
    private readonly MockWeights _weights;

    /// <summary>Every action weighs 1000 permille (tests without a content folder).</summary>
    public MockDecisionPort()
        : this(new MockWeights { DefaultPermille = 1000 })
    {
    }

    /// <summary>Picks by the given weights.</summary>
    public MockDecisionPort(MockWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        _weights = weights;
    }

    /// <summary>The mock with the weights from <c>content/ai/mock-weights.json</c>.</summary>
    public static MockDecisionPort FromContent(string contentDirectory) =>
        new(ContractSerializer.Deserialize<MockWeights>(File.ReadAllText(Path.Combine(contentDirectory, WeightsFile))));

    /// <summary>
    /// The shared rule: options sorted by option id (ordinal); total = sum of their weights; total 0 gives
    /// the option with the action idle, else the first by id; otherwise pick = seed mod total and the first
    /// option whose running sum exceeds pick wins. No branch for any single option (A3).
    /// </summary>
    public static string Pick(IEnumerable<(string OptionId, string Action)> options, long seed, MockWeights weights)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentOutOfRangeException.ThrowIfNegative(seed);
        var ordered = options.OrderBy(o => o.OptionId, StringComparer.Ordinal).ToList();
        if (ordered.Count == 0)
        {
            throw new ArgumentException("No options to choose from.", nameof(options));
        }

        var permille = weights.Weights.ToDictionary(w => w.Action, w => (long)w.Permille, StringComparer.Ordinal);
        long Of(string action) => permille.TryGetValue(action, out var p) ? p : weights.DefaultPermille;
        var total = ordered.Sum(o => Of(o.Action));
        if (total == 0)
        {
            return ordered.FirstOrDefault(o => string.Equals(o.Action, "idle", StringComparison.Ordinal)).OptionId ?? ordered[0].OptionId;
        }

        var target = seed % total;
        long running = 0;
        foreach (var (optionId, action) in ordered)
        {
            running += Of(action);
            if (running > target)
            {
                return optionId;
            }
        }

        throw new InvalidOperationException("Unreachable: the running sum reaches the total.");
    }

    /// <inheritdoc/>
    public void Send(string requestId, string requestJson)
    {
        var request = ContractSerializer.Deserialize<DecisionRequest>(requestJson);
        var chosen = Pick(request.Options.Select(o => (o.OptionId, o.Action)), request.Seed, _weights);
        var response = new DecisionResponse
        {
            ContractVersion = request.ContractVersion,
            RequestId = request.RequestId,
            Tick = request.Tick,
            OptionId = chosen,
            Expression = null,
            Rationale = "mock",
            Thought = null,
            Meta = new ResponseMeta { Source = ResponseMetaSource.Mock, Model = "mock", LatencyMs = 0, Attempts = 1 },
        };
        _ready.Enqueue(new PortMessage(requestId, ContractSerializer.Serialize(response), IsError: false));
    }

    /// <inheritdoc/>
    public IReadOnlyList<PortMessage> Receive(long tick)
    {
        var messages = _ready.ToList();
        _ready.Clear();
        return messages;
    }
}
