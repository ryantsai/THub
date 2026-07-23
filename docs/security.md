# Security architecture

## Trust boundaries

THub handles privileged database access, files, outbound HTTP, and eventually process execution. A workflow designer is not automatically trusted to perform arbitrary operations under the worker service identity.

Primary boundaries:

1. Browser to `THub.Web` over authenticated HTTPS.
2. Internal API consumer to the separate `THub.Publications` HTTPS hostname.
3. Web, publication host, and worker to the THub control-plane database under distinct least-privilege identities.
4. Publication host to approved read-only SQL Server result objects.
5. Worker to source/target SQL Server instances and file locations, including approved staged editor writes.
6. Worker to an approved SMTP relay and, later, external webhooks or executables.
7. Browser editor sessions to bounded staged publication data in `THub.Web`.

Every boundary requires explicit authentication, authorization, input constraints, timeouts, and auditability.

## Authentication

Production uses ASP.NET Core Negotiate/Windows Authentication. It is appropriate for an intranet where users, web servers, and Active Directory participate in a controlled Windows environment.

`DevelopmentAuthenticationHandler` exists only to let loopback browser automation run without a Kerberos/NTLM handshake. It activates only when:

- the ASP.NET Core environment is `Development`;
- `Authentication:DevelopmentBypass` is explicitly `true`; and
- the request originates from a loopback address.

Do not turn this handler into a general development header, accept a caller-selected identity, or enable it in production.

The checked-in VS Code web debug profiles explicitly enable this handler so F5 does not depend on browser Negotiate behavior. The normal Development app setting remains disabled, allowing `dotnet run` without an override to exercise real Windows Authentication.

[ADR-0011](adr/0011-isolated-governed-data-publications.md) defines a separate authentication boundary for read-only REST publications. The dedicated internal `THub.Publications` IIS site permits anonymous transport so requests reach ASP.NET Core, but both implemented data routes require exactly one managed opaque bearer value in the `Authorization` header. Anonymous transport is not anonymous application authorization and must not be enabled on the Windows-authenticated `THub.Web` site. Query-string tokens, cookies, missing or duplicated authorization values, and malformed credentials do not authenticate.

## Authorization

THub uses permission policies rather than scattering raw role checks.

| Role | Intended permissions |
| --- | --- |
| Viewer | View workflows and operational state |
| Operator | View, execute, and manage schedules |
| Designer | View/edit workflows, manage connections and publications |
| Administrator | All permissions and platform settings |

The current resolver maps Windows/AD groups to application roles and optionally assigns a default authenticated role. The checked-in production group arrays are intentionally empty; configure real organization groups before deployment.

Rules:

- Enforce permissions at HTTP/application boundaries even when navigation is hidden.
- Never trust browser-side state or `AuthorizeView` as enforcement.
- Avoid putting authorization decisions inside infrastructure adapters.
- Record privileged changes in an audit stream when audit persistence is added.
- Per-workflow or per-connection grants require a separate authorization design; they are not implemented by the global roles.

Editor data access is resource-specific even though its subjects are the four global roles. Each editor publication independently grants View, Insert, Update, Delete, and Approve. `PublicationManage`, Designer membership, UI visibility, or possession of a REST bearer token grants none of those capabilities implicitly. Server-side authorization runs for editor load, lookup, submit, review, and change-set queries. Staging and review persist only after a serializable recheck of the exact grant fingerprint. Replacing grants transactionally rejects every pending or approved set before the new policy becomes current, so stale authorization cannot later reach the Worker; the Worker then accepts only an approved set whose publication/version/schema state remains current.

## Service identities

Web, publication-host, and worker identities should be separate and least-privileged.

- Web identity: read/write THub management and staging metadata plus only the approved source-read permissions needed to render editor columns/lookups; no source write credentials.
- Publication-host identity: read publication metadata/metering state and only the approved read-only SQL Server result objects for active REST publications; no management or write rights.
- Worker identity: claim/update runs and outbox/change-set rows, access only approved source/target databases and file roots, apply separately approved editor writes, and connect to approved SMTP relays.
- Migration/deployment identity: database schema modification rights; runtime identities should not require them.

For UNC paths or Windows-integrated remote SQL connections, confirm SPNs, delegation requirements, service accounts, and ACLs with infrastructure owners. Do not solve double-hop problems by embedding broad credentials.

## Secret handling

Connection configuration and workflow graphs contain a secret reference, never the secret value.

Database credentials follow [ADR-0013](adr/0013-provider-neutral-database-authentication.md). Connection metadata stores an authentication kind and reference only. The initial asynchronous resolver reads the referenced username/password through `IConfiguration`, allowing environment, key-per-file, Azure Key Vault, or another approved provider to own secret storage. A deployment may replace the resolver with a Vault/OpenBao or other organization-approved adapter. Windows Credential Manager is not the default because Web, Worker, and Publications normally use separate Windows profiles.

Managed publication tokens are not stored as reversible secret references: THub returns the full random token once, then persists only its selector, display prefix, verifier algorithm/version, and one-way verifier. Publication SQL connections use either the host's Windows identity or a referenced database credential and never store a database password. Email profiles also store references rather than credential values. SMTP references are resolved only by the Worker immediately before a send under the Worker identity.

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

## REST publications

[ADR-0011](adr/0011-isolated-governed-data-publications.md) supersedes ADR-0007. Publications remain a separate trust boundary, never a thin generic SQL controller.

- Initial REST deployment is internal-network and read-only on a separate hostname/process. No CORS origins are enabled by default.
- Routes contain only a stable publication slug. They never accept a connection, schema, table, or SQL fragment from the caller.
- An immutable active version allow-lists the SQL Server object, public column aliases, typed filters/operators, sorts, deterministic pagination key, page/row bounds, and schema fingerprint.
- The current version policy does not add a fixed row-level predicate. Publish only a source object whose full approved row scope is safe for every token on that publication; tenant- or consumer-specific row policies require additional design.
- Each publication can have multiple independently labeled, expiring, and revocable opaque tokens for safe rotation. Only `Authorization: Bearer` is accepted; tokens in query strings, cookies, duplicated headers, logs, or exports are rejected.
- Unknown, malformed, expired, revoked, and wrong-publication credentials receive the same generic Bearer challenge. A bearer identity cannot call management or editor endpoints.
- After authentication, resource authorization, and rate/concurrency admission, an atomic conditional update records the accepted request and rechecks token/publication state. Metering failure fails closed. The count describes an accepted credential use even when the later source query fails.
- Apply a pre-authentication network/IP control plus request and SQL timeouts, cancellation, response-size limits, keyset pagination, API Problem Details, and schema-drift failure. The implemented process-local request and concurrency admission partition is one token plus one active version; it does not aggregate traffic across all tokens for a publication.
- Built-in limiter state is process-local, so the accepted initial deployment has one publication-host instance. Scale-out or an aggregate publication-wide limit requires a gateway or distributed-limiter decision.
- Never log Authorization headers, bearer values, filter values, row payloads, or source credentials. Use correlation, token/publication IDs, counts, timing, status category, and normalized errors.

The isolated host implements `GET /api/v1/publications/{slug}/schema` and `GET /api/v1/publications/{slug}/rows`. Both resolve the current immutable active version, admit the request before metering, and atomically record the accepted token use. The schema route returns only approved public metadata; the rows route uses that allow-listed metadata to build parameterized SQL and accepts only bounded `pageSize`, `cursor`, repeated `filter`, and repeated `sort` parameters. Arbitrary SQL and object names are never route input. Schema or source-query failures fail closed with Problem Details. `/healthz` remains process liveness only, and production still needs network controls, a real TLS/hostname configuration, SQL readiness checks, centralized telemetry, and live SQL Server integration testing.

## Spreadsheet data editors

Editors remain inside Windows-authenticated `THub.Web` and stage all writes in the THub control plane.

- Radzen Blazor Spreadsheet is a bounded presentation surface, not the persistence or authorization boundary. Viewer sessions are read-only.
- Editor sessions may change approved values but cannot change structure, formatting, formulas, images, charts, hyperlinks, imports/exports, validation rules, clipboard contents, or autofill. Undo/redo remains available, and staged values are normalized and revalidated server-side.
- The default workbook window is 250 rows and the absolute maximum is 1,000. Server-side filtering and deterministic navigation remain authoritative.
- On submit, server code accepts the current edit, compares the workbook with its protected loaded snapshot, and revalidates keys, types, nullability, lengths, writable columns, formula absence, row limits, and the current resource grant.
- Insert, Update, Delete, and Approve are independent capabilities. Change sets persist submitter, reviewer, timestamps, before/after values, status, and bounded outcome details; the Blazor circuit never obtains a source write credential. A general append-only audit stream and retention/classification policy remain unresolved under PD-009.
- The worker claims only approved change sets and applies every set in one source transaction with source `rowversion` or an explicitly allowed original-value comparison. A concurrency or source-constraint mismatch becomes a conflict rather than an overwrite. A stale ambiguous apply is failed for operator reconciliation rather than replayed automatically.
- An editor source must be a table with a stable selected key; discovery prefers the primary key and otherwise can select a safe unfiltered unique index. Non-generated key values may be supplied on insert but keys are never mutable on update. Foreign-key metadata is discovered, but display/search suggestions default to unapproved and are frozen only after an administrator explicitly enables a lookup and selects its columns. Existing window labels are batch-resolved through bounded parameterized reads. A bounded searchable `RadzenDropDownDataGrid` cell editor commits the complete referenced key; composite components share read/write/nullability policy and update atomically. Submit rechecks every non-null staged tuple against the active version and source schema before persistence, while the source constraint remains the final apply-time authority.

The management UI now supports bounded SQL table/view discovery, immutable version activation, transactional role-grant replacement, and token management. The Windows-authenticated editor implements bounded deterministic windows, read-only viewer behavior, typed insert/update/delete staging, change-set list/detail/review, and governed foreign-key lookup cells. Views and objects without a usable key remain read-only. The current editor does not provide unrestricted workbook import/export, arbitrary formulas, direct writes, or automatic replay after an ambiguous source commit.

## Email alerts and actions

[ADR-0012](adr/0012-durable-email-alert-delivery.md) makes Email the first governed alert channel through a durable outbox rather than inline SMTP calls.

- Administrators manage delivery profiles and workflow-event rules through the Administration-protected `/alerts/email` surface. Profiles contain non-secret relay settings, approved sender, required TLS mode, recipient-domain rules, bounds, and an optional credential secret reference. The UI exposes only a reference field and warns that it is not a password field, but a free-form reference cannot technically distinguish a lookup key from a mistakenly pasted secret; administrators must never enter credential material there.
- Running-run terminal transitions and direct queued-run cancellation commit deduplicated workflow-rule outbox rows with the run state after rechecking the complete matching rule/profile snapshot. The `EmailAlert` action queues through the same outbox and succeeds when durable intent exists, not when delivery is confirmed; its run/node deduplication identity remains stable across recovered step attempts.
- The Worker claims due deliveries with leases and the MailKit adapter revalidates the profile before connecting. Only STARTTLS-required and implicit-TLS profiles are supported; plaintext SMTP is not an option.
- A profile with no credential reference connects without SMTP authentication and is suitable only for an explicitly approved anonymous relay protected by network/service-identity policy. The default `UnavailableSmtpSecretResolver` returns no credential for every reference, causing referenced profiles to fail closed. Production authenticated SMTP requires replacing that registration with an organization-approved `ISecretResolver`; never substitute a password in configuration or in the profile reference field.
- Known transient SMTP/connectivity/timeout failures and otherwise-unexpected sender failures receive bounded exponential-backoff retries. Authentication, TLS/configuration, permanent SMTP rejection, or attempt exhaustion dead-letters the delivery.
- Delivery is at-least-once. A crash after SMTP acceptance but before success persistence can duplicate an Email; stable correlation/message identifiers mitigate but do not eliminate that ambiguity.
- V1 has no attachments, arbitrary headers, or raw row/body templating. Templates use bounded allow-listed workflow/run/error variables, and profiles cap recipients, body/subject size, recipient domains, and concurrent sends. SQL claim admission serializes on the profile row and counts unexpired sending leases, so multiple Worker dispatchers share the same profile admission limit.

Email profiles, rules, outbox persistence, terminal-event integration, the `EmailAlert` action, leased dispatcher, MailKit adapter, and a redacted delivery monitor are implemented. Production secret-provider integration and a reviewed dead-letter requeue operation remain deployment/operations work.

## Logging and data handling

Structured logs may contain identities, workflow/run/step IDs, counts, timings, normalized error categories, and correlation IDs. They must not contain credentials, access tokens, unbounded row data, full connection strings, or raw sensitive payloads.

Publication access logs may add publication/version/token IDs and accepted row counts, never bearer text or filter values. Email logs may add profile/delivery IDs, attempt numbers, and safe outcome categories, never SMTP credentials, recipients beyond approved operational policy, or full subjects/bodies. Editor change sets currently store bounded before/after values; their final classification and retention policy remains open in PD-009, and a separate append-only audit stream is not yet implemented.

Serilog writes local rolling JSON files in addition to console output. Treat the log directory as operational data: grant write access only to the corresponding host identity, grant read access only to approved operators/collectors, monitor disk usage, and apply the documented retention policy. Do not grant workflow authors direct filesystem access to logs.

Quartz persists job and trigger data in SQL Server. That data is restricted to workflow ID, expected version, cron text, time-zone ID, and the logical scheduled occurrence timestamp. Never place graph JSON, connection configuration, credentials, webhook headers, executable arguments, or source rows in a `JobDataMap`.

Continue using structured message templates and bounded scalar properties through `ILogger<T>`. Do not log serialized request bodies, database commands containing sensitive values, or exceptions without considering whether their messages expose connection details.

Data previews must be permission-checked, bounded, short-lived, and masked according to future column-classification policy.
