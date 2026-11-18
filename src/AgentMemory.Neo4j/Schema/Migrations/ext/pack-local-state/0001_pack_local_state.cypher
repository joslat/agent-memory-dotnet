CREATE CONSTRAINT pack_local_mount_id IF NOT EXISTS FOR (n:PackLocalMount) REQUIRE n.id IS UNIQUE;
CREATE CONSTRAINT pack_local_change_id IF NOT EXISTS FOR (n:PackLocalChange) REQUIRE n.id IS UNIQUE;
CREATE INDEX pack_local_change_scope IF NOT EXISTS FOR (n:PackLocalChange) ON (n.mount_id,n.type,n.key);
