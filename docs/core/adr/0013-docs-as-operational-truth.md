# ADR 0013 - Docs as Operational Truth

Status: Accepted

Date: 2026-07-09

## Context

The repository accumulated planning documents, review records, architecture notes, schema notes, and roadmap updates across many implementation phases. Some older documents described future work that is now shipped. Others referenced old package names, old interfaces, stale test counts, or planning-era limitations.

Users and maintainers need a clear documentation hierarchy so they can tell current truth from history.

## Decision

Treat docs as operational truth and maintain a clear hierarchy.

- `docs/core/` is the canonical concept/specification/ADR layer.
- `docs/ROADMAP.md` is the authoritative status and next-work document.
- `docs/architecture.md`, `docs/design.md`, and `docs/schema.md` are current reference docs.
- `docs/archive/` and `docs/reference/` are historical or external context unless current docs cite them explicitly.
- Test counts, release status, and parity claims must be dated.
- Shipped items must be removed from active backlog language.

## Consequences

Positive consequences:

- Users can find current truth quickly.
- Historical reasoning is preserved without being mistaken for current status.
- Architecture decisions are easier to audit.
- Future docs drift has a known place to be corrected.

Tradeoffs:

- Docs require periodic maintenance.
- Some historical docs will remain internally stale by design and must be labeled clearly.
- Current docs must be checked against code before release or major status updates.

## Alternatives Considered

### Delete old planning documents

Rejected. The historical plans and reviews contain useful context and audit trail.

### Keep all docs at the same authority level

Rejected. It caused stale planning claims to compete with current implementation truth.

### Put everything in one large document

Rejected. Different readers need different levels: concept, requirements, design, spec, ADRs, schema, roadmap.

## Verification Anchors

- `docs/core/` exists as the new canonical documentation folder.
- `docs/README.md` points to `docs/core/`.
- `docs/ROADMAP.md` points to `docs/core/` in the docs map.
- `docs/nextsteps.md` is labeled historical.
- `docs/Improvement-Ideas-Backlog.md` removes shipped items from active backlog language.
