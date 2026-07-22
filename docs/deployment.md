# Deployment and operations

## Recommended topology

The initial production topology is Windows Server with IIS hosting `THub.Web`, one `THub.Worker` Windows Service, and an existing SQL Server instance.

```text
Corporate browser
      |
      | HTTPS + Windows Authentication
      v
IIS / THub.Web  -------------------+
                                      \
                                       > SQL Server / THub control plane
                                      /
THub.Worker Windows Service --------+  (Quartz clustered scheduler)
      |
      +---- approved SQL Server databases
      +---- approved local/UNC file roots
      +---- approved outbound webhook destinations
```

Web and worker can initially share a host, but they are independently deployable processes. Use separate service identities even when co-located.

## Environment assumptions to confirm

- Whether IIS, worker, SQL Server, and users are in one AD forest/domain.
- Whether source SQL connections use Windows integrated authentication.
- Whether file locations include UNC shares.
- Whether outbound internet access is allowed.
- Which account owns database migrations.
- Certificate source and renewal procedure.

These decisions affect SPNs, delegation, firewall rules, service accounts, and secret storage.

## Web deployment

Recommended controls:

- Publish a Release build and host behind IIS with Windows Authentication enabled and anonymous authentication disabled, except intentionally anonymous health probes.
- Use HTTPS, HSTS, appropriate host filtering, and forwarded headers only when a trusted proxy exists.
- Persist ASP.NET Core data-protection keys to an access-controlled durable location if multiple web instances are introduced.
- Configure production AD group mappings and remove the permissive default role when required by policy.
- Do not enable `Authentication:DevelopmentBypass`.
- Grant the web application pool identity only the THub database rights needed for management operations.

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

## Configuration

For local Development/debugging, both hosts use `THub.Debug` on `(localdb)\MSSQLLocalDB`. This development connection is defined only in `appsettings.Development.json`, and those files are excluded from publish output.

Every published environment must provide the same real SQL Server `ConnectionStrings:THub` value to both hosts through environment-specific external configuration or an organization-approved secret/configuration provider. Base `appsettings.json` files intentionally contain no fallback connection string, so a missing deployment value fails at startup instead of silently connecting to a developer database.

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
  "Serilog": {
    "FilePath": "%PROGRAMDATA%\\THub\\Logs\\thub-worker-.json"
  }
}
```

`ReconciliationIntervalSeconds` controls how quickly changed THub schedule metadata is reflected in Quartz; it is not a due-work polling interval. Quartz persists timing and cluster state in the `quartz` schema. Both runtime hosts also support `Serilog:FilePath`; relative paths resolve from the host content root and environment variables are expanded.

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

Quartz schedule clustering does not authorize multiple workers to execute queued runs. Keep workflow execution single-worker until THub run claims, leases, attempts, and abandoned-run recovery are implemented.

Do not edit production credentials into checked-in `appsettings` files or publish output. LocalDB must never be used as a production or shared-environment control plane.

## Health and monitoring

The web application exposes `/healthz` as a basic liveness endpoint. It currently does not prove SQL readiness. Before production, add separate liveness/readiness checks and protect detailed dependency information.

Required operational signals:

- Web request/error rate and Blazor circuit failures.
- Worker process/service state, Quartz cluster check-ins, misfires, and reconciliation failures.
- SQL connectivity and query latency.
- Queue depth, oldest queued age, due-schedule lag, and abandoned leases.
- Workflow/step successes, failures, retries, cancellations, durations, and row counts.
- Disk/file-root capacity and file-processing quarantine counts.

Serilog writes structured console output and daily rolling JSON files by default. Each file rolls at 50 MB and 14 files are retained. Create `%PROGRAMDATA%\THub\Logs` and grant the web/worker identities write access, or configure another approved absolute path. Route logs to centralized storage with retention and redaction controls. Windows Event Log is suitable for service lifecycle/errors but should not be the only workflow telemetry store.

Default file names are `thub-web-.json` and `thub-worker-.json`. Development writes to each project's `logs` directory, which is excluded from Git. Production should alert on unwritable log destinations and disk pressure; local rolling files are a resilience buffer, not a substitute for centralized searchable telemetry.

## Recovery

- Web: restart/recycle; durable state remains in SQL Server.
- Worker: restart; Quartz recovers persisted schedules and fires one missed occurrence, while queued THub runs remain. Automated abandoned-run recovery requires the future lease model.
- Database: restore according to organizational RPO/RTO; workflow definitions, schedules, run state, and audit data are control-plane assets.
- Configuration/secrets: back up through their owning systems, not by copying secrets into the THub database.

Quartz is configured for clustered schedule coordination and scheduled occurrences are idempotent in THub. Multiple scheduler instances may share the same database when their clocks are synchronized. Do not enable queued workflow execution on multiple workers until THub run claim/lease and scale-out behavior is implemented and tested.
