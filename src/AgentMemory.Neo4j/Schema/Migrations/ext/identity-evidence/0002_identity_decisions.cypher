CREATE CONSTRAINT identity_decision_id IF NOT EXISTS FOR (d:IdentityDecision) REQUIRE d.id IS UNIQUE;
CREATE CONSTRAINT identity_decision_observation IF NOT EXISTS FOR (d:IdentityDecision) REQUIRE (d.owner_scope, d.identity_observation_key) IS UNIQUE;
CREATE CONSTRAINT identity_revision_id IF NOT EXISTS FOR (r:IdentityDecisionRevision) REQUIRE r.id IS UNIQUE;
CREATE CONSTRAINT identity_binding_id IF NOT EXISTS FOR (b:IdentityFactBinding) REQUIRE b.id IS UNIQUE;
CREATE CONSTRAINT identity_scope_lock IF NOT EXISTS FOR (s:IdentityEvidenceScopeLock) REQUIRE s.owner_scope IS UNIQUE;
CREATE INDEX identity_binding_decision IF NOT EXISTS FOR (b:IdentityFactBinding) ON (b.owner_scope, b.identity_decision_id);
