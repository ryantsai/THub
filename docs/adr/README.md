# Architecture Decision Records

ADRs record decisions that materially constrain THub's structure, runtime behavior, security, persistence, or supported platform.

## Status values

- **Proposed:** under review and not an implementation mandate.
- **Accepted:** current implementation direction.
- **Deprecated:** retained for history but discouraged.
- **Superseded:** replaced by a later ADR, which must link back to it.

Accepted ADRs are not immutable. Supersede them when evidence changes; do not silently rewrite their decision or consequences. Small corrections and link fixes are allowed.

## Index

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-modular-monolith-on-dotnet-10.md) | Accepted | Modular monolith on .NET 10 |
| [0002](0002-blazor-interactive-server-and-radzen.md) | Accepted | Blazor Interactive Server and Radzen UI |
| [0003](0003-windows-authentication-and-permission-policies.md) | Accepted | Windows Authentication and permission policies |
| [0004](0004-sql-server-control-plane-and-windows-worker.md) | Superseded | Initial SQL polling scheduler and Windows worker |
| [0005](0005-versioned-dag-workflow-model.md) | Accepted | Versioned DAG workflow model |
| [0006](0006-v1-sql-server-and-local-file-connectors.md) | Accepted | v1 SQL Server and local-file connector scope |
| [0007](0007-governed-data-publications.md) | Proposed | Governed REST and data-editor publications |
| [0008](0008-gate-webhooks-and-executables.md) | Accepted | Gate webhook and executable actions behind explicit policies |
| [0009](0009-quartz-scheduling-and-serilog-observability.md) | Accepted | Quartz scheduling with THub run ownership and Serilog observability |

## Creating an ADR

1. Copy [template.md](template.md) to the next zero-padded number.
2. Use a concise kebab-case filename.
3. Describe the context and actual decision separately.
4. Include negative consequences and alternatives considered.
5. Link affected docs/issues/code where useful.
6. Add it to this index.
