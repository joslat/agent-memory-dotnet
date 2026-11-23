// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Shared;

namespace AgentMemory.Extensibility.Neo4j.Queries;

/// <summary>
/// What a store holds of a pack id, read without the pack's manifest: the schema names the id owns in the SDK namespace ledger,
/// its own schema steps, and the records it wrote (PACK-REMOVE-MISSING-ARCHIVE); and their transfer to a new id
/// (PACK-NAME-TAKEOVER).
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

    // A transfer of a removed pack id's names to a new id (PACK-NAME-TAKEOVER).

    /// <summary>How many records under one of the old id's labels there are, how many it wrote, how many of those are live, and how
    /// many an owner holds or another pack carried.</summary>
    internal static string Census(string label) => "MATCH (n:`" + label + "`) RETURN count(n) AS total, "
        + "count(CASE WHEN n.source_pack=$pack THEN 1 END) AS mine, "
        + "count(CASE WHEN n.source_pack=$pack AND n.invalidated_at IS NULL THEN 1 END) AS live, "
        + "count(CASE WHEN n.source_pack=$pack AND (n.owner_id IS NOT NULL OR n.source_carrier IS NOT NULL) THEN 1 END) AS scoped";

    /// <summary>How many of a pack id's version keys name a current record.</summary>
    internal const string LiveKeys = "MATCH (k:" + ModuleBookkeepingSchema.KeyLabel + ") WHERE k.module_id=$pack AND k.current_id IS NOT NULL RETURN count(k) AS n";

    /// <summary>Every record a pack id wrote under one label, with its relationships: type, direction and the node at the other end.</summary>
    internal static string Walk(string label) => "MATCH (n:`" + label + "`) WHERE n.source_pack=$pack OPTIONAL MATCH (n)-[r]-(m) "
        + "RETURN n.id AS id, collect(type(r) + CASE WHEN startNode(r)=n THEN '>' ELSE '<' END + coalesce(m.id, elementId(m))) AS edges, "
        + "collect(elementId(r)) AS relationships";

    /// <summary>A pack id's records, every version, attributed to the new id; each keeps the id that wrote it.</summary>
    internal static string Reattribute(string label) => "MATCH (n:`" + label + "`) WHERE n.source_pack=$from "
        + "SET n.source_pack=$to, n.source_pack_transferred_from=coalesce(n.source_pack_transferred_from, $from) RETURN count(n) AS n";

    /// <summary>How many erasure tombstones a pack id has.</summary>
    internal const string Tombstones = "MATCH (t:ModuleTombstone {pack:$pack}) RETURN count(t) AS n";

    /// <summary>A pack id's erasure tombstones, now blocking the new id's imports.</summary>
    internal const string MoveTombstones = "MATCH (t:ModuleTombstone {pack:$from}) SET t.pack=$to RETURN count(t) AS n";

    /// <summary>The namespace claims a pack id owns, now owned by the new id.</summary>
    internal const string MoveNames = "MATCH (m:" + ModuleBookkeepingSchema.Label + " {module_id:$sdk}) "
        + "WHERE m.owner_module_id=$from AND m.key STARTS WITH 'namespace/' SET m.owner_module_id=$to RETURN count(m) AS n";

    /// <summary>A pack id's applied schema steps, now recorded under the new id.</summary>
    internal const string MoveSteps = "MATCH (m:" + ModuleBookkeepingSchema.Label + " {module_id:$from}) SET m.module_id=$to RETURN count(m) AS n";

    /// <summary>
    /// The transfer's own row in the SDK ledger: its key names the old id, the new id and the time; it records the new owner and who
    /// asked. No migration step reads it.
    /// </summary>
    internal const string RecordTransfer = "CREATE (m:" + ModuleBookkeepingSchema.Label + " {module_id:$sdk, key:$key}) "
        + "SET m.state='transferred', m.owner_module_id=$to, m.actor=$actor, m.applied_actor=$actor, m.started_at=$now, m.applied_at=$now RETURN m.key AS key";
}
