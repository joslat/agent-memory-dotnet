CREATE CONSTRAINT identity_evidence_id IF NOT EXISTS FOR (a:IdentityEvidenceAssertion) REQUIRE a.id IS UNIQUE;
CREATE INDEX identity_evidence_lookup IF NOT EXISTS FOR (a:IdentityEvidenceAssertion) ON (a.lookup_key);
CREATE INDEX identity_evidence_surface IF NOT EXISTS FOR (a:IdentityEvidenceAssertion) ON (a.namespace, a.surface);
CREATE INDEX identity_calendar_key IF NOT EXISTS FOR (e:Entity) ON (e.identity_calendar_key);
