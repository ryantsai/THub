# THub documentation

## Architecture set

- [Architecture overview](architecture.md): system context, runtime containers, THub/Quartz ownership, flows, failure model, observability, and roadmap.
- [Data model](data-model.md): THub and Quartz schemas, workflow graph contract, scheduled-run identity, target persistence concepts, and migration conventions.
- [Security architecture](security.md): authentication, authorization, trust boundaries, scheduler/log data, connectors, executables, and publications.
- [Deployment and operations](deployment.md): recommended Windows topology, service/database deployment, Quartz and Serilog configuration, health, and recovery.
- [Architecture Decision Records](adr/README.md): accepted and proposed decisions with rationale and consequences.

## Product planning

- [Open product decisions](product-decisions.md): questions requiring owner confirmation before implementation can safely proceed.

## Maintenance rules

- Update the focused architecture document when implementation or operational assumptions change.
- Add/supersede an ADR for a material technology, boundary, security, persistence, or execution decision.
- Keep planned and implemented behavior distinguishable.
- Prefer links to one authoritative explanation over duplicating instructions across documents.
