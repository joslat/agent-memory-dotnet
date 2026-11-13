// Re-encode technical keys from the explicit owner_id, preserving that authoritative owner.
// Already mixed historical provenance cannot be reconstructed by this migration.
MATCH (n:Fact) WHERE n.owner_id STARTS WITH '*' AND n.owner_key = n.owner_id SET n.owner_key = '*' + n.owner_id;
MATCH (n:Entity) WHERE n.owner_id STARTS WITH '*' AND n.owner_key = n.owner_id SET n.owner_key = '*' + n.owner_id;
MATCH (n:EntitySummary) WHERE n.owner_id STARTS WITH '*' AND n.owner_key = n.owner_id SET n.owner_key = '*' + n.owner_id;
