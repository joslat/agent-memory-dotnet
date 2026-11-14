// REV2-2b: recall for people reaches an owner's people through an index on (owner, type) instead of walking every entity
// the owner has, and keeps the names bound facts used for an owned person on that person (identity_surfaces), so the read
// no longer walks every binding. Binding adds a name as it binds, and this backfills what is already bound. A shared person's
// bound names stay per owner on the bindings and are never written onto the shared node.
CREATE INDEX entity_owner_type IF NOT EXISTS FOR (e:Entity) ON (e.owner_id, e.type);
CREATE INDEX entity_owner_key_type IF NOT EXISTS FOR (e:Entity) ON (e.owner_key, e.type);
MATCH (b:IdentityFactBinding)-[:RESOLVES_TO]->(t:Entity) WHERE t.owner_id IS NOT NULL AND b.owner_id = t.owner_id AND b.identity_observed_surface IS NOT NULL WITH t, collect(DISTINCT b.identity_observed_surface) AS bound SET t.identity_surfaces = bound;
