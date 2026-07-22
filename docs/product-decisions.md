# Open product decisions

These choices materially affect security, deployment, execution semantics, or persistence. The scaffold uses conservative temporary assumptions but does not treat them as product decisions.

| ID | Decision needed | Current conservative assumption | Blocks |
| --- | --- | --- | --- |
| PD-001 | Are web, worker, SQL Server, and users in one AD domain/forest, and will IIS host the web app? | IIS, one domain-compatible intranet environment | Production authentication/deployment design |
| PD-002 | Are global AD-group roles sufficient, or are per-user/per-workflow/per-connection grants required? | Four global group-mapped roles | Resource authorization schema and administration UI |
| PD-003 | What Windows identity runs the worker, and do file connections include UNC shares? | Dedicated least-privilege service identity; local roots only | File connector, Kerberos/delegation, ACL deployment |
| PD-004 | Do online-editor changes write directly to source tables or use staging and approval? | Staged, audited writes | ADR-0007 and editor persistence/runtime |
| PD-005 | Who consumes generated REST APIs, from which networks, and with Windows, Entra/JWT, or managed API-key authentication? | Internal-only, read-only, no runtime yet | ADR-0007 and publication host/auth model |
| PD-006 | Required run concurrency, retry/backoff, missed-schedule, cancellation, and recovery semantics? | One scheduler; at-least-once effects; no automatic execution retry yet | Run claim/lease and execution engine |
| PD-007 | Is `.xlsx`/`.xlsm` sufficient, or is legacy `.xls` mandatory? | Modern workbook formats only | Excel connector/library scope |
| PD-008 | Which approved secret provider should web and worker use? | Secret references in metadata; provider undecided | Production connection creation and execution |
| PD-009 | Required run/log/audit retention and data classification/masking policy? | Store bounded metadata; no row payload in logs | Telemetry schema, cleanup jobs, previews, publications |

## Resolution process

When an owner resolves a decision:

1. Record the decision and date in the relevant issue/product record.
2. Add or update an ADR if the answer constrains architecture.
3. Update the relevant focused architecture document.
4. Implement with tests and migration/configuration changes as needed.
5. Remove the row here only after the authoritative record is linked from the docs.

Do not resolve these implicitly through implementation defaults.
