using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

internal sealed partial class PersistenceStage
{
    private async Task<PreparedEmbeddings> PrepareEmbeddingsAsync(
        ExtractionStageResult extraction,
        CancellationToken cancellationToken)
    {
        if (!_options.UseBatchEmbeddingRequests)
            return await PrepareEmbeddingsIndividuallyAsync(extraction, cancellationToken).ConfigureAwait(false);

        var inputs = BuildLearnedEmbeddingInputs(extraction);
        if (inputs.Count < 2)
            return await PrepareEmbeddingsIndividuallyAsync(extraction, cancellationToken).ConfigureAwait(false);

        var failFast = _options.FailureMode == Abstractions.Options.IngestionFailureMode.FailFast;
        var outcomes = new List<IngestionItemOutcome>();
        var entities = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        var facts = new List<PreparedFact>(extraction.FilteredFacts.Count);
        var preferences = new List<PreparedPreference>(extraction.FilteredPreferences.Count);

        foreach (var (name, entity) in extraction.ResolvedEntityMap)
        {
            if (entity.Embedding is not null)
                entities[name] = entity;
        }

        IReadOnlyList<float[]>? batchResults = null;
        var replayWholeBatch = false;
        try
        {
            batchResults = await _embeddingOrchestrator
                .EmbedBatchAsync(inputs.Select(input => input.Text).ToArray(), cancellationToken)
                .ConfigureAwait(false);

            if (batchResults is null || batchResults.Count != inputs.Count)
            {
                replayWholeBatch = true;
                _logger.LogWarning(
                    "Learned-memory embedding batch returned {Returned} vectors for {Requested} inputs; replaying the batch through the item path.",
                    batchResults?.Count ?? 0,
                    inputs.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            replayWholeBatch = true;
            _logger.LogWarning(
                ex,
                "Learned-memory embedding batch failed for {Count} inputs; replaying through the item path.",
                inputs.Count);
        }

        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            float[]? embedding = !replayWholeBatch &&
                batchResults![index] is { Length: > 0 } available
                    ? available
                    : await EmbedSingleLearnedInputAsync(
                        input,
                        outcomes,
                        failFast,
                        cancellationToken).ConfigureAwait(false);

            if (embedding is null)
                continue;

            switch (input.Kind)
            {
                case MemoryItemKind.Entity:
                    entities[input.SourceKey] = input.Entity! with { Embedding = embedding };
                    break;
                case MemoryItemKind.Fact:
                    facts.Add(new PreparedFact(input.Fact!, embedding));
                    break;
                case MemoryItemKind.Preference:
                    preferences.Add(new PreparedPreference(input.Preference!, embedding));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported learned-memory embedding kind '{input.Kind}'.");
            }
        }

        return new PreparedEmbeddings(entities, facts, preferences, outcomes);
    }

    private static List<LearnedEmbeddingInput> BuildLearnedEmbeddingInputs(
        ExtractionStageResult extraction)
    {
        var inputs = new List<LearnedEmbeddingInput>(
            extraction.ResolvedEntityMap.Count +
            extraction.FilteredFacts.Count +
            extraction.FilteredPreferences.Count);

        foreach (var (name, entity) in extraction.ResolvedEntityMap)
        {
            if (entity.Embedding is null)
            {
                inputs.Add(new LearnedEmbeddingInput(
                    MemoryItemKind.Entity,
                    name,
                    entity.Name,
                    Entity: entity));
            }
        }

        foreach (var fact in extraction.FilteredFacts)
        {
            var text = $"{fact.Subject} {fact.Predicate} {fact.Object}";
            inputs.Add(new LearnedEmbeddingInput(
                MemoryItemKind.Fact,
                text,
                text,
                Fact: fact));
        }

        foreach (var preference in extraction.FilteredPreferences)
        {
            inputs.Add(new LearnedEmbeddingInput(
                MemoryItemKind.Preference,
                preference.PreferenceText,
                preference.PreferenceText,
                Preference: preference));
        }

        return inputs;
    }

    private async Task<float[]?> EmbedSingleLearnedInputAsync(
        LearnedEmbeddingInput input,
        List<IngestionItemOutcome> outcomes,
        bool failFast,
        CancellationToken cancellationToken)
    {
        try
        {
            return input.Kind switch
            {
                MemoryItemKind.Entity => await _embeddingOrchestrator
                    .EmbedEntityAsync(input.Entity!.Name, cancellationToken)
                    .ConfigureAwait(false),
                MemoryItemKind.Fact => await _embeddingOrchestrator
                    .EmbedFactAsync(
                        input.Fact!.Subject,
                        input.Fact.Predicate,
                        input.Fact.Object,
                        cancellationToken)
                    .ConfigureAwait(false),
                MemoryItemKind.Preference => await _embeddingOrchestrator
                    .EmbedPreferenceAsync(input.Preference!.PreferenceText, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"Unsupported learned-memory embedding kind '{input.Kind}'.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error generating learned-memory embedding for {Kind} '{SourceKey}'.",
                input.Kind,
                input.SourceKey);
            RecordFailureAndMaybeThrow(
                outcomes,
                failFast,
                input.Kind,
                IngestionStage.Embedding,
                MemoryErrorCodes.EmbeddingGenerationFailed,
                input.SourceKey,
                null,
                ex,
                FailFastEmbeddingMessage(input));
            return null;
        }
    }

    private static string FailFastEmbeddingMessage(LearnedEmbeddingInput input) =>
        input.Kind switch
        {
            MemoryItemKind.Entity =>
                $"Ingestion failed fast: embedding generation failed for entity '{input.SourceKey}'.",
            MemoryItemKind.Fact =>
                $"Ingestion failed fast: embedding generation failed for fact '{input.SourceKey}'.",
            MemoryItemKind.Preference =>
                "Ingestion failed fast: embedding generation failed for a preference.",
            _ =>
                $"Ingestion failed fast: embedding generation failed for '{input.SourceKey}'."
        };

    private sealed record LearnedEmbeddingInput(
        MemoryItemKind Kind,
        string SourceKey,
        string Text,
        Entity? Entity = null,
        ExtractedFact? Fact = null,
        ExtractedPreference? Preference = null);
}
