# ADR-0021: Extend governed publications across relational providers

- Status: Accepted
- Date: 2026-07-24
- Deciders: Project owner and maintainers
- Extends: [ADR-0011](0011-isolated-governed-data-publications.md)
- Supersedes in part: [ADR-0014](0014-expand-relational-and-ftp-connectors.md)

## Context

ADR-0011 implemented governed publications over SQL Server tables and views. ADR-0014
added MySQL, PostgreSQL, and Oracle workflow connectors but deliberately kept
publications SQL Server-only pending provider-specific validation and authorization
work.

Developers need one publication workflow across every supported database connection:
discover a table or view, analyze keys and foreign-key relationships, review the public
contract, assign human or application access, activate an immutable version, and hand
consumers a stable editor or REST URL. REST consumers also need an OpenAPI contract and
interactive Swagger documentation.

A lowest-common-denominator SQL or ODBC implementation would weaken identifier,
metadata, type, paging, and concurrency guarantees. Provider behavior must remain
explicit.

## Decision

Support governed publications for the four relational connection kinds already
accepted by THub:

- SQL Server;
- MySQL;
- PostgreSQL;
- Oracle Database.

Keep SQL Server as the THub control plane. A publication version continues to reference
an approved `DataConnection`; the connection kind selects a provider-specific
publication dialect and adapter at execution time. Do not persist arbitrary SQL.

Each provider adapter must implement the same bounded contracts for:

- table and view discovery;
- typed column, stable key, generated/concurrency-column, and foreign-key inspection;
- canonical schema fingerprinting;
- quoted reviewed identifiers and parameterized values;
- typed filters and deterministic keyset paging;
- bounded foreign-key lookup and label resolution;
- editor insert, update, and delete with provider-appropriate optimistic concurrency.

Provider adapters may expose different concurrency capabilities. SQL Server may use
`rowversion`; other providers default to reviewed original-value comparison unless a
provider-native immutable concurrency token is explicitly modeled. Views remain
read-only.

Writable versions reference two approved connections. `ConnectionId` is the read
connection used for discovery and all Web or Publications-host reads.
`ApplyConnectionId` is a distinct Worker apply connection used only when an approved
change set is applied. Both must use the same provider and database endpoint. When
username/password authentication is used, their encrypted credential references must
also be distinct. Read-only versions cannot declare an apply connection. This resolves
PD-010 without making a write credential available to a browser circuit or REST read
path.

The management UI uses one staged publication builder for both editor and REST kinds:

1. surface and route;
2. source discovery;
3. relationship and column contract review;
4. limits and access;
5. activate and hand off.

Consumer routes use the stable publication slug. Human editor access remains
Windows-role governed. REST consumer access remains managed opaque bearer tokens.

The isolated publication host exposes a generic OpenAPI v1 document and Swagger UI.
The generic document describes authentication, routes, filters, sorts, paging, and
response envelopes without disclosing a publication's protected column contract.
The authenticated `/schema` route remains authoritative for the active publication
contract. Swagger must not persist bearer tokens.

## Consequences

### Positive

- Developers use one governed publishing model across supported databases.
- Provider differences remain explicit and reviewable.
- Stable slugs and Swagger improve consumer handoff without exposing connection or
  object identifiers.
- Existing token, role, version, schema-drift, and bounded-query controls remain intact.

### Negative

- Four metadata and query dialects require provider contract and live-database tests.
- Key and generated-column discovery differs by database version and granted metadata
  permissions.
- Original-value concurrency can produce wider predicates than a native version token.
- Deployment identities or stored credentials must grant each host exact per-provider
  metadata/read/write rights.
- Administrators must provision and rotate a separate apply connection for every
  writable source boundary.

## Rejected alternatives

- **Generic arbitrary SQL publication:** rejected because it bypasses reviewed object,
  column, relationship, and paging policy.
- **ODBC-only adapter:** rejected because it hides provider-specific metadata,
  identifier, type, and concurrency behavior.
- **Expose a token in a Swagger URL:** rejected because URLs leak through history,
  logs, and referrers.
- **Publish per-publication schemas anonymously:** rejected because column metadata
  remains part of the protected publication contract.

## Required validation

- Provider contract tests for discovery, types, keys, foreign keys, filters, sorts,
  cursor continuation, null ordering, schema drift, and response bounds.
- Editor apply tests for inserts, updates, deletes, constraints, and concurrency on
  every writable provider.
- Publication-host tests for the OpenAPI document, Swagger UI, bearer authorization,
  and absence of token persistence.
- Browser tests for the staged builder, role assignment, activation, and consumer
  handoff at desktop and narrow layouts.
