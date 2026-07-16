using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel.Data;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Security;
using AgentMemory.Core.Services;

#pragma warning disable SKEXP0001

namespace AgentMemory.SemanticKernel;

/// <summary>
/// Implements SK <see cref="ITextSearch{TRecord}"/> backed by <see cref="IMemoryService"/>.
/// Each instance is scoped to a single session and, optionally, to a single owner (R1): when
/// <c>userId</c> is supplied the recall is confined to that owner's plus shared memory; when null the
/// recall is unscoped (returns all owners) — set it per-user in multi-tenant hosts to avoid cross-owner reads.
/// </summary>
/// <remarks>
/// #92 Phase 6: <see cref="GetTextSearchResultsAsync"/>/<see cref="GetSearchResultsAsync"/> previously built
/// <see cref="TextSearchResult"/>s directly from raw entity/fact/preference text -- a second, still-open
/// recall-to-text surface found in the same holistic audit that originally flagged this whole package,
/// alongside <see cref="SearchAsync"/> (which already inherited protection for free via
/// <see cref="MemoryContextFormatter.FormatRecallResult"/>). Each item is now delimited
/// (<see cref="RecalledMemoryDelimiter"/>, #92 Phase 1) and evaluated for instruction-like content with a
/// trust-level bypass (#92 Phase 2/3), matching <c>MemoryContextFormatter</c>'s per-item granularity.
/// Recalled conversation history (<c>RecentMessages</c>/<c>RelevantMessages</c>) is intentionally NOT
/// delimited or evaluated here either, matching the disclosed scope everywhere else in #92.
/// </remarks>
public sealed class Neo4jTextSearch : ITextSearch<TextSearchResult>
{
    private readonly IMemoryService _memoryService;
    private readonly string _sessionId;
    private readonly string? _userId;
    private readonly MemoryRecallSecurityOptions _security;

    /// <summary>Initializes a new instance of <see cref="Neo4jTextSearch"/>.</summary>
    /// <param name="memoryService">The backing memory service.</param>
    /// <param name="sessionId">Session to recall within.</param>
    /// <param name="userId">Optional owner/user id (R1). Null ⇒ unscoped recall (all owners).</param>
    /// <param name="securityOptions">
    /// Optional security options (#92 Phase 6) governing <see cref="GetTextSearchResultsAsync"/>/
    /// <see cref="GetSearchResultsAsync"/>'s per-item admission; defaults to
    /// <see cref="MemoryContextSecurityMode.Permissive"/> when omitted.
    /// </param>
    public Neo4jTextSearch(
        IMemoryService memoryService, string sessionId, string? userId = null,
        MemoryRecallSecurityOptions? securityOptions = null)
    {
        _memoryService = memoryService;
        _sessionId = sessionId;
        _userId = userId;
        _security = securityOptions ?? new MemoryRecallSecurityOptions();
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
        return new KernelSearchResults<TextSearchResult>(BuildTextSearchResults(result.Context, _security, cancellationToken), result.TotalItemsRetrieved);
    }

    /// <inheritdoc/>
    public async Task<KernelSearchResults<TextSearchResult>> GetSearchResultsAsync(
        string query,
        TextSearchOptions<TextSearchResult>? searchOptions = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RecallAsync(query, cancellationToken).ConfigureAwait(false);
        return new KernelSearchResults<TextSearchResult>(BuildTextSearchResults(result.Context, _security, cancellationToken), result.TotalItemsRetrieved);
    }

    private async Task<RecallResult> RecallAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            return await _memoryService.RecallAsync(
                new RecallRequest { SessionId = _sessionId, UserId = _userId, Query = query }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield return value;
    }

    private static async IAsyncEnumerable<TextSearchResult> BuildTextSearchResults(
        MemoryContext ctx,
        MemoryRecallSecurityOptions security,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var msg in ctx.RecentMessages.Items.Concat(ctx.RelevantMessages.Items))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TextSearchResult(msg.Content) { Name = msg.Role };
        }
        foreach (var entity in ctx.RelevantEntities.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = entity.Description ?? entity.Name;
            if (!Admit(text, entity.Metadata.GetTrustLevel(), security)) continue;
            yield return new TextSearchResult(RecalledMemoryDelimiter.Wrap("entities", text)) { Name = entity.Name };
        }
        foreach (var fact in ctx.RelevantFacts.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = $"{fact.Subject} {fact.Predicate} {fact.Object}";
            if (!Admit(text, fact.Metadata.GetTrustLevel(), security)) continue;
            yield return new TextSearchResult(RecalledMemoryDelimiter.Wrap("facts", text)) { Name = fact.Subject };
        }
        foreach (var pref in ctx.RelevantPreferences.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = pref.PreferenceText;
            if (!Admit(text, pref.Metadata.GetTrustLevel(), security)) continue;
            yield return new TextSearchResult(RecalledMemoryDelimiter.Wrap("preferences", text)) { Name = pref.Category };
        }
    }

    private static bool Admit(string content, MemoryTrustLevel trustLevel, MemoryRecallSecurityOptions security) =>
        RecalledMemoryAdmission.ShouldAdmit(
            content, trustLevel, security.MinimumTrustForAdmissionBypass, security.SecurityMode == MemoryContextSecurityMode.Strict);
}
