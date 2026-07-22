# ADR-0006: Limit v1 connectors to SQL Server and local CSV/XLSX files

- Status: Accepted
- Date: 2026-07-22
- Deciders: Project maintainers

## Context

Connector breadth is a major source of security, test, and operational complexity. The requested v1 requires SQL Server and local files, including Excel and CSV. Supporting a small coherent set allows the execution engine, schema model, mapping, checkpointing, and observability to mature before adding more systems.

## Decision

Support these v1 data connectors:

- SQL Server source and target.
- CSV source and target using CsvHelper.
- Modern Excel `.xlsx`/`.xlsm` sheet or named-range access using ClosedXML.

Use a bounded asynchronous tabular batch contract across connectors and transformations. Configuration must reference approved connections/file roots and secret references rather than raw credentials or unrestricted paths.

Webhook and executable nodes are action nodes, not tabular connectors; each requires its own guardrails. Legacy `.xls` is out of scope unless separately accepted.

## Consequences

### Positive

- Focused schema/mapping and execution design.
- Libraries and infrastructure align with the selected .NET/Windows stack.
- Streaming/batch constraints can be tested against representative relational and file data.
- Reduced secret, protocol, and dependency surface for v1.

### Negative

- No Oracle, PostgreSQL, cloud storage, SFTP, or SaaS connectors in v1.
- ClosedXML is not a streaming engine for arbitrarily large workbooks; strict limits are required.
- CSV dialect and encoding differences still require explicit configuration.
- Network shares introduce service identity and path-security concerns despite being "files."

## Alternatives considered

- **Adopt an existing general connector framework immediately:** rejected until THub's data/schema/execution contracts are stable.
- **OLE DB/ODBC generic connector:** rejected for v1 because provider differences weaken validation and supportability.
- **Support legacy `.xls`:** deferred pending an explicit user requirement and library/security evaluation.

## Follow-up

- Define tabular schema and batch interfaces in Application.
- Add size, memory, timeout, and path-root policies.
- Add connector contract and SQL Server integration tests.

