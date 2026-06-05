// Migration 0003 — relationship owner-scope index (R1 / IC2, multi-user isolation).
//
// Adds the owner_id relationship-property index on the RELATED_TO edge so the owner filter applied
// during scoped relationship reads (GetByEntity / GetBySourceEntity / GetByTargetEntity) is cheap.
// Fresh deployments already pick this up via SchemaBootstrapper; this migration brings existing
// databases to parity.
//
// NON-BACKFILLING BY DESIGN: pre-existing RELATED_TO edges keep owner_id = NULL = shared/global, so
// the upgrade is lossless. Newly extracted relationships are stamped with the owner from the
// extraction request (PersistenceStage). Idempotent (IF NOT EXISTS), own transaction.

CREATE INDEX rel_owner_idx IF NOT EXISTS FOR ()-[r:RELATED_TO]-() ON (r.owner_id);
