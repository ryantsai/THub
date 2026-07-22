# ADR-0010: Use immutable workflow versions and leased durable execution

- Status: Accepted
- Date: 2026-07-23
- Deciders: Project maintainers

## Context

Quartz durably fires schedules and THub creates version-specific queued runs, but the current mutable `GraphJson` row cannot reproduce the version referenced by a run. There is also no claimant, execution planner, step state, cancellation, timeout, retry, or abandoned-run recovery model.

Workflow execution can affect databases and files. Worker failure can happen before or after an external system accepts an operation, so THub cannot promise exactly-once effects.

## Decision

Persist mutable drafts separately from immutable published workflow versions. A publish operation validates a schema-versioned graph, writes an immutable version with a checksum, and updates the workflow's published-version pointer in one SQL Server transaction. Every run has a foreign key to that exact version, and the worker deserializes and revalidates it before execution.

Use THub-owned SQL Server run leases independently of Quartz:

- workers atomically claim eligible queued runs with a lease owner, expiry, heartbeat, and attempt number;
- the default overlap policy queues occurrences but permits only one running instance of a workflow at a time;
- expired leases are recoverable, and lease ownership is checked before recording step or terminal state;
- cancellation is a durable request checked before planning, between batches and steps, and by every I/O operation;
- graceful shutdown stops new claims and observes the configured shutdown deadline.

Persist step attempts and normalized errors. Errors have a bounded safe summary, code, category, and retryability flag; secrets, connection strings, SQL values, and row payloads are never persisted as error details.

Execution is deterministic and topological. Node settings are typed and versioned, port/cardinality and connector policy are validated at publication, and the same validation runs again at the worker boundary. Tabular data crosses Application boundaries as asynchronous bounded batches. An implementation may buffer or spool replayable intermediate batches only within configured row, byte, memory, and temporary-storage limits.

Automatic retries are bounded and use exponential backoff with jitter. They are enabled only for failures classified as transient and executors declared retry-safe. Writes, Email actions, and other external side effects default to no automatic replay unless their idempotency policy explicitly permits it. A user retry creates a new run against the same immutable version and records the prior run identity.

THub remains at-least-once across external systems. Step and run records describe partial effects honestly and never claim exactly-once delivery.

## Consequences

### Positive

- Queued and historical runs remain reproducible after a draft changes.
- Multiple workers can recover abandoned work without concurrent ownership of one run.
- Operators receive durable step attempts, cancellation, retry, and safe failure information.
- Retry policy does not silently duplicate unsafe side effects.

### Negative

- Claims, heartbeats, attempts, and immutable versions add tables, indexes, and cleanup obligations.
- Long-running connector calls must cooperate with cancellation and lease renewal.
- Recovery can replay an operation whose external outcome was ambiguous.
- Replayable joins and branches require bounded memory or temporary storage.

## Alternatives considered

- **Execute directly as a Quartz job:** rejected because Quartz is not THub's version, step, lease, or audit model.
- **Keep only the latest graph JSON:** rejected because historical runs would not be reproducible.
- **Retry every failure automatically:** rejected because writes and actions can duplicate effects.
- **Single-worker execution without leases:** rejected because process crashes would leave runs permanently stuck and make later scale-out unsafe.

## Follow-up

- Add immutable versions, run/step state, leases, row-version concurrency, and SQL Server concurrency tests.
- Add configurable retention after PD-009 is resolved.
- Revisit checkpoint/resume-from-step only after connector output and side-effect semantics are defined.

