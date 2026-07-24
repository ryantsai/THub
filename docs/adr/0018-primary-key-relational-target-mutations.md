# ADR-0018: Primary-key relational target mutations

- Status: Accepted
- Date: 2026-07-23
- Deciders: Project maintainers
- Partially supersedes:
  [ADR-0014](0014-expand-relational-and-ftp-connectors.md), specifically its
  insert-only relational target restriction

## Context

Operational workflows need to apply deltas to existing targets instead of appending
every row. SQL Server, MySQL, PostgreSQL, and Oracle expose different native upsert
syntax. A loosely configured merge or delete can update the wrong row set or remove
data that was merely absent from a partial input. THub also has at-least-once execution,
so an ambiguous external commit can be replayed.

## Decision

Relational targets support three explicit modes on all four database providers:

- `insert` retains the existing parameterized append behavior;
- `upsert` inserts a missing row or updates its mapped non-key values;
- `delete` deletes only target rows whose primary keys are present in the input.

Upsert and delete store `keyColumns` in the schema-versioned node settings. At
validation and execution boundaries, those columns must exactly equal the primary key
discovered from the target table. Custom unique keys and arbitrary match expressions
are not accepted. Every key must be mapped from a source column. Mutation keys must be
non-null and unique within one node input. Upsert additionally requires at least one
mapped writable non-key column; generated key columns are not accepted for upsert.
Delete accepts only key mappings, including generated primary keys.

Each target node executes all its rows in one provider transaction with parameterized
values and provider-specific identifier quoting. SQL Server uses an update guarded by
update/serializable locks followed by a conditional insert. MySQL uses
`ON DUPLICATE KEY UPDATE`, PostgreSQL uses `ON CONFLICT`, and Oracle uses `MERGE`.
Metadata is reloaded and the immutable graph is revalidated at Worker execution.

`workflow.target.upsert` and `workflow.target.delete` are separate global or
workflow-resource permissions. The Developer system role receives both; custom roles
must be granted them explicitly. Save and publish operations reauthorize any mutation
mode present in the graph. Target database credentials remain independently
least-privileged.

The existing graph schema version remains compatible because `keyColumns` is required
only for the new modes and insert graphs remain unchanged. Target synchronization,
delete-absent behavior, update-only mode, replace-table mode, and custom merge
predicates are not implemented.

## Consequences

### Positive

- The same bounded workflow contract applies across every supported relational target.
- Primary-key matching is discoverable, deterministic, and revalidated against live
  metadata.
- Delete cannot turn a partial delta into a full target synchronization.
- Upsert and delete can be assigned independently from ordinary workflow editing.

### Negative

- Native provider statements differ and require live compatibility testing against the
  deployed server-version matrix.
- A large input holds one target transaction and its locks until the node completes.
- Tables without a discoverable primary key cannot use upsert or delete.
- Primary keys whose database collation equates distinct input values can still produce
  a provider conflict even though THub's in-memory duplicate check treats them as
  distinct.
- THub still cannot promise exactly-once effects after an ambiguous commit. Upsert and
  delete are replay-friendly for the same input, but database triggers and other side
  effects may not be.
- ADR-0022 provides durable metadata-only lifecycle records; run/step counters remain the bounded execution summary.

## Alternatives considered

- **Arbitrary merge predicates:** rejected because identifier, type, uniqueness, and
  cardinality safety would be difficult to enforce consistently.
- **Delete rows absent from the source:** rejected because a filtered or partial input
  could erase valid target data.
- **Any discovered unique index:** deferred because nullable and provider-specific
  uniqueness semantics differ.
- **Provider-neutral update-then-insert without locking:** rejected because concurrent
  workflows could race and attempt duplicate inserts.
- **One transaction per row:** rejected because a later failure would leave a partially
  applied node.

## Follow-up

- Add live integration suites for supported SQL Server, MySQL, PostgreSQL, and Oracle
  versions, including concurrency, composite keys, cancellation, and rollback.
- ADR-0022 records persisted configuration and execution lifecycle changes without row values.
- Revisit bounded chunk transactions only with an explicit partial-commit and recovery
  contract.
