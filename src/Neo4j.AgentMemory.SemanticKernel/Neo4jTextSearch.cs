using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel.Data;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

#pragma warning disable SKEXP0001

namespace Neo4j.AgentMemory.SemanticKernel;

/// <summary>
/// Implements SK <see cref="ITextSearch{TRecord}"/> backed by <see cref="IMemoryService"/>.
/// Each instance is scoped to a single session.
/// </summary>
public sealed class Neo4jTextSearch : ITextSearch<TextSearchResult>
{
    private readonly IMemoryService _memoryService;
    private readonly string _sessionId;

    /// <summary>Initializes a new instance of <see cref="Neo4jTextSearch"/>.</summary>
    public Neo4jTextSearch(IMemoryService memoryService, string sessionId)
    {
        _memoryService = memoryService;
        _sessionId = sessionId;
    }

    /// <inheritdoc/>
    public async Task<KernelSearchResults<string>> SearchAsync(
        string query,
        TextSearchOptions<TextSearchResult>? searchOptions = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RecallAsync(query, cancellationToken).ConfigureAwait(false);
        var formatted = Neo4jMemoryPlugin.FormatRecallResult(result);
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
                new RecallRequest { SessionId = _sessionId, Query = query }, ct).ConfigureAwait(false);
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
