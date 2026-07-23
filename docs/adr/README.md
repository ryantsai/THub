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
| [0003](0003-windows-authentication-and-permission-policies.md) | Superseded | Windows Authentication and permission policies |
| [0004](0004-sql-server-control-plane-and-windows-worker.md) | Superseded | Initial SQL polling scheduler and Windows worker |
| [0005](0005-versioned-dag-workflow-model.md) | Accepted | Versioned DAG workflow model |
| [0006](0006-v1-sql-server-and-local-file-connectors.md) | Superseded | Initial SQL Server and local-file connector scope |
| [0007](0007-governed-data-publications.md) | Superseded | Initial governed REST and data-editor publication proposal |
| [0008](0008-gate-webhooks-and-executables.md) | Accepted | Gate webhook and executable actions behind explicit policies |
| [0009](0009-quartz-scheduling-and-serilog-observability.md) | Accepted | Quartz scheduling with THub run ownership and Serilog observability |
| [0010](0010-durable-leased-workflow-execution.md) | Accepted | Immutable versions and leased durable workflow execution |
| [0011](0011-isolated-governed-data-publications.md) | Accepted | Isolated REST publications and staged Spreadsheet editors |
| [0012](0012-durable-email-alert-delivery.md) | Accepted | Durable Email alert outbox and SMTP delivery |
| [0013](0013-provider-neutral-database-authentication.md) | Superseded | Initial external provider-neutral referenced database credentials |
| [0014](0014-expand-relational-and-ftp-connectors.md) | Accepted | MySQL, PostgreSQL, Oracle, and FTP/FTPS workflow connectors |
| [0015](0015-bounded-workflow-variables-and-javascript-expressions.md) | Accepted | Bounded workflow variables and JavaScript value expressions |
| [0016](0016-sql-backed-custom-role-and-resource-authorization.md) | Accepted | SQL-backed custom roles and resource authorization |
| [0017](0017-safe-workflow-lifecycle-and-package-portability.md) | Accepted | Safe workflow removal and redacted portable packages |
| [0018](0018-primary-key-relational-target-mutations.md) | Accepted | Primary-key upsert and incoming-key delete for relational targets |
| [0019](0019-encrypted-sql-connection-credentials.md) | Accepted | AES-GCM encrypted SQL-backed connection credentials with external versioned keys |

## Creating an ADR

1. Copy [template.md](template.md) to the next zero-padded number.
2. Use a concise kebab-case filename.
3. Describe the context and actual decision separately.
4. Include negative consequences and alternatives considered.
5. Link affected docs/issues/code where useful.
6. Add it to this index.
