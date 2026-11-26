using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// RVW-CONVERSATION-OWNER-BACKFILL: conversations written before the message paths stamped an owner have none, so the
/// owner erase (which finds messages through their conversation's owner) misses them and an owner-scoped MCP caller no
/// longer sees them. This gives such a conversation its owner where the store can tell it: the memories extracted from
/// its messages all belong to one owner. A conversation with no owned memory, or with memories of several owners, is
/// left without an owner and reported. A dry run by default; idempotent (a conversation that has an owner is never touched).
/// </summary>
public sealed class Neo4jConversationOwnerBackfill(INeo4jTransactionRunner tx)
{
    /// <summary>Conversations looked at per transaction.</summary>
    public const int PageSize = 500;

    /// <summary>Finds every ownerless conversation's owner and, when <paramref name="apply"/> is true, stamps the ones with exactly one.</summary>
    public async Task<ConversationOwnerBackfillReport> RunAsync(bool apply, CancellationToken cancellationToken = default)
    {
        var attributed = new List<ConversationOwnerEvidence>();
        var withoutOwner = new List<string>();
        var severalOwners = new List<ConversationOwnerEvidence>();
        var after = string.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = apply
                ? await tx.WriteAsync(async runner =>
                {
                    var found = await EvidenceAsync(runner, after).ConfigureAwait(false);
                    var claims = found.Where(e => e.Owners.Count == 1)
                        .Select(e => new Dictionary<string, object?> { ["id"] = e.ConversationId, ["owner"] = e.Owners[0] }).ToList();
                    if (claims.Count > 0)
                    {
                        var cursor = await runner.RunAsync(OwnerDataQueries.StampConversationOwners,
                            new Dictionary<string, object?> { ["claims"] = claims }).ConfigureAwait(false);
                        await cursor.ConsumeAsync().ConfigureAwait(false);
                    }
                    return found;
                }, cancellationToken).ConfigureAwait(false)
                : await tx.ReadAsync(runner => EvidenceAsync(runner, after), cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            foreach (var evidence in page)
            {
                switch (evidence.Owners.Count)
                {
                    case 0: withoutOwner.Add(evidence.ConversationId); break;
                    case 1: attributed.Add(evidence); break;
                    default: severalOwners.Add(evidence); break;
                }
            }
            after = page[^1].ConversationId;
        }
        return new ConversationOwnerBackfillReport
        {
            Applied = apply, Attributed = attributed.AsReadOnly(), WithoutOwner = withoutOwner.AsReadOnly(),
            SeveralOwners = severalOwners.AsReadOnly(),
        };
    }

    private static async Task<List<ConversationOwnerEvidence>> EvidenceAsync(IAsyncQueryRunner runner, string after)
    {
        var cursor = await runner.RunAsync(OwnerDataQueries.OwnerlessConversationEvidence,
            new Dictionary<string, object?> { ["after"] = after, ["limit"] = PageSize }).ConfigureAwait(false);
        return (await cursor.ToListAsync().ConfigureAwait(false))
            .Select(r => new ConversationOwnerEvidence(r["id"].As<string>(),
                r["owners"].As<List<object>>().Select(o => o.As<string>()).Order(StringComparer.Ordinal).ToList().AsReadOnly()))
            .ToList();
    }
}

/// <summary>An ownerless conversation and the owners of the memories extracted from its messages.</summary>
public sealed record ConversationOwnerEvidence(string ConversationId, IReadOnlyList<string> Owners);

/// <summary>What <see cref="Neo4jConversationOwnerBackfill"/> found and, when applied, stamped.</summary>
public sealed record ConversationOwnerBackfillReport
{
    /// <summary>Whether the attributed conversations were stamped (false: a dry run).</summary>
    public required bool Applied { get; init; }

    /// <summary>Conversations whose memories all belong to one owner: stamped with that owner when applied.</summary>
    public required IReadOnlyList<ConversationOwnerEvidence> Attributed { get; init; }

    /// <summary>Conversations with no owned memory extracted from them: left without an owner.</summary>
    public required IReadOnlyList<string> WithoutOwner { get; init; }

    /// <summary>Conversations with memories of several owners: left without an owner.</summary>
    public required IReadOnlyList<ConversationOwnerEvidence> SeveralOwners { get; init; }
}
