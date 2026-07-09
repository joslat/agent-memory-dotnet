# Documentation

Start with **[ROADMAP.md](ROADMAP.md)** for the overarching plan and current status, or
**[getting-started.md](getting-started.md)** to install and run.

## Current reference
| Doc | What it covers |
|-----|----------------|
| [ROADMAP.md](ROADMAP.md) | **Start here** — overarching plan, status, shipped capabilities, pending work, road to 1.0 |
| [getting-started.md](getting-started.md) | Prerequisites, install, DI config, first memory store, multi-tenant / multi-database setup |
| [core/](core/) | Canonical current docs: philosophy, requirements, design, specification, implementation plan, compatibility automation, ADRs, summaries |
| [architecture.md](architecture.md) | Packages, layers, boundaries, dependency rules, the Neo4j graph model |
| [design.md](design.md) | Domain model, design decisions, the service-interface and repository catalogs |
| [schema.md](schema.md) | Neo4j schema — node labels, relationship types, indexes, temporal and owner semantics |
| [specification.md](specification.md) | Short current specification entry point; detailed spec lives in `core/specification.md` |

## Planning & tracking
| Doc | What it covers |
|-----|----------------|
| [nextsteps.md](nextsteps.md) | Historical task-tracking table and prioritization rationale |
| [Improvement-Ideas-Backlog.md](Improvement-Ideas-Backlog.md) | Current deferred ideas with stale shipped items removed |
| [Memory_Review_and_Implementation_Plan.md](Memory_Review_and_Implementation_Plan.md) | Detailed historical implementation plan (multi-tenant isolation deep-dive) — kept as deep reference |
| [core/implementation-plan-golden-path-compatibility.md](core/implementation-plan-golden-path-compatibility.md) | Active implementation plan for the golden-path sample and compatibility automation |
| [core/behavioral-compatibility-pack-status.md](core/behavioral-compatibility-pack-status.md) | Live tracker for the behavioral compatibility pack and verification evidence |

## Subfolders
| Folder | What's inside |
|--------|---------------|
| [reviews/](reviews/) | Completed adversarial-review records (cycles 1–6 + capstone, point-in-time) |
| [reference/](reference/) | Upstream / parity reference — Python schema snapshots, the upstream PR how-to, MAF migration guides |
| [archive/](archive/) | Superseded plans and now-implemented design discussions — read-only history |
