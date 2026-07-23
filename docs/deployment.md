# Deployment and operations

## Authorization bootstrap

SQL Server is authoritative for roles, permissions, assignments, and resource grants. Before the first production start, configure at least one emergency/bootstrap administrator user or Windows group:

```text
Authorization__Bootstrap__SystemAdministratorGroups__0=DOMAIN\THub System Administrators
```

Optional Developer users/groups use `DeveloperUsers` or `DeveloperGroups`. Bootstrap arrays do not replace persisted role administration, and an unmapped authenticated identity is denied by default.

## Recommended topology

The accepted initial production topology is Windows Server with separate IIS applications/hostnames for `THub.Web` and one `THub.Publications` instance, one `THub.Worker` Windows Service, and an existing SQL Server instance. The publication host is functional, but production use still requires a real internal hostname/certificate, firewall policy, separate least-privilege identities, reviewed migrations, source-object grants, and live SQL Server verification.

```text
Corporate browser -- HTTPS + Windows Authentication --> IIS / THub.Web --------+
                                                                                 |
Internal API client -- HTTPS + opaque bearer --------> IIS / THub.Publications -+--> SQL Server / THub control plane
                                                                                 |
THub.Worker Windows Service -----------------------------------------------------+  (Quartz clustered scheduler)
      |                                      |
      +---- approved SQL Server/file roots   +---- approved SMTP relay

THub.Publications --------------------- approved read-only SQL result objects
```

Web, publication, and worker processes may initially share a Windows machine, but they are independently deployable and require separate least-privilege identities. The publication API remains one process instance until rate limiting is made distributed or moved to a gateway.

## Environment assumptions to confirm

- Whether IIS, worker, SQL Server, and users are in one AD forest/domain.
- Whether each source SQL connection uses Windows integrated or referenced database authentication.
- Whether file locations include UNC shares.
- Whether outbound internet access is allowed.
- The internal DNS name, certificate, firewall allow-list, and consumer networks for `THub.Publications`.
- Which Windows identity runs the Publications application pool and receives exact source-object `SELECT` grants, and which separate Worker identity applies approved editor change sets.
- SMTP relay, sender, TLS, recipient-domain, and service-account policy for Email delivery, including whether the relay permits the Worker identity/network to send anonymously or requires an approved secret provider.
- Which account owns database migrations.
- Certificate source and renewal procedure.

These decisions affect SPNs, delegation, firewall rules, service accounts, and secret storage.

## Web deployment

Recommended controls:

- Publish a Release build and host behind IIS with Windows Authentication enabled and anonymous authentication disabled, except a separately reviewed health-probe path if required.
- Use HTTPS, HSTS, appropriate host filtering, and forwarded headers only when a trusted proxy exists.
- Persist ASP.NET Core data-protection keys to an access-controlled durable location if multiple web instances are introduced.
- Configure production AD group mappings and remove the permissive default role when required by policy.
- Do not enable `Authentication:DevelopmentBypass`.
- Grant the web application pool identity only the THub database rights needed for management operations.
- Grant only approved source-read access required for Spreadsheet rows and foreign-key lookups. Never grant the web identity source-write or SMTP credentials.

## Publication-host deployment

Publish the executable `THub.Publications` host separately:

```powershell
dotnet publish src/THub.Publications -c Release -r win-x64 --self-contained false -o artifacts/publications
```

The host exposes authenticated read-only `GET /api/v1/publications/{slug}/schema` and `/rows` routes plus anonymous process liveness at `/healthz`. Both data routes resolve the active immutable version, authenticate exactly one managed bearer value, apply process-local admission, and atomically meter the accepted use. The schema route returns approved public metadata; the rows route executes a bounded parameterized query against the approved SQL object. Unknown/malformed/expired/revoked/wrong-publication credentials share a generic challenge; a metering or schema-drift failure serves no data.

Required [ADR-0011](adr/0011-isolated-governed-data-publications.md) deployment controls:

- Use a dedicated internal DNS name, HTTPS certificate, IIS application pool, and least-privilege identity. Do not place publication routes under the `THub.Web` IIS application.
- Enable IIS Anonymous Authentication only on the publication site so a request reaches ASP.NET Core; disable Windows Authentication there. Each ASP.NET Core data endpoint requires the managed opaque bearer credential. Anonymous IIS transport is not anonymous data access.
- Keep the management site on Windows Authentication with anonymous access disabled. A bearer principal must never authorize management or Spreadsheet operations.
- Restrict inbound networks/firewalls to approved internal consumers. No CORS origin is enabled by default, and internet exposure requires a new ADR.
- Grant the host only the control-plane token/publication/metering rights and source-read rights required by active publications. It receives no source-write, workflow-execution, Email, or management authority.
- The Publications composition profile does not register SMTP, workflow execution, editor staging/apply, role-grant management, or other Web/Worker adapters. Database and source-object grants remain the authoritative enforcement if code is changed or misconfigured.
- The current immutable version allow-lists objects and columns but has no fixed row-level authorization predicate. Expose only source objects whose entire approved row scope is safe for every token on that publication.
- Run one publication instance. Admission state is process-local and partitioned by token plus active version; it does not aggregate every token for a publication. Scale-out or aggregate publication-wide admission requires a gateway or distributed limiter.
- Override `AllowedHosts` from the checked-in `localhost` value, supply the real THub connection through deployment configuration, persist any required Data Protection keys, and route logs/metrics to approved storage.
- Treat the full bearer token as a one-time response secret. Never place it in configuration, logs, command lines, URLs, or deployment artifacts.

## Worker deployment

Publish:

```powershell
dotnet publish src/THub.Worker -c Release -r win-x64 --self-contained false -o artifacts/worker
```

Install from an elevated PowerShell terminal:

```powershell
./scripts/install-worker.ps1 `
  -PublishDirectory ./artifacts/worker `
  -Credential 'CONTOSO\svc-thub-worker'
```

The installer creates an automatic service but deliberately does not start it. Before starting:

1. Place production configuration/secrets using the approved provider.
2. Grant the service account access to the THub database.
3. Grant only the required source SQL and file-root permissions.
4. Confirm the content root and log destination are writable where needed.
5. Apply database migrations with the deployment identity.
6. Start the service and verify logs plus scheduler readiness.

The Worker hosts leased workflow execution, leased Email-outbox dispatch, and approved editor-change application in addition to Quartz scheduling. For workflows it atomically claims queued or expired-lease runs, heartbeats ownership, verifies the exact immutable graph checksum, executes bounded nodes, and persists lease-checked step/terminal state. Grant its service identity only the configured source/target SQL rights and approved file roots; SQL targets support insert and file targets support create-new, so do not grant broader mutation rights merely because the account is a Worker. Recovery restarts an abandoned graph and external effects remain at-least-once.

The editor processor claims an approved change set, revalidates its immutable version and current source-schema fingerprint, and applies the bounded set in one source transaction using `rowversion` or original-value predicates. Grant the Worker source-write access only to approved editor tables. A stale ambiguous apply is marked failed for operator reconciliation rather than replayed automatically; do not promise exactly-once cross-database effects. Email delivery revalidates profile policy, uses bounded SMTP timeouts, and records lease-checked durable outcomes. Grant the Worker access only to the configured relay and source/target resources; authenticated SMTP must remain unavailable until an approved secret-resolution integration is deployed.

## Database deployment

Restore the local tool and inspect pending migrations:

```powershell
dotnet tool restore
dotnet tool run dotnet-ef migrations list `
  --project src/THub.Infrastructure `
  --startup-project src/THub.Web
```

Apply migrations through a controlled deployment step. Runtime web/worker identities should not require `ALTER`, `CREATE`, or `DROP` rights.

Back up the THub database before migrations that transform or delete data. Review generated SQL for large-table locks and destructive operations.

The Quartz tables are vendor-defined but are installed through the THub migration chain. The worker validates the expected Quartz schema at startup and fails rather than creating or changing tables. Review the official SQL Server schema changes and create a forward THub migration before upgrading Quartz across a version that changes its persistence schema.

Reviewed migrations create immutable workflow versions, run claim/cancellation fields, durable step attempts, Email delivery profiles/rules/outbox, and publication definitions, immutable versions/columns/foreign keys, role grants, bearer metadata/counters, and change sets/changes, including their indexes, foreign keys, and optimistic concurrency tokens. Apply the complete migration chain before starting Web, Worker, or Publications; do not enable execution, Email, or publication traffic against a partially upgraded database. Other persistence details are documented in [the data model](data-model.md).

## Configuration

For local Development/debugging, all three executable hosts use `THub.Debug` on `(localdb)\MSSQLLocalDB`. Development settings are defined only in `appsettings.Development.json`, and those files are excluded from publish output.

Every non-Development host must receive the real SQL Server `ConnectionStrings:THub` through environment-specific external configuration or an organization-approved configuration provider. Web, Worker, and Publications register Infrastructure and fail startup when it is missing. Base settings intentionally contain no production fallback connection string.

Configured source/target connections store only a credential reference such as
`warehouse_reader` or `partner_ftp` in their metadata. SQL Server independently selects
Windows integrated or referenced username/password authentication. MySQL, PostgreSQL,
Oracle, and FTP require a referenced username/password. Administrators enter those
values in the connection editor; THub stores the payload as AES-256-GCM ciphertext in
`thub.EncryptedConnectionCredentials`.

Generate a random 32-byte key once and provision it to every authorized Web, Worker, and
Publications host through environment configuration:

```powershell
$credentialKeyBytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($credentialKeyBytes)
$env:CredentialEncryption__CurrentKeyVersion = '1'
$env:CredentialEncryption__Keys__1 = [Convert]::ToBase64String($credentialKeyBytes)
[System.Security.Cryptography.CryptographicOperations]::ZeroMemory($credentialKeyBytes)
```

The sample assigns only the current process environment; provision persistent
IIS/Windows-Service environment settings through the deployment system and restart each
host. Do not copy the generated example output into source control, shell history,
deployment logs, or the THub database.

The Web host needs the key to create/replace credentials and test/discover connections;
the Worker needs it for workflow execution. Publications needs it only when an active
SQL publication uses referenced authentication. Give each host only the table access,
source-system grants, and key ring it requires. Missing references, missing key versions,
invalid key lengths, and authentication failures fail closed. Existing installations
must re-enter each externally provisioned credential in the connection editor after
applying the migration.

Trusted webhook authentication and executable run-as accounts use the same encrypted
credential table and `CredentialEncryption` key ring. The Web needs the current key to
create or replace these credentials; the Worker needs every referenced key version to
invoke them. For executable impersonation, provision the target Windows account's
required local logon right and only the filesystem/network ACLs needed by that trusted
definition. Keep the Worker non-interactive. THub clears the inherited child environment,
does not invoke a shell, and never passes the credential through arguments or environment
variables.

After creating a definition under `/trusted-actions`, assign `trusted-action.use` on its
resource ID to the intended custom role under `/settings`. System Administrator has
implicit access to every trusted action. Disabling a trusted action blocks new Worker
invocations, including invocations from already-published workflow versions.

To rotate, add a new `CredentialEncryption__Keys__<version>` value to every authorized
host while retaining old versions, change `CurrentKeyVersion`, and restart the hosts.
New or replaced credentials use the current version. Replace every stored credential
through the editor before removing an old key; automated bulk re-encryption is not yet
implemented. Back up all still-required keys separately from SQL backups. Losing a key
version makes its remaining rows unrecoverable.

FTP connections select plain FTP, explicit FTPS, or implicit FTPS. Plain FTP exposes credentials and data in transit and must be restricted to explicitly approved legacy endpoints on a controlled network. Prefer FTPS with certificate validation. Size Worker temporary storage for the configured maximum FTP file size and monitor `%TEMP%\THub` for remnants after abrupt process termination.

Workflow database variables use the same referenced credentials as their approved connection and resolve once at the start of each execution attempt. Grant the Worker read access only to the lookup objects that workflows are permitted to use. JavaScript value expressions run inside the Worker with fixed internal safety ceilings; no deployment setting can remove CLR/string-compilation restrictions or raise those ceilings in v1.

The worker supports:

```json
{
  "Scheduler": {
    "ReconciliationIntervalSeconds": 15,
    "MaxConcurrency": 10,
    "DatabaseRetryIntervalSeconds": 15,
    "ClusterCheckinIntervalSeconds": 10,
    "ClusterCheckinMisfireThresholdSeconds": 20
  },
  "Execution": {
    "MaximumConcurrency": 32,
    "PollIntervalMilliseconds": 1000,
    "LeaseDurationSeconds": 60,
    "HeartbeatIntervalSeconds": 15,
    "MaximumRunDurationMinutes": 720,
    "NodeAttemptTimeoutMinutes": 30,
    "MaximumColumns": 256,
    "MaximumRowsPerBatch": 5000,
    "MaximumBytesPerBatch": 8388608,
    "MaximumRowsPerNodeOutput": 1000000,
    "MaximumBytesPerNodeOutput": 536870912,
    "MaximumRetainedRowsPerWorkflow": 3000000,
    "MaximumRetainedBytesPerWorkflow": 1610612736
  },
  "Serilog": {
    "FilePath": "%PROGRAMDATA%\\THub\\Logs\\thub-worker-.json"
  }
}
```

`ReconciliationIntervalSeconds` controls how quickly changed THub schedule metadata is reflected in Quartz; it is not a due-work polling interval. Quartz persists timing and cluster state in the `quartz` schema. All executable hosts support `Serilog:FilePath`; relative paths resolve from the host content root and environment variables are expanded.

### Worker workflow-execution settings

| Setting | Default | Valid range | Purpose |
| --- | ---: | ---: | --- |
| `Execution:MaximumConcurrency` | 32 | 1–32 | Concurrent leased workflow runs in this process; nodes inside one run remain topologically sequential |
| `Execution:PollIntervalMilliseconds` | 1,000 | 100–60,000 ms | Wait when no additional run is claimable |
| `Execution:LeaseDurationSeconds` | 60 | 15–3,600 seconds | SQL ownership period for one run |
| `Execution:HeartbeatIntervalSeconds` | 15 | 5–1,200 seconds | Lease renewal and durable cancellation observation interval; must be less than half the lease duration |
| `Execution:MaximumRunDurationMinutes` | 720 | 1–1,440 minutes | End-to-end engine deadline for one claim attempt |
| `Execution:NodeAttemptTimeoutMinutes` | 30 | 1–1,440 minutes | Default deadline for one node attempt |
| `Execution:MaximumColumns` | 256 | 1–512 | Tabular schema bound |
| `Execution:MaximumRowsPerBatch` | 5,000 | 1–100,000 | Rows in one connector/transform batch |
| `Execution:MaximumBytesPerBatch` | 8 MiB | 1 byte–256 MiB | Estimated bytes in one batch |
| `Execution:MaximumRowsPerNodeOutput` | 1,000,000 | 1–10,000,000 | Replayable rows retained for one node output |
| `Execution:MaximumBytesPerNodeOutput` | 512 MiB | 1 byte–4 GiB | Estimated retained bytes for one node output |
| `Execution:MaximumRetainedRowsPerWorkflow` | 3,000,000 | 1–10,000,000 | Combined live intermediate-row budget for a run |
| `Execution:MaximumRetainedBytesPerWorkflow` | 1.5 GiB | 1 byte–4 GiB | Combined live intermediate-byte budget for a run |

Startup validates all ranges and the heartbeat/lease relationship. Intermediate outputs are currently materialized in Worker memory, so absolute maxima are safety ceilings rather than recommended production values; size them to the service account's memory budget and expected branch fan-out. Connection profiles independently cap relational command/batch sizes and local/FTP file bytes/rows/columns. Read-only source/transform attempts retry at most three times for classified transient failures with exponential jitter. Database/file/FTP targets and Email actions have no automatic node retry; an expired whole-run lease can still restart the graph, so every external effect must tolerate at-least-once recovery.

Publication limits are immutable version metadata, not `PublicationApi` host settings. The management UI supplies the defaults below, and domain construction rejects values outside the hard ranges:

| Version setting | Management default | Hard range | Enforcement |
| --- | ---: | ---: | --- |
| Default / maximum REST page size | 100 / 500 | 1–1,000 rows | Requested size is capped by the active version and the SQL connection batch limit |
| Requests / rate window | 600 / 60 seconds | 1–100,000 / 1–3,600 seconds | Process-local fixed window per token plus active version |
| Maximum concurrent requests | 10 | 1–100 | Process-local per token plus active version |
| Spreadsheet editor window | 250 | 1–1,000 rows | Loaded as one deterministic keyset window |
| Request / SQL command timeout | 30 / 30 seconds | 1–300 seconds | Linked request cancellation and the lower applicable SQL connection/version command bound |
| Maximum JSON response | 10 MiB | 1 KiB–100 MiB | Estimated during source materialization and enforced again during serialization |

SQL connection metadata adds a `MaximumBatchRows` bound (default 1,000; range 1–10,000), so increasing a publication limit cannot bypass the connector limit. The REST query accepts at most 16 filters, 8 requested sorts, a 4,096-character server-issued schema/query-bound cursor, and 4,096 characters per filter value. Foreign-key lookups accept at most 100 rows and 256 search characters. Source discovery returns at most 200 objects, 1,024 columns, and 128 foreign-key mappings.

Email delivery profiles are administrator-managed control-plane metadata rather than raw host configuration. Configure profiles and workflow terminal-event rules at `/alerts/email`; each profile contains non-secret relay/sender/TLS/recipient policy and an optional credential secret reference. The Worker resolves that reference only at send time. The checked-in `UnavailableSmtpSecretResolver` always returns no credential, so referenced profiles fail closed. It permits only profiles intentionally configured for an approved anonymous relay. Production authenticated SMTP requires replacing the `ISecretResolver` registration with an organization-approved implementation; do not put SMTP user names or passwords in `appsettings`, the database, or the secret-reference field.

### Worker Email delivery settings

| Setting | Default | Valid range | Purpose |
| --- | ---: | ---: | --- |
| `EmailDelivery:Smtp:OperationTimeoutSeconds` | 30 | 5–300 seconds | Shared bound for SMTP connect, authentication, and send operations |
| `EmailDelivery:Dispatcher:MaximumDeliveriesPerBatch` | 25 | 1–100 | Maximum deliveries claimed by one batch |
| `EmailDelivery:Dispatcher:PollIntervalMilliseconds` | 2,000 | 100–60,000 ms | Delay after a batch that did not fill its claim limit |
| `EmailDelivery:Dispatcher:LeaseDurationSeconds` | 120 | 30–3,600 seconds | Ownership period for a claimed delivery |
| `EmailDelivery:Dispatcher:TransitionTimeoutSeconds` | 15 | 5–60 seconds | Separate bound for persisting the outcome after an SMTP attempt |
| `EmailDelivery:Dispatcher:InitialRetryDelaySeconds` | 30 | 1–86,400 seconds | Initial transient-failure retry delay |
| `EmailDelivery:Dispatcher:MaximumRetryDelaySeconds` | 1,800 | 1–86,400 seconds | Exponential-backoff cap; must not be below the initial delay |
| `EmailDelivery:Dispatcher:RetryJitterRatio` | 0.2 | 0–0.5 | Deterministic retry jitter ratio |

Worker startup validates individual ranges, retry-delay ordering, and that the delivery lease exceeds the SMTP timeout plus the outcome-transition timeout and cleanup margin. A full batch is followed immediately by another claim pass; an under-filled or empty batch waits for the poll interval. Permanent failures and exhausted attempts become `DeadLettered` and are visible in the redacted delivery monitor. No dedicated dead-letter requeue operation exists, so operators should alert on these rows and follow a reviewed recovery procedure rather than updating control-plane tables by hand. Email dispatch may run on multiple Workers: row leases prevent simultaneous ownership of one delivery while unexpired, and a held profile-row lock plus the unexpired sending-lease count enforces each profile's claim-admission limit. This does not imply exactly-once SMTP delivery; acceptance can remain ambiguous if persistence fails after the relay accepts a message.

### Worker publication-apply settings

| Setting | Default | Valid range | Purpose |
| --- | ---: | ---: | --- |
| `PublicationApply:PollIntervalMilliseconds` | 2,000 | 100–60,000 ms | Delay after no work, a lost lease, or an unexpected processor failure; completed/conflicted/failed sets are followed immediately by another claim |

Each process creates a stable-for-process worker ID. The processor uses a ten-minute apply lease, renews it before mutation and every 25 changes, and also honors the SQL connection's `MaximumBatchRows`. Those lease/heartbeat values are current implementation constants rather than deployment settings. Role grants are checked when users load, stage, and review; revoking a role does not cancel a change set that was already approved, so operators must explicitly resolve approved work when access policy changes.

### Worker scheduler settings

| Setting | Default | Valid range | Purpose |
| --- | ---: | ---: | --- |
| `Scheduler:ReconciliationIntervalSeconds` | 15 | 5–300 | Interval for projecting published THub schedules into Quartz and removing stale jobs |
| `Scheduler:MaxConcurrency` | 10 | 1–100 | Quartz thread-pool concurrency; this is not a workflow-execution concurrency limit |
| `Scheduler:DatabaseRetryIntervalSeconds` | 15 | 1–300 | Delay before Quartz retries unavailable job-store access |
| `Scheduler:ClusterCheckinIntervalSeconds` | 10 | 5–300 | Scheduler instance heartbeat interval |
| `Scheduler:ClusterCheckinMisfireThresholdSeconds` | 20 | 5–300 | Delay after which a cluster instance is considered failed or restarted |

Environment-variable overrides use normal .NET configuration names, for example `Scheduler__MaxConcurrency` and `Serilog__FilePath`.

### Scheduling behavior

- Reconciliation creates one durable Quartz job and one one-shot trigger for each published scheduled workflow.
- Cronos evaluates THub's five-field cron expression and time-zone ID; Quartz cron syntax is not exposed as the product contract.
- Each trigger persists its exact logical occurrence. THub records that value as `WorkflowRun.ScheduledForUtc` and rejects a duplicate occurrence through a filtered unique index.
- A stale trigger revalidates workflow status and version before enqueueing, so pausing or republishing a workflow prevents obsolete work from becoming a run.
- After downtime, the missed one-shot occurrence fires once. The following occurrence is calculated from the recovery evaluation time rather than replaying an unbounded backlog.
- Stop the Windows Service gracefully when possible. The Quartz hosted service waits for active jobs during normal host shutdown.

Quartz scheduler clustering and workflow-run ownership are separate. Multiple Workers can claim from one control plane because the run store uses an atomic row claim, a per-workflow SQL application lock, active-lease exclusion, heartbeats, and lease-checked writes; an expired running lease is recovered before newer queued work. Treat production scale-out as unverified until representative SQL contention, clock synchronization, worker termination, and ambiguous target-outcome tests pass in the deployment environment.

Do not edit production credentials into checked-in `appsettings` files or publish output. LocalDB must never be used as a production or shared-environment control plane.

## Health and monitoring

The Web and Publications hosts expose `/healthz` as basic liveness endpoints. Neither currently proves SQL, source-object, or Worker readiness. Before production, add separate liveness/readiness checks and protect detailed dependency information.

Required operational signals:

- Web request/error rate and Blazor circuit failures.
- Publication-host request/error state plus token authentication/rejection, accepted-use, admission rejection, schema-drift, source-query, latency, and response-size signals. Dedicated metrics still need production integration; never attach bearer/filter/row values.
- Worker process/service state, Quartz cluster check-ins, misfires, and reconciliation failures.
- SQL connectivity and query latency.
- Queue depth, oldest queued age, due-schedule lag, and abandoned leases.
- Workflow/step successes, failures, retries, cancellations, durations, and row counts.
- Email outbox depth, oldest due age, attempt counts, retry schedule, lease conflicts, delivery latency, and dead letters; staged change-set queue age/status, apply conflicts/failures, and ambiguous-apply reconciliation also require operator dashboards/alerts.
- Disk/file-root capacity and file-processing quarantine counts.

Serilog writes structured console output and daily rolling JSON files by default. Each file rolls at 50 MB and 14 files are retained. Create `%PROGRAMDATA%\THub\Logs` and grant each executable host identity write access only to its required destination, or configure other approved absolute paths. Route logs to centralized storage with retention and redaction controls. Windows Event Log is suitable for service lifecycle/errors but should not be the only workflow telemetry store.

Every workflow node attempt emits a structured operation trace after its matching
durable execution transition succeeds. The trace covers start, bounded aggregate
progress, retry, success, failure, cancellation, and skip outcomes. The Worker enables
Debug only for the operation-trace source so progress is captured without increasing
framework logging globally. All future executable operations must follow the
[workflow operation tracing convention](operation-tracing.md), including its stable
field names and prohibition on payloads, secrets, settings, SQL, headers, URLs, and
paths.

Default file names are `thub-web-.json`, `thub-worker-.json`, and `thub-publications-.json`. Development writes to each project's `logs` directory, which is excluded from Git. Production should alert on unwritable log destinations and disk pressure; local rolling files are a resilience buffer, not a substitute for centralized searchable telemetry.

## Recovery

- Web: restart/recycle; durable state remains in SQL Server.
- Publications: restart/recycle; active publication/version/token state reloads from SQL Server. An admitted and metered request whose source read was interrupted is retried by the caller and counted again.
- Worker: stop new claims and shut down gracefully when possible. If a process exits mid-run, its unexpired lease temporarily protects ownership; after expiry another Worker claims the run, marks any abandoned running step attempt failed, and executes the immutable graph again from the beginning. A target action accepted before the crash may therefore occur again.
- Email outbox: an expired sending lease is claimable again. If SMTP accepted the message but the delivered transition did not commit, recovery can send a duplicate; stable message IDs reduce correlation cost but cannot provide exactly-once delivery. Dead letters require operator review.
- Editor change sets: approved sets are claimed and applied transactionally to one source database. A constraint or optimistic-concurrency miss becomes `Conflict`. A stale `Applying` set is failed for manual reconciliation because automatically replaying after an ambiguous source commit could duplicate an insert.
- Database: restore according to organizational RPO/RTO; workflow definitions, schedules, run state, and audit data are control-plane assets.
- Configuration/secrets: back up through their owning systems, not by copying secrets into the THub database.

Quartz is configured for clustered schedule coordination and scheduled occurrences are idempotent in THub. Multiple scheduler/Worker instances may share the same database when their clocks are synchronized: Quartz coordinates trigger firing and THub leases coordinate run execution. Before production scale-out, verify SQL application-lock permissions, contention, lease-loss cancellation, and recovery under abrupt process failure with representative connectors.
