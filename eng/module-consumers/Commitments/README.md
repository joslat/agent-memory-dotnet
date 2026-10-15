# Commitments & Follow-ups

An experimental example memory package consuming AgentMemory.Extensibility through NuGet only.
It stores owner-scoped CommitmentItem versions anchored to an existing core Entity. Open, completed
and cancelled are domain states; closing a commitment never deletes its history.

Teach with a persisted **user** message containing one JSON object:

```json
{"format":"agentmemory.commitment/v1","key":"draft","title":"Send Atlas draft","status":"open","priority":2,"due_at":"2026-10-19T16:00:00Z","about":{"kind":"identity","target":"atlas"}}
```

The host registers CommitmentProvider explicitly through module ingestion. The provider copies the
actual source message ID and a bounded substring. The SDK validates properties, persisted membership,
owner scope, target existence, migrations and versioning; this package does not access a database.
Assistant output and ordinary user prose create no proposals. Unknown/duplicate fields, malformed
teaching objects and unsupported target modes refuse the provider batch. Property errors are left to
SDK proposal admission. Bounds: 64 source messages per call, 16 KiB UTF-8 per object, JSON depth 8.
Source excerpts are at most 500 UTF-16 code units without cutting a surrogate pair. Optional absent
fields are omitted; explicit nulls reach SDK validation rather than silently erasing stored values.

Views and contributors share the SDK renderer and scoped reader. ForEntity takes a host-selected
entity identity; History takes a logical key. A missing selection does no read. Due uses the host
clock/system-time pin. The package does not route arbitrary natural-language questions automatically.

V1 is 1.0.0. V2 is 2.0.0 and adds optional indexed priority_reason (maximum 200 characters). The host
must explicitly apply the generated additive schema migration; no old reason is invented or backfilled.
Use the same package identity for both. No dynamic loading, signing or runtime graduation is implied
by compiling this example. The projects are not published by the repository release manifest.

Build through the repository's module-consumer verification procedure with a freshly packed SDK feed.
No ProjectReference or friend access is permitted. With ModuleSdkVersion unset, projects are inert
for source-discovery tools; that state cannot execute the consumer checks.
