using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Random;

namespace GlassWorld.Simulation.Decisions;

/// <summary>
/// Test double for the decider (CI has no GPU, ADR-0006): answers every request at the next tick with an
/// option drawn from the request's own seed. Deterministic and independent of the world's streams. It
/// knows nothing about the world and never invents options.
/// </summary>
public sealed class MockDecisionPort : IDecisionPort
{
    private readonly Queue<PortMessage> _ready = new();

    /// <inheritdoc/>
    public void Send(string requestId, string requestJson)
    {
        var request = ContractSerializer.Deserialize<DecisionRequest>(requestJson);
        var options = request.Options.ToList();
        var rng = new Pcg32((ulong)request.Seed, 0);
        var chosen = options[rng.NextInt(0, options.Count)];
        var response = new DecisionResponse
        {
            ContractVersion = request.ContractVersion,
            RequestId = request.RequestId,
            Tick = request.Tick,
            OptionId = chosen.OptionId,
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
