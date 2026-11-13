using System.Buffers;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Resolution;

/// <summary>Validates and freezes the atomic boundary before any database await.</summary>
internal static class IdentityDecisionPayload
{
    internal const int MaxBytes = 1_048_576;
    internal const int MaxBindings = 256;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };

    internal static IdentityDecisionWrite Capture(IdentityDecisionWrite input, string? ownerId)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Decision);
        var decision = input.Decision;
        Text(decision.DecisionId); Text(decision.ObservationKey);
        ArgumentNullException.ThrowIfNull(decision.Observation);
        var observation = decision.Observation;
        Text(observation.EntityId); Text(observation.Name); Text(observation.Type);
        if (ownerId is not null) Text(ownerId);
        if (!string.Equals(observation.OwnerId, ownerId, StringComparison.Ordinal))
            throw new ArgumentException("An identity observation must belong to the exact write owner.");
        if (!Enum.IsDefined(decision.Status) || decision.Version != 1 || decision.LatestReview is not null)
            throw new ArgumentException("An initial decision needs a valid status and version one.");
        ArgumentNullException.ThrowIfNull(decision.Policy);
        var configuration = IdentityConfiguration.Capture(new IdentityResolutionOptions
        { Enabled = true, Policies = new Dictionary<string, EntityIdentityPolicy> { [observation.Type] = decision.Policy } });
        _ = configuration.PolicyFor(observation.Type);
        if (input.ExpectedCandidates is null || input.ExpectedCandidates.Count > 64 ||
            input.Bindings is null || input.Bindings.Count > MaxBindings ||
            decision.PendingEntityIds is null || decision.PendingEntityIds.Count > 64)
            throw new ArgumentException("The identity decision exceeds its candidate/binding bounds.");
        if (input.ExpectedCandidates.Select(candidate => candidate.Entity.EntityId).Distinct(StringComparer.Ordinal).Count() != input.ExpectedCandidates.Count)
            throw new ArgumentException("Candidate snapshot IDs must be distinct.");
        foreach (var candidate in input.ExpectedCandidates)
        {
            Text(candidate.Entity.EntityId);
            var candidateOwner = candidate.Entity.OwnerId;
            if (candidateOwner != ownerId && !(ownerId is not null && input.IncludeShared && candidateOwner is null))
                throw new ArgumentException("A candidate snapshot is outside the permitted owner/shared scope.");
            CheckEntity(candidate.Entity);
        }
        var offered = input.ExpectedCandidates.Select(candidate => candidate.Entity.EntityId).ToHashSet(StringComparer.Ordinal);
        if (decision.Status == IdentityDecisionKind.Confirmed)
        {
            if (decision.ConfirmedEntityId is null || !offered.Contains(decision.ConfirmedEntityId) || decision.PendingEntityIds.Count != 0)
                throw new ArgumentException("Confirmation requires exactly one offered target and no pending candidates.");
        }
        else if (decision.ConfirmedEntityId is not null ||
            decision.Status == IdentityDecisionKind.New && decision.PendingEntityIds.Count != 0 ||
            decision.Status == IdentityDecisionKind.Pending && (decision.PendingEntityIds.Count == 0 || decision.PendingEntityIds.Any(id => !offered.Contains(id))))
            throw new ArgumentException("New/pending identity status and offered candidates disagree.");
        foreach (var binding in input.Bindings)
        {
            Text(binding.FactId);
            if (!Enum.IsDefined(binding.Role)) throw new ArgumentException("A fact binding needs a supported original role.");
        }
        CheckEntity(observation);
        var context = decision.Context ?? throw new ArgumentException("A decision needs its source context.");
        if (context.SourceMessageIds.Count > 256 || context.Sources.Count > 256 || context.VerifiedKeys.Count > 32 ||
            context.SourceMessageIds.Count == 0 && observation.SourceMessageIds.Count == 0)
            throw new ArgumentException("A decision needs bounded source provenance.");
        if (context.NameNamespaceEvidence is { } namespaceEvidence)
        {
            Text(namespaceEvidence.Namespace); Text(namespaceEvidence.Authority); Text(namespaceEvidence.SourceId);
            if (!string.Equals(context.NameNamespace, namespaceEvidence.Namespace, StringComparison.Ordinal))
                throw new ArgumentException("Name namespace and its provenance must agree exactly.");
        }
        foreach (var id in context.SourceMessageIds) Text(id);
        foreach (var source in context.Sources)
        {
            Text(source.SourceId);
            if (source.Text is null || source.Text.Length > 65_536 || source.OriginIds.Count > 256)
                throw new ArgumentException("Identity source fragments exceed their bounds.");
            foreach (var origin in source.OriginIds) Text(origin);
        }
        ValidateContextualEvidence(decision, offered);
        if (decision.EvidenceJson is null || decision.EvidenceJson.Length > 65_536)
            throw new ArgumentException("The evidence receipt exceeds its bound.");
        using var evidence = JsonDocument.Parse(decision.EvidenceJson, new() { MaxDepth = 16 });
        if (evidence.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("An identity receipt must be a JSON object.");
        // IBufferWriter rejects an oversize request before allocation, including a huge JsonElement.
        // Unknown host objects are rejected before serialization; their getters are never invoked.
        return JsonSerializer.Deserialize<IdentityDecisionWrite>(Serialize(input), Json)
            ?? throw new ArgumentException("Invalid identity write payload.");
    }

    private static void ValidateContextualEvidence(IdentityDecision decision, IReadOnlySet<string> offered)
    {
        if (decision.ContextualEvidence is not { } contextual) return;
        if (!Enum.IsDefined(contextual.Verdict) || contextual.Citations is null || contextual.Citations.Count > 32 ||
            contextual.CandidateId is { } id && !offered.Contains(id) ||
            contextual.Verdict != IdentityAdjudicationVerdict.Uncertain &&
                (contextual.CandidateId is null || contextual.Citations.Count == 0))
            throw new ArgumentException("Contextual identity evidence needs a valid verdict, offered candidate and bounded supporting quotes.");
        if (decision.Status == IdentityDecisionKind.Confirmed &&
            (contextual.Verdict != IdentityAdjudicationVerdict.Same || contextual.CandidateId != decision.ConfirmedEntityId ||
                !decision.Policy.AllowContextualConfirmation))
            throw new ArgumentException("A contextual confirmation requires matching type permission and target.");
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in decision.Context.Sources)
        {
            if (sources.TryGetValue(source.SourceId, out var previous) && previous != source.Text)
                throw new ArgumentException("Contextual source identifiers cannot have conflicting text.");
            sources[source.SourceId] = source.Text;
        }
        foreach (var quote in contextual.Citations)
        {
            if (quote is null) throw new ArgumentException("A contextual citation cannot be null.");
            Text(quote.SourceId);
            if (string.IsNullOrWhiteSpace(quote.Quote) || quote.Quote.Length > 2_048 ||
                !sources.TryGetValue(quote.SourceId, out var text) || !text.Contains(quote.Quote, StringComparison.Ordinal))
                throw new ArgumentException("A contextual citation must be an exact substring of an offered source.");
        }
    }

    internal static string Encode(IdentityDecision value) => Encoding.UTF8.GetString(Serialize(value));
    internal static IdentityDecision Decode(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaxBytes) throw new InvalidOperationException("Stored identity decision exceeds its bound.");
        return JsonSerializer.Deserialize<IdentityDecision>(value, Json) ?? throw new InvalidOperationException("Invalid stored identity decision.");
    }

    internal static byte[] Serialize<T>(T value)
    {
        var buffer = new BoundedBuffer();
        using (var writer = new Utf8JsonWriter(buffer, new() { MaxDepth = 16 }))
        { JsonSerializer.Serialize(writer, value, Json); writer.Flush(); }
        return buffer.Written.ToArray();
    }

    private static void CheckEntity(Entity entity)
    {
        Text(entity.Name); Text(entity.Type);
        if (!double.IsFinite(entity.Confidence) || entity.Confidence is < 0 or > 1 ||
            entity.SourceMessageIds.Count > 256 || entity.Aliases.Count > 256 ||
            entity.Description?.Length > 65_536 || entity.CanonicalName?.Length > 4096 ||
            entity.Embedding?.Length > 65536)
            throw new ArgumentException("An identity entity exceeds its value/context bounds.");
        foreach (var id in entity.SourceMessageIds) Text(id);
        foreach (var alias in entity.Aliases) Text(alias);
        CheckData(entity.Attributes, 0); CheckData(entity.Metadata, 0);
    }

    internal static void CheckData(object? value, int depth)
    {
        if (depth > 12) throw new ArgumentException("Identity attribute nesting exceeds its bound.");
        switch (value)
        {
            case null or bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal: return;
            case string text when text.Length <= 65_536: return;
            case double number when double.IsFinite(number): return;
            case float number when float.IsFinite(number): return;
            case JsonElement: return; // Written through the bounded UTF8 writer, never through arbitrary getters.
            case IReadOnlyDictionary<string, object> map when map.Count <= 256:
                foreach (var (name, child) in map) { Text(name); CheckData(child, depth + 1); } return;
            case IReadOnlyList<object> list when list.Count <= 256:
                foreach (var child in list) CheckData(child, depth + 1); return;
            case Array array when array.Rank == 1 && array.GetLowerBound(0) == 0 && array.Length <= 256:
                foreach (var child in array) CheckData(child, depth + 1); return;
            default: throw new ArgumentException("Identity attributes must contain bounded JSON data, never arbitrary host objects.");
        }
    }

    private static void Text(string? value)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new ArgumentException("Identity text must contain 1–4096 characters."); }

    private sealed class BoundedBuffer : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _buffer = new(4096);
        public ReadOnlySpan<byte> Written => _buffer.WrittenSpan;
        public void Advance(int count)
        {
            if (count < 0 || count > MaxBytes - _buffer.WrittenCount) throw new ArgumentException("Identity payload exceeds its UTF8 budget.");
            _buffer.Advance(count);
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        { Check(sizeHint); var memory = _buffer.GetMemory(sizeHint); return memory[..Math.Min(memory.Length, MaxBytes - _buffer.WrittenCount)]; }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        private void Check(int sizeHint)
        {
            if (sizeHint < 0 || Math.Max(1, sizeHint) > MaxBytes - _buffer.WrittenCount)
                throw new ArgumentException("Identity payload exceeds its UTF8 budget.");
        }
    }
}
