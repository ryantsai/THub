# ADR-0015: Use bounded workflow variables and JavaScript value expressions

- Status: Accepted
- Date: 2026-07-23
- Deciders: Project maintainers

## Context

Graphical destination mappings need values beyond upstream columns. Required examples include a run timestamp or run ID, workflow-wide literal values, one scalar retrieved from an approved database object, and reusable JavaScript functions for bounded value transformation.

Arbitrary SQL or unrestricted script execution would expose the Worker identity, credentials, process, network, and host resources. Evaluating a database query once for every input row would also create an unbounded N+1 workload. The application is unreleased, so the graph contract can change directly without preserving schema version 1.

## Decision

Workflow graph schema version 2 adds:

- typed workflow variables;
- reusable expression-only JavaScript functions;
- destination bindings whose value source is an upstream column, a workflow variable, or a JavaScript expression.

The Worker supplies immutable built-in values for the current execution attempt:

- `runId`;
- `runStartedAtUtc`;
- `utcToday`.

Literal workflow variables are non-secret values stored in the immutable graph. Database variables reference an enabled approved relational connection and a quoted schema, object, value column, and filter column. The filter value is a parameter. A lookup reads at most two rows, succeeds only for exactly one scalar row, and resolves once before node execution. Per-row database enrichment must use ordinary source and join nodes.

JavaScript uses the open-source Jint interpreter. Scripts receive only frozen JSON-shaped `row` and `vars` objects plus declared workflow functions. THub does not enable CLR, filesystem, network, module, or secret access. String compilation through `eval` and the `Function` constructor is disabled. Every evaluation uses cancellation plus fixed memory, statement, recursion, and wall-clock bounds, and remains subject to the node and run deadlines.

Functions are expression-only and destination expressions must return a value compatible with the destination column type. Scripts cannot mutate workflow state or perform side effects.

## Consequences

### Positive

- Designers can graphically bind destination columns to source values, run metadata, shared literals, database configuration values, or reusable transformations.
- Database retrieval remains allow-listed, parameterized, bounded, and independent of input row count.
- JavaScript does not inherit arbitrary Worker or CLR authority.
- Published graphs retain the exact variable, function, and binding definitions used by a run.

### Negative

- Jint is an additional runtime dependency requiring vulnerability monitoring and upgrade review.
- An in-process interpreter is a resource-control layer, not an operating-system security boundary. The Worker service identity and host-level resource controls remain important.
- Database variables are external dynamic inputs. THub does not currently persist their resolved values, so a recovered or manually retried run can observe a changed scalar.
- JavaScript evaluation adds per-row CPU cost. Large transformations should prefer built-in typed transforms.
- Schema version 1 workflow documents are intentionally unsupported.

## Alternatives considered

- **Arbitrary SQL variables:** rejected because a workflow author could bypass approved object and identifier policy.
- **One database lookup per row:** rejected because it creates unbounded load and failure amplification; use source/join nodes.
- **Node.js or browser execution:** rejected because it adds another process/runtime boundary and would not share the Worker cancellation and type contracts cleanly.
- **Only built-in expression syntax:** deferred; Jint provides familiar reusable functions while retaining explicit resource constraints.

## Follow-up

- Add persisted resolved-variable snapshots if exact replay of external database values becomes a requirement.
- Add administrator-configurable script limits only after preserving secure upper bounds.
- Monitor Jint advisories and run the dependency vulnerability audit whenever the package changes.
