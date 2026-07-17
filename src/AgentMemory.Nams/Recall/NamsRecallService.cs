using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;

namespace AgentMemory.Nams.Recall;

/// <summary>
/// Implements the engineering plan's §7 Phase 4 baseline: <see cref="INamsClient.GetContextAsync"/> for
/// reflections/observations/recent messages, plus an optional <see cref="INamsClient.SearchEntitiesAsync"/>
/// when a query is given. Ordering is reflections, then observations, then recent messages, then entities --
/// the tiers' own highest-to-lowest-level order, entities appended as an orthogonal category. Deduplicates
/// by <see cref="NamsRecalledItem.SourceId"/> (first occurrence wins), then applies a greedy character-budget
/// truncation from the end of that order.
/// </summary>
internal sealed class NamsRecallService : INamsRecallService
{
    private readonly INamsClient _namsClient;
    private readonly NamsRecallOptions _options;
    private readonly ILogger<NamsRecallService> _logger;

    public NamsRecallService(INamsClient namsClient, IOptions<NamsRecallOptions> options, ILogger<NamsRecallService> logger)
    {
        _namsClient = namsClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<NamsRecallResult> RecallAsync(string namsConversationId, string? queryText, CancellationToken cancellationToken)
    {
        var items = new List<NamsRecalledItem>();
        var warnings = new List<NamsRecallWarning>();
        var isPartial = false;

        try
        {
            var context = await _namsClient.GetContextAsync(namsConversationId, cancellationToken).ConfigureAwait(false);
            items.AddRange(context.Reflections.Select(MapReflection));
            items.AddRange(context.Observations.Select(MapObservation));
            items.AddRange(context.RecentMessages.Select(MapMessage));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller cancellation -- never degrade
        }
        catch (NamsOperationException ex) when (ex.FailureKind is NamsFailureKind.Authentication or NamsFailureKind.Authorization)
        {
            throw; // identity/security violations propagate -- never silently degrade
        }
        catch (NamsOperationException ex)
        {
            _logger.LogWarning(ex, "NAMS context retrieval failed for conversation {ConversationId}; proceeding without hosted context.", namsConversationId);
            isPartial = true;
            warnings.Add(new NamsRecallWarning("context", "NAMS context retrieval failed; proceeding without hosted context."));
        }

        if (_options.IncludeEntitySearch && !string.IsNullOrWhiteSpace(queryText))
        {
            try
            {
                var entities = await _namsClient.SearchEntitiesAsync(queryText, type: null, _options.EntitySearchLimit, cancellationToken)
                    .ConfigureAwait(false);
                items.AddRange(entities.Select(MapEntity));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (NamsOperationException ex) when (ex.FailureKind is NamsFailureKind.Authentication or NamsFailureKind.Authorization)
            {
                throw;
            }
            catch (NamsOperationException ex)
            {
                _logger.LogWarning(ex, "NAMS entity search failed for conversation {ConversationId}; proceeding without entity recall.", namsConversationId);
                isPartial = true;
                warnings.Add(new NamsRecallWarning("entity", "NAMS entity search failed; proceeding without entity recall."));
            }
        }

        var deduplicated = items.DistinctBy(item => item.SourceId).ToList();
        var (budgeted, truncated) = ApplyCharacterBudget(deduplicated, _options.MaxTotalCharacters);

        return new NamsRecallResult
        {
            Items = budgeted,
            IsPartial = isPartial || truncated,
            Warnings = warnings
        };
    }

    private static (IReadOnlyList<NamsRecalledItem> Items, bool Truncated) ApplyCharacterBudget(
        IReadOnlyList<NamsRecalledItem> items, int maxTotalCharacters)
    {
        var kept = new List<NamsRecalledItem>();
        var total = 0;
        foreach (var item in items)
        {
            var length = item.Content.Length;
            if (total + length > maxTotalCharacters)
                return (kept, true);

            kept.Add(item);
            total += length;
        }

        return (kept, false);
    }

    private static NamsRecalledItem MapReflection(NamsReflection reflection) => new()
    {
        SourceId = reflection.Id,
        Category = NamsRecallCategory.Reflection,
        Content = reflection.Content,
        Provenance = NamsRecallProvenance.ModelGenerated,
        CreatedAt = reflection.CreatedAt
    };

    private static NamsRecalledItem MapObservation(NamsObservation observation) => new()
    {
        SourceId = observation.Id,
        Category = NamsRecallCategory.Observation,
        Content = observation.Content,
        Provenance = NamsRecallProvenance.ModelGenerated,
        CreatedAt = observation.CreatedAt
    };

    private static NamsRecalledItem MapMessage(NamsMessage message) => new()
    {
        SourceId = message.Id,
        Category = NamsRecallCategory.RecentMessage,
        Content = message.Content,
        Provenance = ProvenanceForRole(message.Role),
        Role = message.Role,
        CreatedAt = message.CreatedAt
    };

    private static NamsRecalledItem MapEntity(NamsEntity entity) => new()
    {
        SourceId = entity.Id,
        Category = NamsRecallCategory.Entity,
        Content = entity.Description ?? entity.Name,
        Provenance = NamsRecallProvenance.ModelGenerated,
        CreatedAt = entity.CreatedAt
    };

    // Case-insensitive: the OpenAPI snapshot doesn't guarantee lowercase, and mismatching case here would
    // silently under-trust (never over-trust) a legitimate user/assistant message -- still worth avoiding.
    private static NamsRecallProvenance ProvenanceForRole(string role) => role.ToLowerInvariant() switch
    {
        "user" => NamsRecallProvenance.UserProvided,
        "assistant" => NamsRecallProvenance.ModelGenerated,
        "tool" => NamsRecallProvenance.ToolDerived,
        _ => NamsRecallProvenance.Untrusted // "system" or anything unrecognized -- the conservative default
    };
}
