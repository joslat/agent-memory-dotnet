# ADR 0001 - Independent Community Project

Status: Accepted

Date: 2026-07-09

## Context

The project is inspired by the Python `neo4j-labs/agent-memory` project and interoperates conceptually with the Neo4j graph-memory ecosystem. However, the repository is owned and implemented independently, uses .NET-specific architecture, and is licensed by its own maintainer.

The documentation previously mixed inspiration, interop, and product identity in ways that could imply a stronger upstream relationship than the code and repository actually have.

## Decision

Agent Memory for .NET is documented and packaged as an independent community .NET implementation.

It may:

- cite the Python project as inspiration,
- preserve compatible schema concepts,
- compare parity against upstream behavior,
- interoperate with Neo4j and Microsoft agent ecosystems.

It must not:

- present itself as an official Neo4j product,
- claim endorsement or support by Neo4j, Inc.,
- present itself as a fork of the Python project,
- imply that external upstream packages are runtime dependencies when the code has internalized equivalent behavior.

## Consequences

Positive consequences:

- Product identity is honest.
- Users understand support boundaries.
- The project can make .NET-native decisions without needing exact upstream symmetry.
- Documentation can distinguish inspiration from implementation truth.

Tradeoffs:

- The project must maintain its own compatibility story.
- Parity claims require explicit evidence and dates.
- Docs need care when referencing Neo4j Labs projects.

## Alternatives Considered

### Present as a direct port

Rejected. The code is not a line-by-line port and makes .NET-specific decisions around DI, package topology, MEAI, MAF, SK, MCP, and Neo4j query organization.

### Present as an official Neo4j integration

Rejected. The repository is independent and must not imply official support.

### Avoid mentioning upstream projects

Rejected. The Python project is important context, and schema/parity references help users understand the design lineage.

## Verification Anchors

- Repository root `README.md` states independent community identity.
- `LICENSE` is MIT.
- `Directory.Build.props` uses `PackageLicenseExpression` = `MIT`.
- Core documentation repeats the independence boundary in `docs/core/philosophy.md` and `docs/core/specification.md`.
