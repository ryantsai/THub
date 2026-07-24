# Data model

## Principles

- SQL Server is authoritative for metadata and run state.
- Published workflow versions are immutable.
- A run always references the exact version it was queued to execute.
- JSON stores versioned node settings and graph structure; indexed lifecycle/query fields remain relational columns.
- Secrets are never stored in graph or connection JSON. Persist a secret identifier/reference only.
- All timestamps are stored in UTC. Schedule evaluation also stores an explicit time-zone identifier.
- Development uses SQL Server LocalDB (`THub.Debug`) so the same SQL Server provider, schema, and migrations are exercised locally; published environments use an externally configured SQL Server instance.

The migration chain and runtime now implement the immutable workflow/run/step model required by [ADR-0010](adr/0010-durable-leased-workflow-execution.md), the durable Email model required by [ADR-0012](adr/0012-durable-email-alert-delivery.md), and the governed publication model required by [ADR-0011](adr/0011-isolated-governed-data-publications.md).

`TrustedActions` stores administrator-owned webhook or executable definitions, enablement,
creator/last-updater metadata, and an optional encrypted credential reference. Workflow
graphs contain only the trusted-action ID and a bounded non-secret webhook body.
`AccessResourceGrants` uses resource kind `TrustedAction` plus permission
`trusted-action.use` to authorize publication. Credential values remain in
`EncryptedConnectionCredentials`; the definition row never contains a password or token.

## Current relational model

The initial EF Core migration creates the `thub` schema and the foundation tables below. Reviewed forward migrations add the durable slices described later in this document.

### `thub.Workflows`

Represents the current workflow aggregate and draft graph.

| Field | Meaning |
| --- | --- |
| `Id` | Workflow identity |
| `Name`, `Description` | Human metadata |
| `Owner` | Windows identity recorded as owner |
| `Status` | Draft, Published, Paused, or Archived |
| `Version` | Candidate graph version; advances when a published graph is edited |
| `DraftRevision` | Optimistic concurrency revision for editable workflow state |
| `GraphJson` | Canonical schema-versioned mutable draft DAG, variables, functions, and node settings |
| `PublishedVersionId`, `PublishedVersionNumber` | Pointer to the active immutable snapshot |
| `CronExpression` | Standard five-field cron expression, when scheduled |
| `TimeZoneId` | Time zone used to interpret the schedule |
| `NextRunAtUtc` | Indexed next due occurrence |
| `CreatedAtUtc`, `UpdatedAtUtc` | Audit timestamps |
| `ArchivedAtUtc` | Archive timestamp when lifecycle state is Archived |

The mutable draft remains on this row for editing convenience, but a publish transaction inserts or selects a `WorkflowVersions` snapshot and updates the published pointer. Editing a published graph creates a new candidate version without changing the prior snapshot used by queued or historical runs.

### `thub.WorkflowVersions`

Represents one immutable published workflow graph.

| Field | Meaning |
| --- | --- |
| `Id` | Deterministic identity derived from workflow plus version number |
| `WorkflowId`, `Version` | Owning workflow and unique version number |
| `SchemaVersion` | Supported workflow document schema version |
| `GraphJson` | Canonical immutable DAG with typed variables, functions, and node-settings document |
| `Checksum` | SHA-256 integrity checksum over the stored graph JSON |
| `PublishedBy`, `PublishedAtUtc` | Publication actor and UTC time |

`(WorkflowId, Version)` is unique. Runs have a restrictive foreign key to this row; the Worker also verifies the deterministic identity, version tuple, and checksum before execution.

### `thub.WorkflowRuns`

Represents an execution request for a specific workflow version.

| Field | Meaning |
| --- | --- |
| `Id` | Run identity |
| `WorkflowId` | Parent workflow |
| `WorkflowVersionId`, `WorkflowVersion` | Exact immutable version identity and number frozen at enqueue time |
| `RetryOfRunId` | Prior run identity for a user-requested retry, otherwise null |
| `Status` | Queued, Running, Succeeded, Failed, or Cancelled |
| `TriggeredBy` | Scheduler or initiating identity/source |
| `ScheduledForUtc` | Logical Quartz occurrence for scheduled runs; null for manual/event runs |
| `QueuedAtUtc`, `StartedAtUtc`, `CompletedAtUtc` | Lifecycle timestamps |
| `AttemptCount` | Number of run-lease claims, including abandoned-run recovery |
| `LeaseOwner`, `LeaseExpiresAtUtc`, `LastHeartbeatAtUtc` | Current SQL-coordinated execution ownership |
| `CancellationRequestedAtUtc`, `CancellationRequestedBy` | Durable cancellation request |
| `ErrorJson`, `ErrorMessage` | Normalized code/category/retryability/safe summary plus compatibility summary; never secret or row data |

Workers atomically claim queued runs or running runs whose lease expired, prefer recovery work, and permit at most one unexpired running lease per workflow. Heartbeats and every durable step/terminal write verify the current lease. Run recovery restarts the immutable graph; it is not checkpoint/resume and may replay an ambiguous external effect.

Scheduled runs have a filtered unique index on `(WorkflowId, WorkflowVersion, ScheduledForUtc)`. This keeps Quartz recovery or repeated delivery idempotent for one logical occurrence without constraining manual runs, whose `ScheduledForUtc` is null.

### `thub.WorkflowStepRuns`

Represents one durable attempt to execute or skip one node in a run.

| Field | Meaning |
| --- | --- |
| `WorkflowRunId`, `NodeId`, `Attempt` | Unique run/node/attempt identity |
| `Status` | Queued, Running, Succeeded, Failed, Cancelled, or Skipped |
| `QueuedAtUtc`, `StartedAtUtc`, `CompletedAtUtc` | Attempt lifecycle |
| `RowsRead`, `RowsWritten`, `BatchesProcessed` | Durable progress counters |
| `BytesRead`, `BytesWritten` | Durable byte counters |
| `ErrorJson` | Bounded normalized safe execution error |

When an expired run is recovered, any still-running prior step for a node is failed as `execution.lease-recovered` and a new attempt number is created. Downstream nodes whose dependency did not succeed are recorded as skipped.

### `thub.Connections`

Represents an approved connector configuration.

| Field | Meaning |
| --- | --- |
| `Id` | Connection identity |
| `Name` | Unique display name |
| `Kind` | SQL Server, MySQL, PostgreSQL, Oracle, FTP, CSV file, or Excel file |
| `ConfigurationJson` | Non-secret, connector-versioned configuration |
| `CreatedBy`, `CreatedAtUtc` | Audit metadata |

Examples of safe configuration include database/server aliases, FTP host and transport mode, file bounds, sheet names, delimiters, an authentication kind, and a credential secret reference. SQL Server supports `Integrated` and `UserPassword`; MySQL, PostgreSQL, Oracle, and FTP use `UserPassword`. Raw passwords, tokens, or embedded connection strings containing credentials are not safe configuration.

### `thub.EncryptedConnectionCredentials`

Stores a referenced database or FTP username/password payload as authenticated
ciphertext. It is deliberately separate from `Connections.ConfigurationJson`.

| Field | Meaning |
| --- | --- |
| `SecretReference` | Primary key matching the non-secret reference in connection metadata |
| `KeyVersion` | External master-key version needed to decrypt this row |
| `Nonce` | Fresh 12-byte AES-GCM nonce |
| `Ciphertext` | Encrypted credential payload |
| `AuthenticationTag` | 16-byte AES-GCM authentication tag |
| `UpdatedAtUtc` | Last create/replace instant |

The reference and payload schema version are authenticated as associated data. The
database never stores a master key. A reference may intentionally be shared by multiple
connections; replacing it changes the credential resolved by all of them.

### `quartz.QRTZ_*`

Quartz.NET owns the operational scheduler tables in the `quartz` schema. They contain durable jobs, one-shot triggers, fired-trigger state, cluster check-ins, and locks. THub creates and upgrades these tables through reviewed migrations, while runtime scheduling code accesses them only through Quartz APIs. Application and reporting code must not write directly to these tables.

Quartz rows are operational projections, not the source of truth for workflow definitions or run history. Backups and disaster recovery should nevertheless include both schemas so persisted firing state stays aligned with THub schedule metadata. After recovery, reconciliation removes stale jobs and recreates missing jobs from published THub workflows.

## Workflow graph contract

`WorkflowGraph` contains typed `Variables`, expression-only `Functions`, `Nodes`, and `Edges`.

```json
{
  "schemaVersion": 2,
  "variables": [
    {
      "name": "region",
      "kind": "Literal",
      "dataType": "String",
      "value": "north",
      "schema": null,
      "object": null,
      "valueColumn": null,
      "filterColumn": null,
      "filterValue": null
    }
  ],
  "functions": [
    {
      "name": "normalize",
      "parameters": ["value"],
      "expression": "String(value).trim().toUpperCase()"
    }
  ],
  "nodes": [
    {
      "id": "customer-source",
      "kind": "SqlSource",
      "name": "Customers",
      "x": 80,
      "y": 120,
      "settings": {
        "connectionId": "...",
        "object": "dbo.Customers"
      }
    }
  ],
  "edges": []
}
```

The serializer requires the explicit `schemaVersion` envelope, rejects unknown/duplicate properties and unsupported versions, and canonicalizes saved documents. Each node retains a JSON `settings` object in the graph, but publish and Worker execution parse it through a strict kind-specific contract with required fields, bounds, allow-listed properties/operators/modes, graph-aware join inputs, and destination value bindings. Schema version 1 is intentionally unsupported because the application has not been released.

Workflow package schema version 1 wraps the current editable metadata, schedule, graph,
and non-secret connection identity hints. Import creates a new draft identity and does not
copy immutable versions or runs. Archived workflows retain their relational history.
Permanent deletion is restricted transactionally to unpublished drafts without versions,
runs, or alert rules.

## Durable persistence slices

These concepts are governed by accepted ADRs. Each subsection states whether its current persistence is implemented; unresolved invariants must not be collapsed into opaque UI state.

### Durable workflow execution (implemented)

- `WorkflowVersions`: immutable schema-versioned graph JSON, checksum, publisher, and publication timestamp. A workflow stores its mutable draft and active published-version pointer separately.
- `WorkflowRuns`: exact version foreign key/number, retry origin, cancellation state, attempt count, lease owner/expiry/heartbeat, normalized error, and row-version concurrency.
- `WorkflowStepRuns`: node identity, attempt, state, timing, row/batch/byte counters, normalized error, and row-version concurrency.

An atomic SQL claim plus a per-workflow application lock owns one run until its lease expires and prevents another unexpired run for that workflow from starting. Lease ownership is checked before recording step or terminal state. Error columns never contain secrets, SQL values, connection strings, or row payloads. The current model has no step-output checkpoint or staging table; bounded replayable intermediates live only in Worker memory for the active attempt.

### Governed publications (implemented)

- `Publications`: stable identity and normalized route slug, publication kind/state, active-version pointer, owner/audit fields, and row-version concurrency.
- `PublicationVersions`: immutable approved relational read connection/object identity, optional distinct Worker apply connection, schema fingerprint, source kind, concurrency mode, paging/editor/rate/concurrency/timeout/response bounds, creator, and creation timestamp. The selected deterministic key is represented by ordered key columns.
- `PublicationColumns`: source name/type, public alias/type/nullability/length metadata, ordinal, readable/filterable/sortable/writable flags, ordered key metadata, generated/concurrency flags, and numeric bounds.
- `PublicationColumnForeignKeys`: one administrator-approved owned mapping per governed local column, including constraint/group ordinal, referenced object/column, explicitly selected display/search columns, and lookup mode. Inspector suggestions are not persisted by default. A shared constraint name and column count group composite mappings whose readable, writable, and nullable policy is atomic.
- `AccessRoles`, `AccessRolePermissions`, and `AccessRoleAssignments`: SQL-backed system/custom roles, bounded global permissions, and Windows user/group membership.
- `AccessResourceGrants`: role plus workflow or connection identifier and one resource-valid permission.
- `PublicationGrants`: editor publication plus SQL-backed role identifier with separate View, Insert, Update, Delete, and Approve capabilities. Publication-management permission is not a data grant.
- `PublicationAccessTokens`: publication scope, random selector, display prefix, verifier algorithm/version and one-way verifier, label, expiry/revocation metadata, `AcceptedRequestCount`, `LastUsedAtUtc`, and row-version concurrency. The current active version is resolved and rechecked on every request, so a token follows version activation. Plaintext bearer secrets are never persisted.
- `PublicationChangeSets` and `PublicationChanges`: publication/version identity, submitting/reviewing/applying actors and timestamps, approval/apply state, bounded key/before/after JSON, bounded outcome detail, heartbeat/update time, and row-version concurrency. The current claim treats `ApplyStartedBy` plus `UpdatedAtUtc` as apply ownership/heartbeat state.

REST metering performs one conditional atomic update after authentication, route/publication authorization, and process-local admission. It increments `AcceptedRequestCount` and monotonically advances `LastUsedAtUtc` while rechecking that the token, publication, and expected active version remain usable. The count is an admitted credential use, so it remains incremented if the later source query fails. If this update cannot be committed, serving data fails closed.

SQL Server, MySQL, PostgreSQL, and Oracle publication source tables require a stable selected key; discovery prioritizes the primary key and otherwise may select a safe non-null unique constraint or index. SQL Server apply uses source `rowversion` when available; an explicitly approved original-value comparison is the fallback. Writable versions require a distinct `ApplyConnectionId` with the same provider and database endpoint as `ConnectionId`; referenced username/password credentials must use different encrypted credential references. Web and REST reads resolve only `ConnectionId`, while the Worker revalidates and mutates through `ApplyConnectionId`. Non-generated key values are insertable but never updateable. Each executable approved change set is applied in one source transaction; concurrent changes, duplicate keys, and foreign-key violations become conflicts rather than overwrites. Foreign-key lookup metadata is discovered and frozen with the publication version, while the source constraint remains the final apply-time authority. A stale ambiguous `Applying` set is marked failed for operator reconciliation rather than automatically replayed across the source/control-plane transaction boundary.

### Durable Email delivery

The current migration chain and EF model create three Email-related tables:

- `EmailDeliveryProfiles`: administrator-owned, non-secret SMTP host/port, approved sender, required transport-security mode, recipient-domain policy, delivery limits, enabled state, audit timestamps, optional credential secret reference, and row-version concurrency. The reference is a lookup key, never a user name or password.
- `WorkflowAlertRules`: workflow/profile foreign keys, terminal-event flags, recipients, enabled/audit state, and a bounded subject/body template serialized as a value object. There is no separate `EmailTemplates` table.
- `AlertDeliveries`: the durable outbox row for either a workflow rule or an `EmailAlert` node. It stores the source/run/rule-or-node and originating-step identity, a unique deduplication key, stable message identity, bounded message payload, status, maximum/current attempt counts, next/last attempt timestamps, completion time, lease/heartbeat fields, provider message ID, bounded normalized last error, and row-version concurrency.

The composite delivery index covers status, next-attempt time, and lease expiry for due-work claims; the deduplication key is unique. Running-run terminal transitions and direct queued-run cancellation commit with their rule deliveries in one THub transaction. The commit rechecks the complete enabled matching rule set and each prepared rule/profile revision. An `EmailAlert` node uses a stable run/node deduplication key and reports success once its delivery row is durable; a recovered step attempt therefore observes the existing intent instead of creating a second delivery.

V1 has no `AlertDeliveryAttempts` history table. Each attempt updates the aggregate delivery's attempt count, timestamps, retry/dead-letter state, optional provider identity, and latest safe error. Current structured logs report batch totals and batch exceptions rather than one event per attempt, so they are not an authoritative per-attempt history. The MailKit adapter uses the stable THub MIME Message-ID but currently leaves `ProviderMessageId` empty. The outbox is at-least-once: a crash after SMTP acceptance but before the delivered transition commits can duplicate a message.

### Audit and retention

- A future `AuditEvents` stream would record actor or token identity, action, subject, correlation, timestamp, result, and bounded redacted details for privileged changes, token lifecycle, publication access, editor submission/approval/apply, and Email administration/delivery. It is not mapped today; publication/token/change-set rows retain their own actor, counter, status, and timestamp metadata instead.

PD-009 still blocks final retention periods and before/after value classification. No table may use the absence of a retention decision as permission to store credentials, bearer-token text, unrestricted row data, or full Email bodies.

## EF Core conventions

- Use LocalDB only under the Development environment. Do not introduce a second EF provider or divergent debug schema.
- Migrations live under `src/THub.Infrastructure/Persistence/Migrations`.
- Mapping belongs in `THubDbContext` or dedicated infrastructure configurations.
- Domain entities do not reference EF Core attributes.
- Use explicit string lengths, conversions, indexes, delete behaviors, and SQL types for large JSON/text.
- Do not call `EnsureCreated` for application startup. Apply reviewed migrations operationally.
- Avoid using EF's in-memory provider to validate SQL behavior; prefer SQL Server integration tests or SQLite only where relational semantics are sufficient.
