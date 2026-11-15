// REV2-2b: recall for people reaches an owner's people through an index on (owner, type) instead of walking every entity
// the owner has. The names bound facts used are kept on an owned entity (identity_surfaces) by binding itself, with no
// backfill here: a person with none kept is read through its bindings, and its next binding starts it from all of them.
// A shared person's bound names stay per owner on the bindings and are never written onto the shared node.
CREATE INDEX entity_owner_type IF NOT EXISTS FOR (e:Entity) ON (e.owner_id, e.type);
CREATE INDEX entity_owner_key_type IF NOT EXISTS FOR (e:Entity) ON (e.owner_key, e.type);
