CREATE CONSTRAINT identity_relationship_observation_id IF NOT EXISTS
FOR (o:IdentityRelationshipObservation) REQUIRE o.id IS UNIQUE;
CREATE INDEX identity_relationship_observation_owner IF NOT EXISTS
FOR (o:IdentityRelationshipObservation) ON (o.owner_scope, o.id);
CREATE INDEX identity_relationship_legacy_id IF NOT EXISTS
FOR ()-[r:RELATED_TO]-() ON (r.id);
