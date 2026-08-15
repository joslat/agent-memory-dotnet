using System.Diagnostics;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Services;

internal sealed partial class MemoryExtractionPipeline
{
    public async Task<IReadOnlyList<ExtractionResult>> ExtractBatchAsync(
        IReadOnlyList<ExtractionRequest> requests,
        int maxSessionsPerBatch,
        int maxInputTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessionsPerBatch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputTokens);
        if (requests.Count == 0)
            return [];

        var ordered = requests
            .Select((request, index) => new
            {
                Request = request,
                Index = index,
                Timestamp = request.Messages
                    .Select(message => message.TimestampUtc)
                    .DefaultIfEmpty(DateTimeOffset.MinValue)
                    .Min(),
            })
            .OrderBy(item => item.Timestamp)
            .ThenBy(item => item.Index)
            .Select(item => item.Request)
            .ToArray();

        var batchExtractor = _multiSessionExtractors.FirstOrDefault(extractor => extractor.IsEnabled);
        if (batchExtractor is null)
        {
            var fallback = new List<ExtractionResult>(ordered.Length);
            foreach (var request in ordered)
                fallback.Add(await ExtractAsync(request, cancellationToken).ConfigureAwait(false));
            return fallback;
        }

        var extractedBySession = await batchExtractor.ExtractAsync(
            ordered,
            maxSessionsPerBatch,
            maxInputTokens,
            cancellationToken).ConfigureAwait(false);

        var persisted = new List<ExtractionResult>(ordered.Length);
        using var resolutionBatch = _extractionStage.BeginResolutionBatch();
        foreach (var request in ordered)
        {
            if (!extractedBySession.TryGetValue(request.SessionId, out var extracted))
                throw new InvalidOperationException(
                    $"Validated batch output is missing source session '{request.SessionId}'.");

            var sw = Stopwatch.StartNew();
            var scope = _isolationPolicy.ResolveReadScope(
                explicitScope: null,
                request.UserId,
                nameof(ExtractBatchAsync),
                MemoryOperationAccess.Tenant);
            var staged = await _extractionStage.ProcessUnifiedAsync(
                request.Messages,
                extracted,
                request.TypesToExtract,
                scope,
                cancellationToken).ConfigureAwait(false);

            var ownerId = _isolationPolicy.ResolveWriteOwner(
                request.UserId,
                nameof(ExtractBatchAsync),
                MemoryOperationAccess.Tenant);
            var trustLevel = request.TrustLevel ?? _options.DefaultTrustLevel;
            var result = await _persistenceStage.PersistAsync(
                staged,
                ownerId,
                trustLevel,
                cancellationToken).ConfigureAwait(false);
            // 30.6, on BOTH paths. Every recorded quality number in this project came from the batch
            // extractor, so a feature wired only into the per-request path would be measured as absent
            // and concluded ineffective -- the shape of at least two earlier findings here.
            await AccountAsync(staged, ownerId, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            if (result.Outcomes.Any(outcome => outcome.Status == IngestionItemStatus.Failed))
                _extractionStage.InvalidateResolutionBatch();


            persisted.Add(new ExtractionResult
            {
                Entities = staged.RawEntities,
                Facts = staged.RawFacts,
                Preferences = staged.RawPreferences,
                Relationships = staged.RawRelationships,
                SourceMessageIds = staged.SourceMessageIds,
                Status = ComputeStatus(result.Outcomes),
                Outcomes = result.Outcomes,
                Metadata = new Dictionary<string, object>
                {
                    ["sessionId"] = request.SessionId,
                    ["postBatchProcessingTimeMs"] = sw.ElapsedMilliseconds,
                    ["entityCount"] = result.EntityCount,
                    ["factCount"] = result.FactCount,
                    ["preferenceCount"] = result.PreferenceCount,
                    ["relationshipCount"] = result.RelationshipCount,
                },
            });
        }

        return persisted;
    }
}
