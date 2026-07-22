# THub

THub is an intranet-first data workflow orchestration and visual-design platform inspired by SSIS, n8n, DolphinScheduler, Kestra, and Azure Data Factory. The v1 product boundary is Microsoft SQL Server plus local CSV and Excel files.

> **Repository status:** executable foundation. Authentication, authorization, the Blazor shell, in-memory graph designer, graph validation, SQL Server metadata model, migrations, Quartz scheduling, structured Serilog output, and scheduled-run enqueueing are implemented. Workflow persistence from the designer, schema discovery/mapping, node execution, run leasing, publications, and the online editor are not implemented yet.

## Architecture at a glance

```text
Windows user
    |
    v
THub.Web (Blazor Interactive Server + Radzen)
    |
    v
SQL Server control plane
    ^
    |
THub.Worker (Windows Service + persistent Quartz scheduler; execution host in later slices)
```

The web application owns management and design interactions. The worker owns durable background work. SQL Server is their coordination boundary. See the full [architecture description](docs/architecture.md) and [architecture decision records](docs/adr/README.md).

Quartz owns schedule timing, persistence, misfire handling, and cluster coordination only. THub remains authoritative for workflow versions, queued runs, run state, and future step-run state. A Quartz fire is therefore a request to create a THub run, not the run itself.

## Capability status

| Capability | Status | Notes |
| --- | --- | --- |
| Windows Authentication | Implemented | Negotiate in normal environments; explicit loopback-only development bypass available |
| Permission-based RBAC | Implemented | AD/Windows groups map to Viewer, Operator, Designer, and Administrator |
| Blazor/Radzen application shell | Implemented | Dashboard and management views are present |
| Visual workflow designer | Foundation | Add/select/configure/remove nodes and validate an in-memory graph |
| Workflow graph validation | Implemented | IDs, endpoints, self-edges, and cycle detection |
| SQL Server metadata schema | Implemented | EF Core model and initial migration for workflows, runs, and connections |
| Cron/time-zone scheduling | Implemented | Quartz persists one trigger per published schedule; THub transactionally owns queued runs |
| Designer save/load/publish | Planned | UI actions are placeholders until the repository slice is built |
| SQL/CSV/XLSX execution | Planned | Libraries and node contracts exist; executors do not |
| Schema mapping and transforms | Planned | Node types exist; execution and mapping UI remain |
| Webhook/executable execution | Planned and gated | Requires allow-listing, secret, timeout, and service-identity policy |
| Generated REST API/data editor | Planned and gated | Requires publication security and auditing decisions |

## Technology

- .NET 10 / ASP.NET Core 10
- Blazor Web App with global Interactive Server rendering
- Radzen Blazor 11
- .NET Worker hosted as a Windows Service
- EF Core 10 with Microsoft SQL Server
- Quartz.NET 3.18 with a clustered SQL Server job store
- Cronos for the product's five-field cron calculation
- Serilog structured console and rolling JSON file logging
- CsvHelper and ClosedXML reserved for v1 connector implementation
- xUnit for unit tests

## Repository layout

| Path | Responsibility |
| --- | --- |
| `src/THub.Web` | Blazor UI, Negotiate authentication, permission policies, internal endpoints |
| `src/THub.Worker` | Windows Service host, Quartz reconciliation, and scheduled-run dispatch |
| `src/THub.Application` | Use-case contracts, graph validation, schedule calculation |
| `src/THub.Domain` | Workflow, run, and connection domain model |
| `src/THub.Infrastructure` | EF Core persistence, migrations, schedule projection, and THub run enqueueing |
| `tests/THub.Domain.Tests` | Domain behavior tests |
| `tests/THub.Application.Tests` | Application service tests |
| `tests/THub.Web.Tests` | ASP.NET Core authentication and request-pipeline integration tests |
| `tests/THub.Worker.Tests` | Quartz schedule mapping and worker integration unit tests |
| `docs` | Architecture, ADRs, product decisions, and documentation index |
| `scripts` | Operational helper scripts |

The dependency direction is `Web/Worker -> Infrastructure/Application -> Domain`. `Domain` must remain framework-independent.

## Prerequisites

- .NET SDK 10.0.302 or a compatible .NET 10 patch
- SQL Server Developer, Express, or an accessible existing instance
- A Windows account; production is designed for domain accounts and AD groups
- PowerShell for the documented commands and service tooling

## Local setup

1. Restore the SDK tool and packages:

   ```powershell
   dotnet tool restore
   dotnet restore
   ```

2. Confirm SQL Server LocalDB is installed:

   ```powershell
   sqllocaldb info MSSQLLocalDB
   ```

   Both Development hosts use the same `THub.Debug` LocalDB database through their checked-in `appsettings.Development.json` files. LocalDB uses the same EF Core SQL Server provider and migrations as the actual SQL Server environment.

3. Apply the metadata schema:

   ```powershell
   $env:ASPNETCORE_ENVIRONMENT = 'Development'
   dotnet tool run dotnet-ef database update --project src/THub.Infrastructure --startup-project src/THub.Web
   ```

4. Start the web application:

   ```powershell
   dotnet run --project src/THub.Web --launch-profile https
   ```

5. Start the scheduler in a second terminal:

   ```powershell
   dotnet run --project src/THub.Worker
   ```

Base settings intentionally contain no database connection string. Non-Development environments must provide `ConnectionStrings:THub` through the approved deployment configuration or secret provider. Development settings are excluded from publish output.

## VS Code: debug everything

1. Open the repository root in VS Code.
2. Install the recommended **C# Dev Kit** extension when prompted.
3. Open **Run and Debug** (`Ctrl+Shift+D`).
4. Select **THub: Debug All** and press `F5`.

The compound profile runs one preparation task, then starts the web/frontend/backend host and worker under separate .NET debugger sessions. Preparation automatically:

- restores local .NET tools and NuGet packages;
- starts `MSSQLLocalDB`;
- builds the Debug solution;
- applies EF Core migrations to `THub.Debug`.

The web application opens automatically at its HTTPS development URL. Stopping either debugger stops the complete compound session. Web-only and Worker-only profiles are also available. The VS Code web profiles explicitly enable the loopback-only Development identity so browser differences in Negotiate support do not block F5 debugging. Development logs are written beneath `src/THub.Web/logs` and `src/THub.Worker/logs`; those directories are ignored by Git.

### Automated browser testing

Negotiate authentication is not normally available to headless browsers. A test identity can be enabled explicitly for loopback development only:

```powershell
$env:Authentication__DevelopmentBypass = 'true'
dotnet run --project src/THub.Web --launch-profile http
```

The bypass is ignored outside the `Development` environment and refuses non-loopback clients. It must never be enabled in deployed configuration.

To test real Windows/Negotiate authentication locally, run the `https` launch profile without setting `Authentication__DevelopmentBypass`; the checked-in Development setting remains `false`.

## Build and test

```powershell
dotnet format THub.slnx --verify-no-changes
dotnet build THub.slnx
dotnet test THub.slnx --no-build
```

Warnings are treated as errors through `Directory.Build.props`.

## Database migrations

Create a migration only after intentionally changing the EF Core model:

```powershell
dotnet tool run dotnet-ef migrations add DescriptiveName `
  --project src/THub.Infrastructure `
  --startup-project src/THub.Web `
  --output-dir Persistence/Migrations
```

Review generated SQL and the model snapshot. Never edit an already-deployed migration; add a forward migration.

## Windows Service publishing

```powershell
dotnet publish src/THub.Worker -c Release -r win-x64 --self-contained false -o artifacts/worker
./scripts/install-worker.ps1 -PublishDirectory ./artifacts/worker -Credential 'DOMAIN\svc-thub'
```

The service identity needs only the SQL Server and file-system permissions required by its configured workflows.

The migration creates both THub metadata in the `thub` schema and Quartz operational tables in the `quartz` schema. Runtime identities require data access to both schemas but do not require schema modification rights.

## Configuration and security

- LocalDB is for Development/debugging only. Never use `(localdb)\MSSQLLocalDB` in a published environment.
- Published web and worker environments must supply a real SQL Server `ConnectionStrings:THub`; startup fails fast when it is missing.
- Production logs default to `%PROGRAMDATA%\THub\Logs`. Grant each host identity write access or override `Serilog:FilePath` through deployment configuration.
- Replace the placeholder `CONTOSO\THub ...` groups under `Authorization:RoleMappings`.
- Production currently gives an authenticated unmapped account the configured default role (`Viewer`). Use an invalid/empty default when explicit group membership must be mandatory.
- Never put source-system passwords or tokens in `DataConnection.ConfigurationJson`; store a protected secret reference.
- Treat workflow JSON, file paths, SQL object names, webhook URLs, and executable settings as untrusted input at execution boundaries.
- ClosedXML supports the intended `.xlsx`/`.xlsm` scope. Legacy `.xls` requires a separate connector decision.

## Documentation

- [Documentation index](docs/README.md)
- [Architecture](docs/architecture.md)
- [Security architecture](docs/security.md)
- [Data model](docs/data-model.md)
- [Deployment and operations](docs/deployment.md)
- [Architecture decision records](docs/adr/README.md)
- [Open product decisions](docs/product-decisions.md)
- [AI agent instructions](AGENTS.md)

When an architectural choice changes, update the architecture document and add or supersede an ADR in the same change.
