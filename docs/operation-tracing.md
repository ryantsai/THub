# Workflow operation tracing convention

Every executable workflow operation must use the common execution-engine lifecycle and
structured trace convention described here. This applies to all current node executors
and is a required acceptance condition for every new operation.

## Required lifecycle

An operation must execute through `BoundedWorkflowExecutionEngine` and emit:

1. `NodeStarted` before invoking the executor.
2. Bounded `NodeProgressed` events when aggregate counters change.
3. Exactly one attempt outcome: `NodeSucceeded`, `NodeFailed`, `NodeCancelled`, or
   `NodeRetryScheduled`.
4. `NodeSkipped` when policy, preflight, or an unsuccessful dependency prevents execution.

Every node event must include the workflow run ID, stable node ID, node kind (the
operation name), attempt number when an attempt exists, and UTC occurrence time.
Terminal attempt events must include aggregate progress and a normalized error when
applicable. Retry events must also include the selected delay.

The Worker wraps the authoritative SQL event sink with `WorkflowOperationTraceSink`.
It logs only after the durable transition succeeds, so a trace line means the matching
execution event was accepted by the control plane. Start and terminal events use
Information, expected operation failures and retry scheduling use Warning, and bounded
progress events use Debug. The Worker configuration enables Debug for this trace source
without enabling verbose framework logs globally.

## Stable structured fields

Trace events keep these field names stable so centralized logging can correlate and
aggregate them:

- `WorkflowRunId`, `WorkflowId`, `WorkflowVersion`, and `WorkflowVersionId` for execution
  identity.
- `OperationName`, `NodeId`, and `Attempt` for operation identity.
- `OperationStatus`, `DurationMilliseconds`, and `RetryDelayMilliseconds` for outcomes.
- `RowsRead`, `RowsWritten`, `BatchesProcessed`, `BytesRead`, and `BytesWritten` for
  bounded aggregate progress.
- `ErrorCode`, `ErrorCategory`, and `IsRetryable` for normalized failure classification.
- `ReasonCode` for skipped operations.

New operation kinds must use these fields rather than creating operation-specific names
for equivalent values. They may add safe, low-cardinality fields when the common fields
cannot describe an operationally important fact.

## Data-safety boundary

Operation traces must never contain secrets, credentials, tokens, connection strings,
authorization or other sensitive headers, request or response bodies, row/cell values,
workflow settings JSON, SQL text or parameters, executable arguments or environment
variables, URLs, or local/remote paths. Do not log exception messages originating from
external systems. Use stable identifiers, aggregate counts, timings, and normalized
error codes/categories instead.

Executor-specific logs are allowed only for a fact that the common lifecycle cannot
express. They must follow the same field names and data-safety boundary and cannot
replace the common lifecycle trace.

## Adding a new operation

For every new `IWorkflowNodeExecutor`:

1. Register its `WorkflowNodeKind` and descriptor through the normal executor registry.
2. Execute it only through `BoundedWorkflowExecutionEngine`; do not bypass the engine
   or write an independent lifecycle.
3. Report aggregate progress through `IWorkflowNodeProgressReporter`.
4. Return normal results or throw a classified execution exception so the engine emits
   the terminal event.
5. Confirm every emitted node event includes `NodeKind`.
6. Review every additional log field against the data-safety boundary above.

Code review must reject a new operation that bypasses or weakens this convention.
