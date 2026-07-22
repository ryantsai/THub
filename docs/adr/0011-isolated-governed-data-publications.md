# ADR-0011: Isolate governed REST publications and stage editor writes

- Status: Accepted
- Date: 2026-07-23
- Deciders: Project maintainers
- Supersedes: [ADR-0007](0007-governed-data-publications.md)

## Context

THub must expose approved workflow result tables to other applications through REST and provide a role-controlled browser editor. The management application uses Windows Authentication and is normally deployed with IIS anonymous access disabled. Bearer-only clients cannot reach such an application reliably, and enabling anonymous transport on the management host weakens isolation.

A generic table or SQL proxy would expose broad database authority. Editor writes also require explicit column policy, validation, foreign-key handling, concurrency control, and audit.

## Decision

Create a separate `THub.Publications` ASP.NET Core host and hostname for REST traffic. It runs behind HTTPS with anonymous IIS transport so ASP.NET Core can authenticate the request, uses a separate least-privilege identity, and has no management or Windows-authenticated endpoints. Initial deployments are internal-network only. No CORS origins are enabled by default.

A publication is a reviewed, versioned resource. Its immutable active version contains an approved SQL Server connection and table or view, schema fingerprint, public column aliases, readable/filterable/sortable/writable flags, deterministic pagination key, row and page bounds, and operation policy. Routes contain only a stable publication slug, never connection, schema, table, or SQL text. REST v1 is read-only.

REST v1 uses managed opaque bearer tokens:

- tokens contain a random public selector and a 256-bit random secret;
- only `Authorization: Bearer` is accepted, and the full token is returned once;
- THub stores the selector, algorithm version, display prefix, and one-way verifier, never plaintext;
- each publication may have multiple independently named, expiring, revocable tokens for rotation;
- unknown, malformed, expired, revoked, and wrong-publication credentials return the same generic challenge;
- bearer identities cannot call management operations.

After authentication, resource authorization, and rate/concurrency admission, THub atomically increments that token's `AcceptedRequestCount` and updates `LastUsedAtUtc` while rechecking token and publication state. The request is counted even if the later source query fails. Metering failure fails closed so data is not served unmetered.

Use bounded typed filters and sorts, keyset pagination, request and SQL timeouts, cancellation, response-size limits, API Problem Details, schema-drift checks, and per-token/per-publication limits. Built-in rate-limit state is process-local; the initial topology therefore uses one publication host instance. Scale-out requires a gateway or distributed limiter decision.

Keep online editors in `THub.Web` under Windows Authentication. Each editor publication explicitly grants the global Viewer, Operator, Designer, and Administrator roles independent `View`, `Insert`, `Update`, `Delete`, and `Approve` capabilities. The management permission does not imply data access. Server-side resource authorization is authoritative and is re-evaluated for every load, lookup, submit, approval, and apply operation.

Use Radzen Blazor Spreadsheet as the bounded editing surface. Viewer sessions are read-only. Editor sessions permit value edits but disable structural, formatting, formula, image, chart, hyperlink, import/export, and validation-rule changes. Clipboard and autofill are disabled by default. A workbook window defaults to 250 rows and cannot exceed 1,000 rows. Server-side filtering and deterministic page/window navigation remain authoritative because the Spreadsheet workbook is in memory and has no database paging or row-save contract. Before submit, server code accepts the current edit, compares the workbook with its protected loaded snapshot, and revalidates keys, types, nullability, lengths, writable columns, formulas, row limits, and authorization.

Editor changes are staged in THub, audited with before/after values subject to classification policy, and applied only after approval. Source tables require a primary key. Apply uses a source `rowversion` when available; without one, the publication must explicitly opt into original-value comparison or remain read-only. Concurrent changes produce a conflict instead of being overwritten. Insert, update, and delete are separate publication capabilities and are disabled unless explicitly granted.

`THub.Web` may receive only the source read permissions needed to render approved editor columns and lookups. Approved write change sets are claimed and applied by `THub.Worker` using a separately configured least-privilege write identity; the Blazor circuit never receives write credentials.

SQL Server foreign-key metadata is discovered at publication validation. For a permitted small single-column foreign key whose stored and displayed values are identical, Spreadsheet list data validation may provide the dropdown. When key and display differ, THub registers a custom Spreadsheet cell editor backed by a bounded `RadzenDropDownDataGrid`; larger lookups use server filtering. The editor renders the friendly label but commits the referenced key, and the server revalidates it on submit. The display/search columns are configurable; a suitable text column may be suggested but is never silently persisted as policy. Composite keys use one logical multi-column lookup editor rather than independent dropdowns.

## Consequences

### Positive

- Bearer traffic is isolated from the Windows management surface and service identity.
- Multiple tokens support safe rotation and independent usage visibility.
- Publications cannot turn route input into arbitrary object or SQL access.
- Role grants, staging, validation, and concurrency checks prevent silent source-table overwrites.
- Foreign keys are edited using governed referenced values.

### Negative

- A new host, hostname, identity, deployment profile, and monitoring surface are required.
- Staged edits add an approval step and control-plane storage.
- Spreadsheet workbooks and foreign-key lists require strict row limits.
- Process-local rate limits constrain the initial deployment to one publication host instance.
- Tables without a stable primary key cannot be editable.

## Alternatives considered

- **Serve bearer and Windows traffic from `THub.Web`:** rejected because IIS authentication and workload isolation become fragile.
- **Use JWTs issued by THub:** rejected because THub does not need to become an identity provider for managed application credentials.
- **Generic CRUD over a configured table:** rejected because it cannot enforce reviewed object, column, operation, and row policy safely.
- **Write editor changes directly:** rejected as the default because approval, recovery, and complete audit are required for a general data-governance tool.

## Follow-up

- Decide access-audit retention and classification under PD-009.
- Add a future ADR before supporting internet exposure, Entra/JWT, multiple publication host instances, or direct writes.
- Give the publication host source credentials no broader than the active read-only publications.
