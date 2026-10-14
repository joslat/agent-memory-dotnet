using AgentMemory.Shared;

namespace AgentMemory.Extensibility.Neo4j.Queries;

internal static class ModuleLedgerQueries
{
    internal const string Constraints = "SHOW CONSTRAINTS YIELD name, type, entityType, labelsOrTypes, properties RETURN name, type, entityType, labelsOrTypes, properties";
    internal const string Indexes = "SHOW INDEXES YIELD name, type, entityType, labelsOrTypes, properties, owningConstraint, state RETURN name, type, entityType, labelsOrTypes, properties, owningConstraint, state";
    internal const string Labels = "CALL db.labels() YIELD label RETURN label";
    internal const string Relations = "CALL db.relationshipTypes() YIELD relationshipType RETURN relationshipType";
    internal const string Ledger = "MATCH (m:" + ModuleBookkeepingSchema.Label + ") WHERE m.module_id IN $moduleIds OR m.module_id = $sdkId RETURN properties(m) AS row";
    internal const string CreateLedger = "CREATE CONSTRAINT " + ModuleBookkeepingSchema.ConstraintName
        + " IF NOT EXISTS FOR (m:" + ModuleBookkeepingSchema.Label + ") REQUIRE (m.module_id, m.key) IS UNIQUE";
    // Every compliant writer takes the same write lock before checking and reserving any namespace.
    // DDL runs outside this data transaction; matching durable intents make interruption resumable.
    internal const string Lock = "MERGE (m:" + ModuleBookkeepingSchema.Label + " {module_id:$sdkId, key:'reservation-lock'}) "
        + "ON CREATE SET m.state='lock' SET m.actor=$actor, m.started_at=$now RETURN m.key";
    internal const string Claim = "MERGE (m:" + ModuleBookkeepingSchema.Label + " {module_id:$sdkId, key:$key}) "
        + "ON CREATE SET m.owner_module_id=$owner, m.state='owned', m.actor=$actor, m.started_at=$now RETURN m.owner_module_id";
    internal const string Intent = "MERGE (m:" + ModuleBookkeepingSchema.Label + " {module_id:$moduleId, key:$key}) "
        + "ON CREATE SET m.sha256=$hash, m.state='applying', m.completed_statements=0, m.actor=$actor, m.started_at=$now RETURN m.sha256";
    internal const string Applied = "MATCH (m:" + ModuleBookkeepingSchema.Label + " {module_id:$moduleId, key:$key}) "
        + "WHERE m.sha256=$hash AND m.state='applying' AND m.completed_statements=$total SET m.state='applied', m.applied_actor=$actor, m.applied_at=$now RETURN m.key";
    internal const string Step = "MATCH (m:" + ModuleBookkeepingSchema.Label + " {module_id:$moduleId, key:$key}) RETURN properties(m) AS row";
    internal const string Progress = "MATCH (m:" + ModuleBookkeepingSchema.Label + " {module_id:$moduleId, key:$key}) "
        + "WHERE m.sha256=$hash AND m.state='applying' AND coalesce(m.completed_statements,0)=$before "
        + "SET m.completed_statements=$after RETURN m.key";

}
