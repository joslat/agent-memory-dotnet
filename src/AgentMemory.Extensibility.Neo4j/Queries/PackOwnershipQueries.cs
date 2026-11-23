// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Shared;

namespace AgentMemory.Extensibility.Neo4j.Queries;

/// <summary>
/// What a store holds of a pack id, read without the pack's manifest: the schema names the id owns in the SDK namespace ledger,
/// its own schema steps, and the records it wrote (PACK-REMOVE-MISSING-ARCHIVE).
/// </summary>
[Experimental("AMEXT001")]
internal static class PackOwnershipQueries
{
    /// <summary>The namespace claims (<c>namespace/{kind}/{name}</c>) a pack id owns.</summary>
    internal const string OwnedNames = "MATCH (m:" + ModuleBookkeepingSchema.Label + " {module_id:$sdk}) "
        + "WHERE m.owner_module_id=$pack AND m.key STARTS WITH 'namespace/' RETURN m.key AS key, m.state AS state ORDER BY key";

    /// <summary>A pack id's own schema steps.</summary>
    internal const string Steps = "MATCH (m:" + ModuleBookkeepingSchema.Label + " {module_id:$pack}) RETURN m.key AS key, m.sha256 AS sha256, m.state AS state ORDER BY key";

    /// <summary>The label a namespace key names, when it names one.</summary>
    internal const string LabelClaim = "namespace/label/";

    /// <summary>The live records a pack wrote under one of its labels, with what closing one checks.</summary>
    internal static string LiveRecords(string label) => "MATCH (n:`" + label + "`) WHERE n.source_pack=$pack AND n.invalidated_at IS NULL "
        + "RETURN n.id AS id, n.key_id AS keyId, n.key AS key, n.owner_id AS owner, n.superseded_by AS supersededBy, "
        + "coalesce(n.created_at > datetime($now), false) AS newer";

    /// <summary>Locks an existing version key and reads it; no row when the key is missing (a removal never creates one).</summary>
    internal const string LockHeldKey = "MATCH (k:" + ModuleBookkeepingSchema.KeyLabel + " {id:$keyId}) SET k.lock_token=true REMOVE k.lock_token RETURN properties(k) AS row";
}
