using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// 26.1. A third task shape, built to satisfy the <b>fifth</b> validity rule that the second one failed.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the second task taught.</b> <see cref="ProceduralIncidentTask"/> satisfied all four stated
/// rules — enforced chain, non-inferable token source, refusal-only discovery, no chain-revealing
/// descriptions — and still did not discriminate: the control solved it cold in three tool calls, every
/// attempt. The gate was <i>"acquire a change window before deploying"</i>, which is standard practice
/// a model already knows, so there was no discovery cost to save.
/// </para>
/// <para>
/// <b>The fifth rule: the convention must be ARBITRARY, not merely ENFORCED.</b> A gate the model would
/// propose anyway is free to discover however strictly it is enforced.
/// </para>
/// <para>
/// So the gate here is <c>warm_cache</c> before retiring a record. Nothing connects warming a cache to
/// retiring data — it is not good practice, not a safety step, and not something a model would propose.
/// It is a quirk of one system, discoverable only by being refused, and that is exactly what a runbook
/// exists to record. The token source follows the rail task's shape: a <c>list_index_shards</c> call —
/// a name suggesting inventory — is the only thing that yields a retention token.
/// </para>
/// <para>
/// <b>Prediction, recorded before running it:</b> the control should need at least one extra call to
/// discover the cache requirement, because no competent model proposes it unprompted. If the control
/// again solves it cold, this task is invalid for the same reason the second one was, and the fifth
/// rule needs sharpening rather than the result being explained away.
/// </para>
/// </remarks>
internal sealed class ProceduralArchiveTask : IProceduralTask
{
    /// <summary>Marker the agent can only emit by completing the real chain.</summary>
    internal const string ConfirmationMarker = "RECORD-RETIRED";

    /// <summary>Every refusal starts with this, so "did this call work" is an exact test.</summary>
    internal const string RefusalPrefix = ProceduralBenchmarkTask.RefusalPrefix;

    private const string Record = "ds-4471";
    private const string RetentionToken = "RET-9902";
    private const string ArchiveReference = "ARC-5518";

    private bool _cacheWarmed;

    public List<string> Calls { get; } = [];

    public string Prompt =>
        $"Retire the stale dataset record '{Record}'. "
        + "Reply with the archive reference exactly as the tool returns it.";

    public bool IsComplete(string response) =>
        response.Contains(ConfirmationMarker, StringComparison.Ordinal);

    public IReadOnlyList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(ListIndexShards),
        AIFunctionFactory.Create(WarmCache),
        AIFunctionFactory.Create(RetireRecord),
        .. Decoys(),
    ];

    [Description("Lists index shards holding a dataset record.")]
    private string ListIndexShards(
        [Description("The record identifier.")] string record)
    {
        Calls.Add(nameof(ListIndexShards));
        // The retention token is buried in an otherwise unremarkable shard listing. Nothing in the
        // tool's name or signature says it is the source of a retention token.
        return $"shards for {record}: shard-02 (primary), shard-07 (replica), "
            + $"bytes 41220, retention={RetentionToken}";
    }

    [Description("Loads a dataset record into the read cache.")]
    private string WarmCache(
        [Description("The record identifier.")] string record)
    {
        Calls.Add(nameof(WarmCache));
        _cacheWarmed = true;
        return $"{record} loaded into the read cache";
    }

    [Description("Retires a dataset record to archive storage.")]
    private string RetireRecord(
        [Description("The record identifier.")] string record,
        [Description("The retention value.")] string retention)
    {
        Calls.Add(nameof(RetireRecord));

        // THE arbitrary convention. Nothing connects warming a read cache to retiring a record: it is
        // not good practice and not a safety step, so no model proposes it. It is a quirk of one
        // system, discoverable only by being refused -- which is what makes it worth remembering.
        if (!_cacheWarmed)
            return $"{RefusalPrefix} {record} is not resident in the read cache.";

        // The non-inferable dependency: the token exists, but only the shard listing yields it.
        if (!string.Equals(retention, RetentionToken, StringComparison.Ordinal))
            return $"{RefusalPrefix} retention value not recognised for {record}.";

        return $"{ConfirmationMarker} {Record} archive={ArchiveReference}";
    }

    /// <summary>
    /// Plausible tools that are never needed, so calling everything stops being free.
    /// </summary>
    private IEnumerable<AITool> Decoys() =>
        new (string Name, string Description)[]
        {
            ("get_record_schema", "Returns the schema of a dataset record."),
            ("list_downstream_consumers", "Lists jobs reading a dataset record."),
            ("get_storage_class", "Returns the storage class of a dataset record."),
            ("check_replication_lag", "Returns replication lag for a shard."),
            ("list_snapshots", "Lists snapshots of a dataset record."),
            ("get_access_log", "Returns recent access entries for a record."),
            ("check_legal_hold", "Returns whether a record is under legal hold."),
            ("get_record_size", "Returns the on-disk size of a record."),
            ("list_tags", "Lists tags applied to a dataset record."),
            ("get_owner_team", "Returns the owning team for a dataset record."),
            ("check_encryption", "Returns the encryption state of a record."),
            ("list_partitions", "Lists partitions of a dataset record."),
        }
        .Select(decoy => AIFunctionFactory.Create(
            (string query) =>
            {
                Calls.Add(decoy.Name);
                return $"{decoy.Name}: no action required for this retirement.";
            },
            decoy.Name,
            decoy.Description));
}
