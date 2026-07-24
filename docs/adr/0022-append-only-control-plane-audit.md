# ADR-0022: Persist an append-only control-plane audit trail

- Status: Accepted
- Date: 2026-07-24
- Deciders: Project maintainers

## Context

THub already persists actor and lifecycle fields on workflows, runs, tokens, deliveries,
and staged publication changes, but those rows are current operational state rather than
one searchable, retained history. Privileged management and automated Worker activity
cross Web, Publications, Worker, EF Core, and direct SQL paths. Audit storage must not
become a second payload log or expose secrets, row values, configuration, SQL, paths,
headers, Email content, or bearer material.

PD-009 has not selected retention periods or a final classification for staged
before/after values. That prevents an automatic purge policy but does not prevent a
conservative metadata-only append-only trail.

## Decision

Add `thub.AuditRecords` as an append-only metadata stream. Each record contains:

- UTC occurrence time;
- user, system, or managed-token actor kind and a bounded safe identifier;
- originating host;
- stable machine-readable action and outcome;
- resource type and optional stable resource/correlation identifiers.

EF-backed inserts, state transitions, updates, and deletes append audit records in the
same control-plane transaction. Direct SQL paths append records in their own authoritative
transaction for workflow-run claims, managed-token accepted uses, and publication
change-set claim/apply outcomes. Pure lease renewals and aggregate node progress are
operational telemetry, not separate audit actions; their meaningful lifecycle transitions
remain audited and their detailed durable state remains in the owning run, step, delivery,
or change-set table.

The audit table accepts inserts only. EF rejects tracked update/delete attempts and a SQL
Server trigger rejects direct updates or deletes. Database owners and backup operators
remain outside this application-level tamper boundary, so deployment access and database
backup controls are still required.

Only the `audit.view` permission can open or query the searchable, paged Blazor audit
viewer. The page policy and application query service both enforce it. System
Administrators receive every permission implicitly; the permission is not included in
Developer defaults.

Audit records never contain changed values or arbitrary details. Failed validation or
authorization attempts that do not create an authoritative control-plane transition are
security telemetry rather than durable action records until a separate request/security
event policy defines their availability and failure semantics.

No automatic deletion is implemented until PD-009 defines retention. Operators must
capacity-plan the table and protect it as control-plane data.

## Consequences

### Positive

- Completed management and runtime state changes have one durable searchable history.
- EF-backed actions cannot commit without their matching audit rows.
- The separate Publications and Worker direct-SQL paths do not silently bypass auditing.
- Payload-safe fixed columns make viewer and retention work predictable.

### Negative

- Audit writes add storage and transaction work to every meaningful control-plane change.
- The stream is not cryptographically chained and cannot protect against a SQL database owner.
- Failed or denied requests without a durable state transition are not yet a complete
  security-event stream.
- Retention remains manual until PD-009 is resolved.

## Alternatives considered

- **Serilog files only:** rejected because local logs are not the authoritative SQL control
  plane and have host-specific retention.
- **Database triggers on every control-plane table:** rejected because they cannot reliably
  attach application actors and would create noisy records for progress/lease internals.
- **Store before/after JSON:** rejected because it would duplicate untrusted configuration,
  secrets, and row payloads into a broad-read audit surface.
- **Wait for retention policy:** rejected because a bounded append-only metadata stream can
  be implemented without guessing a purge period.
