// Explicit upgrade of identity records predating native owner stamps.
// Only absent stamps are filled. Existing ownership is never silently reassigned.
MATCH (n:IdentityEvidenceAssertion) WHERE n.owner_scope STARTS WITH 'owned:' AND n.owner_id IS NULL SET n.owner_id = substring(n.owner_scope, 6);
MATCH (n:IdentityDecision) WHERE n.owner_scope STARTS WITH 'owned:' AND n.owner_id IS NULL SET n.owner_id = substring(n.owner_scope, 6);
MATCH (n:IdentityDecisionRevision) WHERE n.owner_scope STARTS WITH 'owned:' AND n.owner_id IS NULL SET n.owner_id = substring(n.owner_scope, 6);
MATCH (n:IdentityFactBinding) WHERE n.owner_scope STARTS WITH 'owned:' AND n.owner_id IS NULL SET n.owner_id = substring(n.owner_scope, 6);
MATCH (n:IdentityEvidenceScopeLock) WHERE n.owner_scope STARTS WITH 'owned:' AND n.owner_id IS NULL SET n.owner_id = substring(n.owner_scope, 6);
