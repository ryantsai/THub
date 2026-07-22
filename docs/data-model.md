# Data model

## Principles

- SQL Server is authoritative for metadata and run state.
- Published workflow versions are immutable.
- A run always references the exact version it was queued to execute.
- JSON stores versioned node settings and graph structure; indexed lifecycle/query fields remain relational columns.
- Secrets are never stored in graph or connection JSON. Persist a secret identifier/reference only.
- All timestamps are stored in UTC. Schedule evaluation also stores an explicit time-zone identifier.
- Development uses SQL Server LocalDB (`THub.Debug`) so the same SQL Server provider, schema, and migrations are exercised locally; published environments use an externally configured SQL Server instance.

## Current relational model

The initial EF Core migration creates the `thub` schema and these tables.

### `thub.Workflows`

Represents the current workflow aggregate and draft graph.

| Field | Meaning |
| --- | --- |
| `Id` | Workflow identity |
| `Name`, `Description` | Human metadata |
| `Owner` | Windows identity recorded as owner |
| `Status` | Draft, Published, Paused, or Archived |
| `Version` | Current graph version number |
| `GraphJson` | Serialized workflow DAG/settings |
| `CronExpression` | Standard five-field cron expression, when scheduled |
| `TimeZoneId` | Time zone used to interpret the schedule |
| `NextRunAtUtc` | Indexed next due occurrence |
| `CreatedAtUtc`, `UpdatedAtUtc` | Audit timestamps |

The current aggregate is sufficient for the scheduling scaffold. Before production authoring, split immutable published versions into a separate `WorkflowVersions` table so a draft can change without overwriting the graph used by queued or historical runs.

### `thub.WorkflowRuns`

Represents an execution request for a specific workflow version.

| Field | Meaning |
| --- | --- |
| `Id` | Run identity |
| `WorkflowId` | Parent workflow |
| `WorkflowVersion` | Version frozen at enqueue time |
| `Status` | Queued, Running, Succeeded, Failed, or Cancelled |
| `TriggeredBy` | Scheduler or initiating identity/source |
| `ScheduledForUtc` | Logical Quartz occurrence for scheduled runs; null for manual/event runs |
| `QueuedAtUtc`, `StartedAtUtc`, `CompletedAtUtc` | Lifecycle timestamps |
| `ErrorMessage` | Bounded terminal summary; never secret or row data |

Claim/lease, attempt, and step-run fields are intentionally not invented yet. They require decisions about worker scale, retry, and cancellation semantics.

Scheduled runs have a filtered unique index on `(WorkflowId, WorkflowVersion, ScheduledForUtc)`. This keeps Quartz recovery or repeated delivery idempotent for one logical occurrence without constraining manual runs, whose `ScheduledForUtc` is null.

### `thub.Connections`

Represents an approved connector configuration.

| Field | Meaning |
| --- | --- |
| `Id` | Connection identity |
| `Name` | Unique display name |
| `Kind` | SQL Server, CSV file, or Excel file |
| `ConfigurationJson` | Non-secret, connector-versioned configuration |
| `CreatedBy`, `CreatedAtUtc` | Audit metadata |

Examples of safe configuration include database/server aliases, sheet names, delimiters, and a secret reference. Raw passwords, tokens, or embedded connection strings containing credentials are not safe configuration.

### `quartz.QRTZ_*`

Quartz.NET owns the operational scheduler tables in the `quartz` schema. They contain durable jobs, one-shot triggers, fired-trigger state, cluster check-ins, and locks. THub creates and upgrades these tables through reviewed migrations, while runtime scheduling code accesses them only through Quartz APIs. Application and reporting code must not write directly to these tables.

Quartz rows are operational projections, not the source of truth for workflow definitions or run history. Backups and disaster recovery should nevertheless include both schemas so persisted firing state stays aligned with THub schedule metadata. After recovery, reconciliation removes stale jobs and recreates missing jobs from published THub workflows.

## Workflow graph contract

`WorkflowGraph` contains `Nodes` and `Edges`.

```json
{
  "schemaVersion": 1,
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

The current C# model stores `SettingsJson` as an opaque versioned value. Persistence APIs must wrap the graph in an explicit `schemaVersion` envelope before import/export is implemented. Deserializers must reject unsupported future versions instead of silently dropping fields.

## Target tables

The next persistence slices are expected to introduce:

- `WorkflowVersions`: immutable graph snapshot, checksum, publisher, and publication timestamp.
- `WorkflowRunClaims`: owner, lease expiry, heartbeat, and concurrency token, or equivalent fields on `WorkflowRuns`.
- `WorkflowStepRuns`: node identity, attempt, state, timing, counters, checkpoint, and bounded error summary.
- `AuditEvents`: actor, action, subject, correlation, timestamp, and redacted details.
- `Publications` and `PublicationGrants`: approved source object, columns, operations, row policy, and authentication policy.

These are target concepts, not current schema promises. Add migrations and an ADR when their concurrency and retention behavior is chosen.

## EF Core conventions

- Use LocalDB only under the Development environment. Do not introduce a second EF provider or divergent debug schema.
- Migrations live under `src/THub.Infrastructure/Persistence/Migrations`.
- Mapping belongs in `THubDbContext` or dedicated infrastructure configurations.
- Domain entities do not reference EF Core attributes.
- Use explicit string lengths, conversions, indexes, delete behaviors, and SQL types for large JSON/text.
- Do not call `EnsureCreated` for application startup. Apply reviewed migrations operationally.
- Avoid using EF's in-memory provider to validate SQL behavior; prefer SQL Server integration tests or SQLite only where relational semantics are sufficient.
