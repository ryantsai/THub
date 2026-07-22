# Security architecture

## Trust boundaries

THub handles privileged database access, files, outbound HTTP, and eventually process execution. A workflow designer is not automatically trusted to perform arbitrary operations under the worker service identity.

Primary boundaries:

1. Browser to `THub.Web` over authenticated HTTPS.
2. Web and worker to the THub control-plane database.
3. Worker to source/target SQL Server instances and file locations.
4. Worker to external webhooks or executables.
5. Consumers to generated REST/editor publications.

Every boundary requires explicit authentication, authorization, input constraints, timeouts, and auditability.

## Authentication

Production uses ASP.NET Core Negotiate/Windows Authentication. It is appropriate for an intranet where users, web servers, and Active Directory participate in a controlled Windows environment.

`DevelopmentAuthenticationHandler` exists only to let loopback browser automation run without a Kerberos/NTLM handshake. It activates only when:

- the ASP.NET Core environment is `Development`;
- `Authentication:DevelopmentBypass` is explicitly `true`; and
- the request originates from a loopback address.

Do not turn this handler into a general development header, accept a caller-selected identity, or enable it in production.

## Authorization

THub uses permission policies rather than scattering raw role checks.

| Role | Intended permissions |
| --- | --- |
| Viewer | View workflows and operational state |
| Operator | View, execute, and manage schedules |
| Designer | View/edit workflows, manage connections and publications |
| Administrator | All permissions and platform settings |

The current resolver maps Windows/AD groups to application roles and optionally assigns a default authenticated role. Replace placeholder groups before deployment.

Rules:

- Enforce permissions at HTTP/application boundaries even when navigation is hidden.
- Never trust browser-side state or `AuthorizeView` as enforcement.
- Avoid putting authorization decisions inside infrastructure adapters.
- Record privileged changes in an audit stream when audit persistence is added.
- Per-workflow or per-connection grants require a separate authorization design; they are not implemented by the global roles.

## Service identities

Web and worker identities should be separate and least-privileged.

- Web identity: read/write THub management metadata required by user operations; no direct source-data execution rights unless a concrete feature requires them.
- Worker identity: claim/update runs and access only approved source/target databases and file roots.
- Migration/deployment identity: database schema modification rights; runtime identities should not require them.

For UNC paths or Windows-integrated remote SQL connections, confirm SPNs, delegation requirements, service accounts, and ACLs with infrastructure owners. Do not solve double-hop problems by embedding broad credentials.

## Secret handling

Connection configuration and workflow graphs contain a secret reference, never the secret value.

Acceptable secret providers may include Windows DPAPI-protected storage, Windows Credential Manager, Azure Key Vault, or another organization-approved vault. The provider decision remains open.

Never:

- commit credentials or tokens;
- return secrets to Blazor components after creation;
- write secrets to logs, run errors, audit details, data previews, import/export packages, or exception messages;
- use reversible application-level obfuscation as secret storage.

## SQL connectors

- Use parameterized values; never concatenate user values into commands.
- Validate server/database/schema/table/column identifiers against discovered and approved metadata.
- Store explicit allowed objects and operations in the workflow or publication definition.
- Apply command timeouts, cancellation, row/batch limits, and least-privilege database accounts.
- Keep preview queries bounded.
- Treat merge/upsert/delete/replace modes as separately authorized, auditable capabilities.

## Local files

- Resolve paths against configured connection roots and verify the final canonical path remains inside the root.
- Reject traversal, device paths, unexpected reparse points, and unauthorized UNC locations.
- Bound file size, sheet/range size, row length, and parsing errors.
- Do not execute file content or use uploaded filenames as trusted paths.
- Define collision, partial-file, file-lock, archive, and quarantine behavior before file watchers are enabled.

## Webhooks

- Use `IHttpClientFactory` with explicit timeouts and bounded request/response sizes.
- Allow only approved schemes and destinations; defend against SSRF, redirects to private endpoints, and DNS rebinding as appropriate to the environment.
- Resolve authentication headers from secret references.
- Redact sensitive headers and bodies from logs.
- Define retry/idempotency behavior per webhook node.

## External executables

Executable nodes are a privileged feature and must remain disabled until policy is implemented.

Required controls:

- Administrator-owned allow-list of canonical executable paths.
- Fixed argument templates with typed placeholders; never a shell command string.
- Controlled working directories and environment variables.
- Restricted Windows service account and filesystem ACLs.
- Time, CPU/memory where enforceable, stdout/stderr, and output-file limits.
- Process-tree termination on cancellation/timeout.
- No interactive desktop and no arbitrary PowerShell/cmd invocation by workflow authors.
- Audit records for definition, approval, and every invocation.

## REST and data-editor publications

Publications are a separate application trust boundary, not a thin generic SQL controller.

- Default REST publications to read-only.
- Require an explicit named authentication policy per publication class.
- Allow-list source object, projected columns, filters, sorting, pagination, and maximum page size.
- Never expose arbitrary SQL or accept an arbitrary table name from a route.
- For editors, separately grant insert, update, and delete; apply optimistic concurrency and field validation.
- Prefer staged changes with approval and full before/after audit for sensitive data.
- Add rate limiting, request size limits, problem details, and telemetry without leaking schema or connection information.

No publication runtime should ship until ADR-0007 is resolved.

## Logging and data handling

Structured logs may contain identities, workflow/run/step IDs, counts, timings, normalized error categories, and correlation IDs. They must not contain credentials, access tokens, unbounded row data, full connection strings, or raw sensitive payloads.

Data previews must be permission-checked, bounded, short-lived, and masked according to future column-classification policy.

