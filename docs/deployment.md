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
THub.Worker Windows Service --------+
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

## Configuration

Both hosts require the same `ConnectionStrings:THub` value. The worker also supports:

```json
{
  "Scheduler": {
    "PollIntervalSeconds": 10,
    "ErrorRetrySeconds": 30
  }
}
```

Use environment-specific external configuration or an organization-approved secret/configuration provider. Do not edit secrets into checked-in `appsettings` files or publish output.

## Health and monitoring

The web application exposes `/healthz` as a basic liveness endpoint. It currently does not prove SQL readiness. Before production, add separate liveness/readiness checks and protect detailed dependency information.

Required operational signals:

- Web request/error rate and Blazor circuit failures.
- Worker process/service state and scheduler tick failures.
- SQL connectivity and query latency.
- Queue depth, oldest queued age, due-schedule lag, and abandoned leases.
- Workflow/step successes, failures, retries, cancellations, durations, and row counts.
- Disk/file-root capacity and file-processing quarantine counts.

Route logs to centralized storage with retention and redaction controls. Windows Event Log is suitable for service lifecycle/errors but should not be the only workflow telemetry store.

## Recovery

- Web: restart/recycle; durable state remains in SQL Server.
- Worker: restart; queued work remains. Automated abandoned-run recovery requires the future lease model.
- Database: restore according to organizational RPO/RTO; workflow definitions, schedules, run state, and audit data are control-plane assets.
- Configuration/secrets: back up through their owning systems, not by copying secrets into the THub database.

The current scheduler is designed for one active scheduler instance. Do not start multiple production workers that schedule the same database until claim/lease and scale-out behavior is implemented and tested.
