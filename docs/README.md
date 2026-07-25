# Documentation

Start with **[getting-started.md](getting-started.md)** to install and run.

| Doc | What it covers |
|-----|----------------|
| [getting-started.md](getting-started.md) | Prerequisites, install, DI config, first memory store, multi-tenant / multi-database setup |
| [architecture.md](architecture.md) | Packages, layers, boundaries, dependency rules, the Neo4j graph model |
| [agent-framework.md](agent-framework.md) | Using AgentMemory with the Microsoft Agent Framework — the `AIContextProvider` lifecycle, memory tools, identity/scoping |
| [schema.md](schema.md) | Neo4j schema — node labels, relationship types, indexes, temporal and owner semantics |
| [specification.md](specification.md) | Current specification — product identity, package set, architecture, and requirements |
| [neo4j-memory-ecosystem.md](neo4j-memory-ecosystem.md) | Compatibility with upstream `neo4j-labs/agent-memory` — schema-parity/schema-check tooling, TCK conformance, and the review process behind releases |
| [performance/README.md](performance/README.md) | What a turn costs — the two phases (recall before the model, ingestion after), what is and isn't measured, tuning levers, and how to reproduce the numbers |
| [security/threat-model.md](security/threat-model.md) | Threat table — attack vectors, mitigations, residual risk, and test coverage for each; read before production use |
| [security/production-checklist.md](security/production-checklist.md) | Pre-production hardening checklist |
