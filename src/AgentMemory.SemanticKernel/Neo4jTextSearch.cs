using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel.Data;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;

#pragma warning disable SKEXP0001

namespace AgentMemory.SemanticKernel;

/// <summary>
/// Implements SK <see cref="ITextSearch{TRecord}"/> backed by <see cref="IMemoryService"/>.
/// Each instance is scoped to a single session and, optionally, to a single owner (R1): when
/// <c>userId</c> is supplied the recall is confined to that owner's plus shared memory; when null the
/// recall is unscoped (returns all owners) — set it per-user in multi-tenant hosts to avoid cross-owner reads.
/// </summary>
public sealed class Neo4jTextSearch : ITextSearch<TextSearchResult>
{
    private readonly IMemoryService _memoryService;
    private readonly string _sessionId;
    private readonly string? _userId;

    /// <summary>Initializes a new instance of <see cref="Neo4jTextSearch"/>.</summary>
    /// <param name="memoryService">The backing memory service.</param>
    /// <param name="sessionId">Session to recall within.</param>
    /// <param name="userId">Optional owner/user id (R1). Null ⇒ unscoped recall (all owners).</param>
    public Neo4jTextSearch(IMemoryService memoryService, string sessionId, string? userId = null)
    {
        _memoryService = memoryService;
        _sessionId = sessionId;
        _userId = userId;
    }

    /// <inheritdoc/>
    public async Task<KernelSearchResults<string>> SearchAsync(
        string query,
        TextSearchOptions<TextSearchResult>? searchOptions = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RecallAsync(query, cancellationToken).ConfigureAwait(false);
        var formatted = MemoryContextFormatter.FormatRecallResult(result);
        var items = string.IsNullOrEmpty(formatted)
            ? AsyncEnumerable.Empty<string>()
            : YieldSingle(formatted, cancellationToken);
        return new KernelSearchResults<string>(items, result.TotalItemsRetrieved);
    }

    /// <inheritdoc/>
    public async Task<KernelSearchResults<TextSearchResult>> GetTextSearchResultsAsync(
        string query,
        TextSearchOptions<TextSearchResult>? searchOptions = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RecallAsync(query, cancellationToken).ConfigureAwait(false);
        return new KernelSearchResults<TextSearchResult>(BuildTextSearchResults(result.Context, cancellationToken), result.TotalItemsRetrieved);
    }

    /// <inheritdoc/>
    public async Task<KernelSearchResults<TextSearchResult>> GetSearchResultsAsync(
        string query,
        TextSearchOptions<TextSearchResult>? searchOptions = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RecallAsync(query, cancellationToken).ConfigureAwait(false);
        return new KernelSearchResults<TextSearchResult>(BuildTextSearchResults(result.Context, cancellationToken), result.TotalItemsRetrieved);
    }

    private async Task<RecallResult> RecallAsync(string query, CancellationToken ct)
    {
        try
        {
            return await _memoryService.RecallAsync(
                new RecallRequest { SessionId = _sessionId, UserId = _userId, Query = query }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Honor cancellation — do not mask it as an empty result.
            throw;
        }
        catch
        {
            return new RecallResult
            {
                Context = new MemoryContext { SessionId = _sessionId, AssembledAtUtc = DateTimeOffset.UtcNow },
                TotalItemsRetrieved = 0,
            };
        }
    }

    private static async IAsyncEnumerable<string> YieldSingle(
        string value,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield return value;
    }

    private static async IAsyncEnumerable<TextSearchResult> BuildTextSearchResults(
        MemoryContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var msg in ctx.RecentMessages.Items.Concat(ctx.RelevantMessages.Items))
        {
            ct.ThrowIfCancellationRequested();
            yield return new TextSearchResult(msg.Content) { Name = msg.Role };
        }
        foreach (var entity in ctx.RelevantEntities.Items)
        {
            ct.ThrowIfCancellationRequested();
            yield return new TextSearchResult(entity.Description ?? entity.Name) { Name = entity.Name };
        }
        foreach (var fact in ctx.RelevantFacts.Items)
        {
            ct.ThrowIfCancellationRequested();
            yield return new TextSearchResult($"{fact.Subject} {fact.Predicate} {fact.Object}") { Name = fact.Subject };
        }
        foreach (var pref in ctx.RelevantPreferences.Items)
        {
            ct.ThrowIfCancellationRequested();
            yield return new TextSearchResult(pref.PreferenceText) { Name = pref.Category };
        }
    }
}
