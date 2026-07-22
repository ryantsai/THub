# ADR-0009: Use Quartz.NET scheduling and Serilog observability

- Status: Accepted
- Date: 2026-07-22
- Deciders: Project maintainers
- Supersedes: [ADR-0004](0004-sql-server-control-plane-and-windows-worker.md)

## Context

THub needs durable, time-zone-aware scheduling that survives worker restarts and can eventually coordinate multiple scheduler instances. The initial polling loop calculated due work in THub code and required a serializable scan of the workflow table. THub must still own immutable workflow versions, run identity, run/step state, authorization, and future execution leases; a scheduling library must not become the product's workflow model.

Both hosts also need consistent structured logs with bounded local retention and configurable production destinations while application code continues to depend on `ILogger<T>`.

## Decision

Keep SQL Server as the control plane and `THub.Worker` as the Windows Service boundary. Use Quartz.NET 3.18 as the scheduling substrate with its clustered ADO.NET job store in the `quartz` SQL schema.

- A Quartz reconciliation job synchronizes published THub schedule metadata to one durable Quartz job and one one-shot trigger per workflow. It detects definition changes and removes schedules for paused, draft, archived, or deleted workflows.
- Quartz owns persistence, timing, misfire detection, and scheduler cluster coordination. The reconciliation job does not scan for due workflows.
- THub retains its standard five-field cron contract and uses Cronos to calculate the next occurrence. A one-shot Quartz trigger represents that occurrence, avoiding incompatible cron dialect semantics.
- A missed one-shot trigger fires once when the scheduler recovers. The next occurrence is then calculated from the current evaluation time, so downtime does not produce an unbounded catch-up storm.
- A Quartz fire calls an application port that verifies the workflow is still published at the expected version, inserts a THub-owned `WorkflowRun`, and advances `NextRunAtUtc`.
- `WorkflowRun.ScheduledForUtc` records the logical occurrence. A filtered unique index on workflow ID, version, and scheduled time makes replay/recovery idempotent for that occurrence.
- Quartz job data contains only workflow identifiers, version, cron text, and time-zone ID. It never contains workflow graphs, credentials, connection configuration, or row payloads.
- Quartz tables are created through the reviewed THub EF migration; neither runtime host mutates the schema at startup.

Use Serilog as the provider behind `ILogger<T>` in both hosts. Write human-readable console output and daily rolling JSON files with a 50 MB per-file limit and 14-file retention default. The default deployed path is under `%PROGRAMDATA%\THub\Logs`; Development writes beneath each project content root. Production may replace or supplement the sink through an approved collector, but logs are not stored in the THub control-plane tables.

## Consequences

### Positive

- Schedule timing, persistence, misfires, shutdown, and cluster coordination use a maintained scheduling component.
- THub run/version semantics remain independent of Quartz.
- Scheduled occurrence identity prevents duplicate run records during scheduler recovery.
- The due-workflow table scan and custom worker loop are removed.
- Structured host, framework, request, scheduler, and application logs share one provider and enrichment pipeline.

### Negative

- The control-plane database gains vendor-managed Quartz tables and migration obligations.
- Schedule metadata exists in THub while operational trigger state exists in Quartz; reconciliation and monitoring are required.
- Schedule changes can take up to the configured reconciliation interval to appear in Quartz.
- Scheduler clustering requires synchronized clocks and correct SQL connectivity.
- Quartz clustering does not solve workflow execution concurrency. Run claim/lease semantics remain required before multiple workers execute queued runs.
- File log directories require explicit write permissions and retention monitoring.

## Alternatives considered

- **Continue the custom polling loop:** rejected because Quartz provides the required durable timing, misfire, lifecycle, and clustering behavior.
- **Make Quartz own workflow/run state:** rejected because THub needs immutable versions, step telemetry, authorization, and execution semantics outside a job scheduler's model.
- **Use Quartz cron expressions as the product contract:** rejected for now because the existing five-field cron contract has different day-of-week and day-of-month semantics.
- **Hangfire:** not selected because THub needs scheduling infrastructure rather than another job/run dashboard and persistence model.
- **Write logs directly to SQL Server:** rejected because operational log volume and retention must not contend with authoritative control-plane state.

## Follow-up

- Add run claims, leases, attempts, and abandoned-run recovery before enabling multi-worker execution.
- Add scheduler readiness, lag, misfire, and reconciliation-failure metrics.
- Establish centralized log collection, access, retention, and redaction policy for production.
- Review Quartz schema upgrade scripts before every Quartz major-version upgrade.
