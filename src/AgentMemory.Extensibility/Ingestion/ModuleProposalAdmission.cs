using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Ingestion;

/// <summary>Pure proposal shape validation. Acceptance grants neither activation nor permission to persist.</summary>
[Experimental("AMEXT001")]
public static class ModuleProposalAdmission
{
    /// <summary>Validates against frozen metadata and returns an independent immutable proposal snapshot.</summary>
    /// <remarks>Call after owner-policy resolution. Does not access storage or verify persisted provenance,
    /// migration readiness, target existence, final required anchors, or final link cardinality after merging.
    /// The store must enforce those in its transaction. Do not mutate inputs concurrently with this call.</remarks>
    public static ModuleProposalAdmissionResult Admit(ModuleResolution resolution, IngestionProviderDescriptor provider,
        IngestionRequest request, ModuleProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(provider);
        var providerId = provider.Id;
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        if (!resolution.Succeeded) errors.Add("resolution: successful whole-set metadata resolution is required.");
        var module = resolution.Modules.FirstOrDefault(m => ModuleIdentity.IsOwnedId(m.Id, providerId));
        if (module is null) errors.Add("provider: identity must belong to a resolved module.");
        if (!ValidIdentity(request.SessionId)) errors.Add("request.session: nonempty session identity is required.");
        if (provider.Trigger is not (IngestionTrigger.EveryTurn or IngestionTrigger.OnRequest)) errors.Add("provider.trigger: unknown trigger.");
        if (provider.Trigger == IngestionTrigger.OnRequest && request.Trigger != IngestionTrigger.OnRequest)
            errors.Add("provider.trigger: OnRequest providers cannot run during an automatic turn.");
        if (request.Trigger is not (IngestionTrigger.EveryTurn or IngestionTrigger.OnRequest)) errors.Add("request.trigger: unknown trigger.");
        if (request.ProviderIds is null) errors.Add("request.providers: collection cannot be null.");
        if (request.Trigger == IngestionTrigger.OnRequest && !(request.ProviderIds?.Contains(providerId, StringComparer.Ordinal) ?? false))
            errors.Add("request.providers: OnRequest requires explicit selection of this provider.");
        var messages = new HashSet<string>(StringComparer.Ordinal);
        if (request.Messages is null) errors.Add("request.messages: collection cannot be null.");
        else foreach (var message in request.Messages)
        {
            if (message is null || !ValidIdentity(message.Id) || message.Role is not ("user" or "assistant") || message.Text is null)
                errors.Add("request.messages: each source requires identity, user/assistant role and text.");
            else if (!messages.Add(message.Id)) errors.Add("request.messages: duplicate source identity.");
        }
        var type = module?.Ontology?.Types.FirstOrDefault(t => t.Name == proposal.Type);
        if (type is null) errors.Add("type: must name a type owned by the provider's module.");
        if (type?.Scope == "shared" && (request.Trigger != IngestionTrigger.OnRequest || provider.Trigger != IngestionTrigger.OnRequest))
            errors.Add("type.scope: shared writes require an explicit OnRequest provider selection.");
        if (!ValidIdentity(proposal.Key)) errors.Add("key: must be nonempty and contain no control characters.");
        if (proposal.Kind is not (ProposalKind.Assert or ProposalKind.Invalidate)) errors.Add("kind: unknown proposal operation.");
        ValidateSource(proposal.Source, messages, errors);
        ValidateProperties(proposal, type, errors);
        ValidateLinks(proposal, module, errors);
        if (errors.Count != 0) return new(null, errors.ToList().AsReadOnly());
        var frozen = proposal with
        {
            Properties = proposal.Properties.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal).AsReadOnly(),
            Links = proposal.Links.Select(link => link with { }).ToList().AsReadOnly(),
            Source = proposal.Source with { MessageIds = proposal.Source.MessageIds.ToList().AsReadOnly() }
        };
        return new(frozen, []);
    }

    private static bool ValidIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static void ValidateSource(ProposalSource? source, HashSet<string> messages, SortedSet<string> errors)
    {
        if (source is null) { errors.Add("source: provenance is required."); return; }
        if (string.IsNullOrWhiteSpace(source.Excerpt) || source.Excerpt.Length > 500)
            errors.Add("source.excerpt: requires 1-500 characters with non-whitespace content.");
        if (source.MessageIds is not { Count: > 0 }) { errors.Add("source.messageIds: at least one source ID is required."); return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in source.MessageIds)
            if (!ValidIdentity(id) || !seen.Add(id) || !messages.Contains(id))
                errors.Add("source.messageIds: IDs must be distinct members of this request.");
    }

    private static void ValidateProperties(ModuleProposal proposal, ModuleType? type, SortedSet<string> errors)
    {
        if (proposal.Properties is null) { errors.Add("properties: collection cannot be null."); return; }
        if (proposal.Kind == ProposalKind.Invalidate)
        {
            if (proposal.Properties.Count != 0) errors.Add("properties: invalidation cannot carry state changes.");
            return;
        }
        if (type is null) return;
        var properties = proposal.Properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var declared in type.Properties)
            if (declared.Value.Required && !properties.ContainsKey(declared.Key)) errors.Add("properties." + declared.Key + ": required value missing.");
        foreach (var pair in properties)
        {
            var path = "properties." + pair.Key;
            if (!type.Properties.TryGetValue(pair.Key, out var declared)) { errors.Add(path + ": undeclared property."); continue; }
            try
            {
                if (!ModuleScalarValues.ValueFits(pair.Value, declared.Type, allowNull: false, allowPlaceholders: false))
                { errors.Add(path + ": scalar does not fit the declared type."); continue; }
                if (declared.Type == "string")
                {
                    var text = pair.Value.GetString()!;
                    if (declared.MaxLength is { } max && text.Length > max) errors.Add(path + ": exceeds maximum length.");
                    if (declared.Values.Count > 0 && !declared.Values.Contains(text, StringComparer.Ordinal)) errors.Add(path + ": value is not in the declared enumeration.");
                }
                if (declared.Type is "int" or "float")
                {
                    if (declared.Minimum is { } min && Compare(pair.Value, min, declared.Type) < 0) errors.Add(path + ": below minimum.");
                    if (declared.Maximum is { } max && Compare(pair.Value, max, declared.Type) > 0) errors.Add(path + ": above maximum.");
                }
            }
            catch (ObjectDisposedException) { errors.Add(path + ": source JSON document has been disposed."); }
        }
    }

    private static int Compare(JsonElement value, JsonElement bound, string type) => type == "int"
        ? value.GetInt64().CompareTo(bound.GetInt64()) : value.GetDouble().CompareTo(bound.GetDouble());

    private static void ValidateLinks(ModuleProposal proposal, ModuleManifest? module, SortedSet<string> errors)
    {
        if (proposal.Links is null) { errors.Add("links: collection cannot be null."); return; }
        if (proposal.Kind == ProposalKind.Invalidate && proposal.Links.Count != 0) errors.Add("links: invalidation cannot change relationships.");
        var seen = new HashSet<(string, string, LinkTargetKind)>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var link in proposal.Links)
        {
            if (link is null) { errors.Add("links: null link."); continue; }
            var relation = module?.Ontology?.Relations.FirstOrDefault(r => r.Name == link.Relation && r.From == proposal.Type);
            if (relation is null) { errors.Add("links: relation must be declared for the proposal's owned type."); continue; }
            if (!ValidIdentity(link.Target)) errors.Add("links: target identity or name cannot be empty or contain control characters.");
            if (link.TargetKind is not (LinkTargetKind.Identity or LinkTargetKind.EntityName)
                || (link.TargetKind == LinkTargetKind.EntityName && relation.To != "core:Entity"))
                errors.Add("links: target selector is unsupported for this relation.");
            if (!seen.Add((link.Relation, link.Target, link.TargetKind))) errors.Add("links: duplicate target.");
            counts[link.Relation] = counts.GetValueOrDefault(link.Relation) + 1;
            if (relation.Max is { } max && counts[link.Relation] > max) errors.Add("links: proposed targets exceed declared maximum.");
        }
    }
}

/// <summary>Shape-admission result; an accepted snapshot still requires transactional storage checks.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleProposalAdmissionResult
{
    internal ModuleProposalAdmissionResult(ModuleProposal? proposal, IReadOnlyList<string> errors)
    { Proposal = proposal; Errors = errors; }
    /// <summary>Whether shape validation succeeded, not whether the record was persisted.</summary>
    public bool Accepted => Proposal is not null && Errors.Count == 0;
    /// <summary>Deep-frozen copy when accepted; null on refusal.</summary>
    public ModuleProposal? Proposal { get; }
    /// <summary>Concrete refusal reasons in ordinal order.</summary>
    public IReadOnlyList<string> Errors { get; }
}
