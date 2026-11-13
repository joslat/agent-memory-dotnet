namespace AgentMemory.Neo4j.Schema.Extensions;

/// <summary>Explicit host evidence and decision-owned reversible fact-role support.</summary>
internal sealed class IdentityEvidenceSchemaExtension : ISchemaExtension
{
    public string Id => "identity-evidence";
    public int Version => 4;
    public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Entity"] = new HashSet<string>(["identity_evidence_version", "identity_calendar_key", "identity_observation_key"], StringComparer.Ordinal),
            ["IdentityEvidenceAssertion"] = new HashSet<string>(["id", "owner_id", "assertion_kind", "namespace", "authority", "source_id", "owner_scope",
                "payload", "lookup_key", "surface", "subject_kind", "calendar_key", "created_at"], StringComparer.Ordinal),
            ["IdentityDecision"] = new HashSet<string>(["id", "owner_id", "owner_scope", "identity_observation_key", "identity_record_json", "identity_target_snapshot",
                "identity_revision", "identity_lock_version", "created_at"], StringComparer.Ordinal),
            ["IdentityDecisionRevision"] = new HashSet<string>(["id", "owner_id", "owner_scope", "identity_record_json", "identity_revision", "created_at"], StringComparer.Ordinal),
            ["IdentityFactBinding"] = new HashSet<string>(["id", "owner_id", "owner_scope", "identity_decision_id", "identity_fact_role", "identity_observed_surface",
                "identity_source_ids", "created_at"], StringComparer.Ordinal),
            ["IdentityRelationshipObservation"] = new HashSet<string>(["id", "owner_id", "owner_scope", "identity_record_json",
                "identity_source_ids", "identity_source_decision_id", "identity_target_decision_id", "identity_lock_version", "created_at", "valid_until", "relation_type", "confidence", "description", "attributes"], StringComparer.Ordinal),
            ["IdentityEvidenceScopeLock"] = new HashSet<string>(["owner_scope", "owner_id", "identity_lock_version"], StringComparer.Ordinal)
        };
    public IReadOnlySet<string> DeclaredLabels { get; } = new HashSet<string>(
        ["IdentityEvidenceAssertion", "IdentityDecision", "IdentityDecisionRevision", "IdentityFactBinding", "IdentityEvidenceScopeLock", "IdentityRelationshipObservation"], StringComparer.Ordinal);
    public IReadOnlySet<string> DeclaredRelationshipTypes { get; } = new HashSet<string>(
        ["HAS_IDENTITY_EVIDENCE", "HAS_IDENTITY_BINDING", "DECIDED_BY", "RESOLVES_TO", "HAS_IDENTITY_REVISION", "POSSIBLE_SAME_AS", "SOURCE_IDENTITY", "TARGET_IDENTITY"], StringComparer.Ordinal);
    public IReadOnlyList<string> MigrationScripts { get; } = ["0001_identity_evidence.cypher", "0002_identity_decisions.cypher", "0003_identity_owner_stamps.cypher", "0004_identity_relationship_observations.cypher"];
    public IReadOnlySet<string> BaseResidentMigrations { get; } = new HashSet<string>(StringComparer.Ordinal);
    public SchemaParityDelta ParityDelta { get; } = SchemaParityDelta.Create(
        addNetOnlyLabels: ["IdentityEvidenceAssertion", "IdentityDecision", "IdentityDecisionRevision", "IdentityFactBinding", "IdentityEvidenceScopeLock", "IdentityRelationshipObservation"],
        addNetOnlyRelationshipTypes: ["HAS_IDENTITY_EVIDENCE", "HAS_IDENTITY_BINDING", "DECIDED_BY", "RESOLVES_TO", "HAS_IDENTITY_REVISION", "POSSIBLE_SAME_AS", "SOURCE_IDENTITY", "TARGET_IDENTITY"],
        addNetSupersetProperties: ["identity_evidence_version", "identity_calendar_key", "assertion_kind", "namespace", "authority", "source_id", "owner_scope",
            "payload", "lookup_key", "surface", "subject_kind", "calendar_key", "identity_observation_key", "identity_record_json", "identity_target_snapshot",
            "identity_revision", "identity_lock_version", "identity_decision_id", "identity_fact_role", "identity_observed_surface", "identity_source_ids", "identity_source_decision_id", "identity_target_decision_id"]);
    public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(StringComparer.Ordinal);
    public TckProfileDescriptor TckProfile => TckProfileDescriptor.None;
}
