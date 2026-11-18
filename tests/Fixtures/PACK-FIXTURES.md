# Frozen pack fixtures

These files keep public CI independent of the private strategy checkout.

- MartialArts.Legacy: frozen old module-bundle inputs for migration, authentication and compatibility tests.
- MartialArts.V12: frozen native agentmemory.pack/1.2 files for direct build/load/query tests.

They are test data, not production authoring or a pack distribution. Active sources and reusable archives live in the private strategy/ampack/packs catalogue. Signing keys are generated per test; legacy fixture signatures written by the acceptance harness are ignored.
