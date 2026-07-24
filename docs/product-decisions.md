# Open product decisions

These choices materially affect security, deployment, execution semantics, or persistence. The scaffold uses conservative temporary assumptions but does not treat them as product decisions.

| ID | Decision needed | Current conservative assumption | Blocks |
| --- | --- | --- | --- |
| PD-001 | Are web, worker, SQL Server, and users in one AD domain/forest, and will IIS host the web app? | IIS, one domain-compatible intranet environment | Production authentication/deployment design |
| PD-003 | What Windows identity runs the worker, and do file connections include UNC shares? | Dedicated least-privilege service identity; local roots only | File connector, Kerberos/delegation, ACL deployment |
| PD-007 | Is `.xlsx`/`.xlsm` sufficient, or is legacy `.xls` mandatory? | Modern workbook formats only | Excel connector/library scope |
| PD-009 | Required run/log/audit retention and data classification/masking policy? | Store bounded metadata; no row payload in logs | Telemetry schema, cleanup jobs, previews, publications |

## Resolved decisions

| ID | Resolution | Authoritative record |
| --- | --- | --- |
| PD-004 | Online-editor changes are staged and audited. A separately authorized approval/apply path performs constrained source writes; direct writes are not part of v1. | [ADR-0011](adr/0011-isolated-governed-data-publications.md) |
| PD-005 | REST v1 is internal-network, read-only, and hosted separately from the Windows management app. Consumers use multiple managed opaque bearer tokens with independent revocation and accepted-request counts. | [ADR-0011](adr/0011-isolated-governed-data-publications.md) |
| PD-006 | Runs use immutable versions and SQL leases. Occurrences queue with one active run per workflow by default; cancellation is durable; only transient retry-safe operations receive bounded automatic retry; recovery remains at-least-once. | [ADR-0010](adr/0010-durable-leased-workflow-execution.md) |
| PD-008 | Database and FTP credentials retain provider-neutral references while their values are stored as AES-GCM ciphertext in SQL; one 256-bit master key remains external configuration. | [ADR-0023](adr/0023-single-credential-encryption-key.md) |
| PD-002 | SQL-backed roles support Windows user/group assignments, global permissions, per-workflow/per-connection grants, and custom-role editor publication grants. | [ADR-0016](adr/0016-sql-backed-custom-role-and-resource-authorization.md) |
| PD-010 | Writable editor versions use a separate Worker apply connection. It must target the same provider/database as the read connection and use a distinct stored credential reference when applicable. | [ADR-0021](adr/0021-provider-neutral-governed-publications.md) |

## Resolution process

When an owner resolves a decision:

1. Record the decision and date in the relevant issue/product record.
2. Add or update an ADR if the answer constrains architecture.
3. Update the relevant focused architecture document.
4. Implement with tests and migration/configuration changes as needed.
5. Remove the row here only after the authoritative record is linked from the docs.

Do not resolve these implicitly through implementation defaults.
