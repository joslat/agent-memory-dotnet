using System.Collections.Frozen;

namespace AgentMemory.Neo4j.Schema.Extensions;

internal sealed class PackLocalStateSchemaExtension : ISchemaExtension
{
    public string Id => "pack-local-state";
    public int Version => 3;
    public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["PackLocalMount"] = new[] { "id", "application_id", "owner_id", "pack_id", "base_digest", "base_version", "write_protected", "automatic_learning", "active", "revision", "lock_token" }.ToFrozenSet(StringComparer.Ordinal),
            ["PackLocalChange"] = new[] { "id", "mount_id", "type", "key", "actor", "base_digest", "payload", "created_at", "invalidated_at", "valid_until" }.ToFrozenSet(StringComparer.Ordinal),
            ["PackLocalRevision"] = new[] { "id", "mount_id", "base_digest", "base_version", "write_protected", "automatic_learning", "active", "revision", "created_at", "invalidated_at", "valid_until" }.ToFrozenSet(StringComparer.Ordinal),
            ["PackLocalGate"] = new[] { "id", "lock_token" }.ToFrozenSet(StringComparer.Ordinal),
        }.ToFrozenDictionary(StringComparer.Ordinal);
    public IReadOnlySet<string> DeclaredLabels { get; } = new[] { "PackLocalMount", "PackLocalChange", "PackLocalRevision", "PackLocalGate" }.ToFrozenSet(StringComparer.Ordinal);
    public IReadOnlySet<string> DeclaredRelationshipTypes { get; } = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    public IReadOnlyList<string> MigrationScripts { get; } = ["0001_pack_local_state.cypher", "0002_pack_local_revision.cypher", "0003_pack_local_gate.cypher"];
    public IReadOnlySet<string> BaseResidentMigrations { get; } = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    public SchemaParityDelta ParityDelta { get; } = SchemaParityDelta.Create(addNetOnlyLabels: ["PackLocalMount", "PackLocalChange", "PackLocalRevision", "PackLocalGate"],
        addNetSupersetProperties: ["application_id", "pack_id", "base_digest", "base_version", "write_protected", "automatic_learning", "active", "revision", "lock_token", "mount_id", "type", "key", "actor", "payload", "valid_until"]);
    public IReadOnlySet<string> DependsOn { get; } = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    public TckProfileDescriptor TckProfile => TckProfileDescriptor.None;
}
