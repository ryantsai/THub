# ADR-0005: Represent workflow versions as directed acyclic graphs

- Status: Accepted
- Date: 2026-07-22
- Deciders: Project maintainers

## Context

Visual workflows need explicit nodes, dependencies, validation, schema mapping, import/export, reproducible execution, and future parallel branches. An ordered list is insufficient for branches/joins, while unrestricted cycles complicate scheduling, retries, checkpoints, and row-stream semantics.

## Decision

Represent each workflow version as a directed acyclic graph containing typed nodes and directed edges. Node IDs are stable within the version. Publishing freezes an immutable graph version, and every run references that version.

Persist versioned graph/settings JSON while keeping lifecycle, ownership, status, schedules, and indexed query fields relational. Validate graph structure before publication and again before execution.

## Consequences

### Positive

- Supports parallel branches, joins, and deterministic topological scheduling.
- Visual layout and executable dependency structure share one model.
- Immutable versions make runs reproducible and auditable.
- JSON settings allow connector-specific evolution without a table per node kind.

### Negative

- Cyclic/iterative workflows are not supported by the core graph.
- JSON requires explicit schema versioning and migration/import compatibility.
- Database queries inside node settings are less convenient.
- Connector validation and schema compatibility require a second validation layer beyond graph structure.

## Alternatives considered

- **Ordered step list:** rejected because it cannot model general branching/joining cleanly.
- **Cyclic graph/state machine:** deferred; loops require bounded iteration and checkpoint semantics not needed for v1.
- **Fully normalized node setting tables:** rejected because connector configuration evolves and varies significantly by kind.

## Follow-up

- Add an explicit serialized graph schema version.
- Create an immutable `WorkflowVersions` persistence model before production save/publish.
- Define port/cardinality and schema compatibility contracts.

