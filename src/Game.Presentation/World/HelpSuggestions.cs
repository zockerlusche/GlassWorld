using System.Security.Cryptography;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Presentation.World;

/// <summary>A suggestion card in the help window.</summary>
/// <param name="Kind">"gift" (the player may place it) or "answer" (an encouraging sentence).</param>
/// <param name="GiftType">Object type of a gift card; null for an answer.</param>
/// <param name="Text">The AI's sentence (already in the display language); null for a card from the data.</param>
public sealed record HelpCard(string Kind, string? GiftType, string? Text)
{
    /// <summary>Kind of a gift card.</summary>
    public const string Gift = "gift";

    /// <summary>Kind of an answer card.</summary>
    public const string Answer = "answer";
}

/// <summary>
/// Help suggestions for an open call (A1341, B6 R8/R10, F5238): the request to the AI service and the
/// cards the help window shows. The AI only proposes: gift types outside the allowed list are dropped,
/// and the core still checks every gift the player places. Without a usable answer the cards come from
/// the data (GiftFallback).
/// </summary>
public static class HelpSuggestions
{
    /// <summary>The place command puts exactly one object, so a gift suggestion is for one.</summary>
    public const int GiftAmount = 1;

    /// <summary>The contract allows at most three suggestions.</summary>
    public const int ContractMax = 3;

    /// <summary>Object types that may be suggested for a call: those that truly help by the data (world truth).</summary>
    public static IReadOnlyList<string> AllowedTypes(SimulationHost host, HelpRow call)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(call);
        return GiftFallback.For(call.Need, host.Content.Brain.Weights, host.Content.Objects, 32);
    }

    /// <summary>How many suggestions the window shows (data, capped by the contract).</summary>
    public static int MaxSuggestions(SimulationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Math.Clamp(host.Content.GiftRules.MaxOpenSuggestions, 1, ContractMax);
    }

    /// <summary>
    /// The request for an open call. Id and seed come from world seed, the call's tick, inhabitant and
    /// need, so the same call always asks the same question.
    /// </summary>
    public static HelpSuggestionsRequest Request(SimulationHost host, HelpRow call, string locale)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(call);
        var requestId = DecisionIds.RequestId(host.World.Seed, call.Tick, call.InhabitantId + "|help|" + call.Need, 0);
        var request = new HelpSuggestionsRequest
        {
            ContractVersion = ContractInfo.Version,
            RequestId = requestId,
            Tick = host.World.Tick,
            Seed = (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)), 0) & 0x7FFFFFFF),
            DeadlineMs = host.DeciderSettings.DeadlineMs,
            DisplayLocale = Enum.Parse<Locale>(locale, ignoreCase: true),
            InhabitantId = call.InhabitantId,

            // A call for help is the knock at the glass (A1341, TL #19249).
            Trigger = HelpSuggestionsRequestTrigger.Knock,
            FailedAttempts = Math.Min(1000, host.Player.AttemptsOf(call.InhabitantId, call.Need)),
            MaxSuggestions = MaxSuggestions(host),
        };
        request.Missing.Add(call.Need);
        foreach (var type in AllowedTypes(host, call))
        {
            request.AllowedGifts.Add(new AllowedGift { TypeId = type, MaxAmount = GiftAmount });
        }

        return request;
    }

    /// <summary>
    /// Cards for a call: the AI's suggestions when there is a usable answer (gifts only of an allowed
    /// type), otherwise gift cards from the data.
    /// </summary>
    public static IReadOnlyList<HelpCard> Cards(HelpSuggestionsResponse? response, IReadOnlyList<string> allowed, int max)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        var cards = new List<HelpCard>();
        foreach (var suggestion in response?.Suggestions ?? [])
        {
            if (suggestion.Kind == HelpSuggestionKind.Answer && suggestion.Gift is null)
            {
                cards.Add(new HelpCard(HelpCard.Answer, null, suggestion.Reason.Text));
            }
            else if (suggestion.Kind == HelpSuggestionKind.Gift && suggestion.Gift is { } gift && allowed.Contains(gift.TypeId, StringComparer.Ordinal))
            {
                cards.Add(new HelpCard(HelpCard.Gift, gift.TypeId, suggestion.Reason.Text));
            }
        }

        return cards.Count > 0
            ? [.. cards.Take(max)]
            : [.. allowed.Take(max).Select(type => new HelpCard(HelpCard.Gift, type, null))];
    }
}
