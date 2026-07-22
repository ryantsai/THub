# THub documentation

## Architecture set

- [Architecture overview](architecture.md): management, publication, worker, and SQL boundaries; current-versus-target flows; failure model; observability; and roadmap.
- [Data model](data-model.md): current THub/Quartz schemas plus accepted immutable workflow, lease/step, publication/token/grant/change-set, Email outbox, and audit persistence targets.
- [Security architecture](security.md): Windows management authentication, isolated managed-bearer publications, Spreadsheet role grants/staging, connectors, Email secrets/outbox, and gated executables/webhooks.
- [Deployment and operations](deployment.md): separate Web/Publications IIS boundaries, worker/database deployment, Quartz and Serilog configuration, least-privilege identities, health, and recovery.
- [Architecture Decision Records](adr/README.md): accepted, proposed, and superseded decisions with rationale and consequences, including ADR-0010 through ADR-0012.

## Product planning

- [Open product decisions](product-decisions.md): remaining owner decisions plus the authoritative records for resolved publication, editor, and execution choices.

## Maintenance rules

- Update the focused architecture document when implementation or operational assumptions change.
- Add/supersede an ADR for a material technology, boundary, security, persistence, or execution decision.
- Keep planned and implemented behavior distinguishable.
- Prefer links to one authoritative explanation over duplicating instructions across documents.
