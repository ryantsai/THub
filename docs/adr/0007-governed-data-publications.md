# ADR-0007: Govern generated REST APIs and online data editors

- Status: Proposed
- Date: 2026-07-22
- Deciders: Project maintainers

## Context

THub is expected to publish connected database data as REST APIs and permissioned online editors. A generic endpoint that accepts a connection/table name or arbitrary SQL would cross a major trust boundary and expose the worker/web database identities to injection, data leakage, and unauthorized writes.

Consumer identity, network exposure, row/column policy, and direct-versus-staged writes have not yet been confirmed.

## Decision

If accepted, model a publication as a reviewed, versioned resource containing:

- an approved connection and database object;
- an allow-list of readable and writable columns;
- separately granted read, insert, update, and delete operations;
- a named authentication/authorization policy;
- bounded filter, sort, projection, and pagination capabilities;
- optional row policy and field validation;
- rate/request limits, optimistic concurrency, audit, and revocation state.

Default REST publications to read-only. Prefer staged, auditable edits with approval over direct source-table writes. Never generate a generic arbitrary-table or arbitrary-SQL endpoint.

No publication runtime should be implemented until the unresolved identity and write-path choices are confirmed.

## Consequences

### Positive

- Makes publications explicit governed products rather than raw database proxies.
- Supports least privilege, revocation, auditing, and API documentation.
- Separates read-only REST risk from write-capable editor risk.

### Negative

- Requires significant metadata, policy, validation, audit, and API infrastructure.
- Row-level security and staged approval can be domain-specific.
- Direct table publication remains risky even with allow-lists.
- External consumers may require an authentication stack beyond Windows Authentication.

## Alternatives considered

- **Generic CRUD controller over configured tables:** rejected as unsafe and too difficult to govern.
- **Always write directly to source tables:** rejected as a default; it lacks approval and recovery boundaries.
- **Exclude publications entirely:** possible for v1 execution, but it does not meet the long-term product requirement.

## Follow-up

- Confirm consumer types and Windows vs Entra/JWT vs managed API-key authentication.
- Confirm internal-only vs partner/public network exposure.
- Confirm direct write vs staging/approval behavior.
- Define audit retention, row/column classification, and ownership before changing status to Accepted.
