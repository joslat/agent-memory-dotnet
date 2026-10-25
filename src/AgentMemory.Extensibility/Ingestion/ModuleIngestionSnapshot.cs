using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentMemory.Extensibility.Ingestion;

[Experimental("AMEXT001")]
internal static class ModuleIngestionSnapshot
{
    internal static IngestionRequest Request(IngestionRequest request, string? owner)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Any(char.IsControl)
            || request.Trigger is not (IngestionTrigger.EveryTurn or IngestionTrigger.OnRequest)
            || request.Messages is null || request.ProviderIds is null)
            throw new ArgumentException("Invalid ingestion request identity, trigger or collections.", nameof(request));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in request.Messages)
            if (message is null || string.IsNullOrWhiteSpace(message.Id) || message.Id.Any(char.IsControl)
                || !ids.Add(message.Id) || message.Role is not ("user" or "assistant") || message.Text is null)
                throw new ArgumentException("Source messages require distinct identities, user/assistant roles and text.", nameof(request));
        if (request.ProviderIds.Any(string.IsNullOrWhiteSpace)
            || request.ProviderIds.Distinct(StringComparer.Ordinal).Count() != request.ProviderIds.Count
            || (request.Trigger == IngestionTrigger.EveryTurn && request.ProviderIds.Count != 0)
            || (request.Trigger == IngestionTrigger.OnRequest && request.ProviderIds.Count == 0))
            throw new ArgumentException("Provider selection must be distinct and explicit only for OnRequest.", nameof(request));
        return request with
        {
            Owner = owner, AtUtc = request.AtUtc.ToUniversalTime(),
            Messages = request.Messages.Select(m => m with { }).ToList().AsReadOnly(),
            ProviderIds = request.ProviderIds.Order(StringComparer.Ordinal).ToList().AsReadOnly()
        };
    }

    internal static string Fingerprint(IngestionRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request))).ToLowerInvariant();

    internal static IReadOnlyList<ModuleProposal> Proposals(IReadOnlyList<ModuleProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        if (proposals.Any(p => p is null)) throw new ArgumentException("Null proposal cannot be recorded.", nameof(proposals));
        return proposals.Select(Proposal).ToList().AsReadOnly();
    }

    // Preserve malformed serializable fields for admission; never repair a refused input into an accepted one.
    internal static ModuleProposal Proposal(ModuleProposal proposal) => proposal with
    {
        Properties = proposal.Properties?.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal).AsReadOnly()!,
        Links = proposal.Links?.Select(link => link is null ? null! : link with { }).ToList().AsReadOnly()!,
        Source = proposal.Source is null ? null! : proposal.Source with
        { MessageIds = proposal.Source.MessageIds?.ToList().AsReadOnly()! },
        Replaces = proposal.Replaces?.ToList().AsReadOnly()!
    };
}
