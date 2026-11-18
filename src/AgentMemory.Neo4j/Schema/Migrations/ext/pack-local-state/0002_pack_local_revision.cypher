CREATE CONSTRAINT pack_local_revision_id IF NOT EXISTS FOR (n:PackLocalRevision) REQUIRE n.id IS UNIQUE;
CREATE INDEX pack_local_revision_scope IF NOT EXISTS FOR (n:PackLocalRevision) ON (n.mount_id,n.created_at);
