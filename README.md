# THub

THub is an intranet-first data workflow orchestration and visual-design platform inspired by SSIS, n8n, DolphinScheduler, Kestra, and Azure Data Factory. Its workflow connector boundary includes SQL Server, MySQL, PostgreSQL, Oracle Database, local CSV/Excel files, and FTP/FTPS CSV, tab-delimited, and Excel files.

> **Repository status:** functional v1 foundation. The persisted designer/catalog, schema-versioned graph validation, immutable checksummed workflow versions, manual and scheduled run queues, SQL-leased Worker execution, durable step attempts, bounded relational/local-file/FTP nodes, select/filter/join transforms, Email alerts, and governed SQL Server publications are implemented. Webhook, executable, and publication canvas nodes remain intentionally non-operational; production readiness still requires deployment-specific identities, secrets, live connector verification, plus readiness/metrics, audit, and retention work.

## Architecture at a glance

```text
Windows user -------> THub.Web ----------------------------------> SQL Server control plane
Internal API client -> THub.Publications ------------------------> SQL Server + approved source tables
THub.Worker (Quartz + leased execution/background work) --------> SQL Server + approved sources/targets
```

The web application owns Windows-authenticated management, persisted workflow design, Email policy administration, publication administration, and bounded Spreadsheet-editor interactions. The worker owns durable scheduling and leased workflow execution as well as Email outbox delivery and approved staged editor writes. The isolated publication host owns internal read-only REST traffic authenticated with managed opaque bearer tokens. SQL Server is their coordination boundary. See the full [architecture description](docs/architecture.md) and [architecture decision records](docs/adr/README.md).

Quartz owns schedule timing, persistence, misfire handling, and scheduler cluster coordination only. THub remains authoritative for immutable workflow versions, queued/running runs, execution leases, cancellation, and step attempts. A Quartz fire is therefore a request to create a THub run, not the run itself.

## Capability status

| Capability | Status | Notes |
| --- | --- | --- |
| Windows Authentication | Implemented | Negotiate in normal environments; explicit loopback-only development bypass available |
| SQL-backed RBAC | Implemented | Windows users/groups map to System Administrator, Developer, or custom roles with global and resource-specific grants |
| Blazor/Radzen application shell | Implemented | Dashboard and management views are present |
| Visual workflow designer | Implemented | Create/load/save/publish/pause/archive persisted workflows, configure/connect nodes, and detect optimistic draft-revision conflicts |
| Workflow graph validation | Implemented | Explicit schema version, size bounds, IDs, endpoints, cardinality, cycles, typed per-node settings, and operational-policy checks |
| SQL Server metadata schema | Implemented | The migration chain includes workflows, runs, connections, durable alerts, and governed publication versions/tokens/grants/change sets |
| Cron/time-zone scheduling | Implemented | Quartz persists one trigger per published schedule; THub transactionally owns queued runs |
| Designer save/load/publish | Implemented | Draft graph JSON is canonicalized and saved with optimistic revision checks; publish creates and selects an immutable checksummed version transactionally |
| Workflow lifecycle and packages | Implemented | Authorized archive preserves history; permanent delete is limited to unused drafts; versioned JSON export/import redacts connection configuration and imports as a new unpublished draft |
| Immutable workflow versions and execution leases | Implemented | Runs reference exact versions; Workers atomically claim/heartbeat runs, recover expired leases, honor durable cancellation, and persist lease-checked step attempts |
| Relational/file execution | Implemented v1 | SQL Server, MySQL, PostgreSQL, and Oracle table/view sources and transactional insert targets; bounded local and FTP/FTPS CSV, tab-delimited, and modern Excel sources plus create-new targets |
| Connection authentication | Implemented v1 | SQL Server supports Windows integrated or referenced credentials; other databases and FTP use referenced username/password credentials through a replaceable external configuration resolver |
| Schema mapping and transforms | Implemented v1 | The designer opens live SQL Server/MySQL/PostgreSQL/Oracle source and target schemas, selects source columns, and edits target mappings graphically; advanced JSON remains available for import/troubleshooting |
| Workflow variables and expressions | Implemented v1 | Destination bindings can use source columns, run values, typed workflow globals, bounded one-row database lookups, or constrained reusable JavaScript expressions |
| Email alerts/actions | Implemented | Administrator-managed profiles/rules, terminal-event and `EmailAlert` action enqueueing, deduplicated SQL outbox, leased Worker dispatch, bounded retries/dead letters, and MailKit SMTP delivery; credential-backed SMTP requires an approved `ISecretResolver` deployment integration |
| Webhook/executable execution | Gated | Draft node concepts remain visible, but publish and execution reject them until administrator allow-list, secret, timeout, identity, and audit policies exist |
| Publication canvas nodes | Gated and separated | `PublishRestApi` and `PublishDataEditor` cannot run in a workflow; create the implemented governed resources under Publications instead |
| Isolated REST publication host | Implemented | Separate read-only `/schema` and `/rows` routes use managed bearer tokens, atomic accepted-use metering, typed filters/sorts, keyset cursors, schema checks, response/time limits, and process-local admission |
| Role-governed Spreadsheet editor | Implemented | Independent View/Insert/Update/Delete/Approve grants, bounded windows, foreign-key lookup cells, typed staging/review, and worker-applied optimistic-concurrency change sets |

## Technology

- .NET 10 / ASP.NET Core 10
- Blazor Web App with global Interactive Server rendering
- Radzen Blazor 11
- Separate ASP.NET Core publication host for the internal read-only REST boundary
- .NET Worker hosted as a Windows Service
- EF Core 10 with Microsoft SQL Server
- Quartz.NET 3.18 with a clustered SQL Server job store
- MailKit for bounded TLS-protected SMTP delivery
- Cronos for the product's five-field cron calculation
- Serilog structured console and rolling JSON file logging
- MySqlConnector, Npgsql, and Oracle Managed Data Access Core for relational connectors
- FluentFTP for FTP/FTPS transfers
- CsvHelper and ClosedXML for bounded CSV and modern Excel connector execution
- xUnit for unit tests
- Jint for bounded expression-only JavaScript value evaluation

## Repository layout

| Path | Responsibility |
| --- | --- |
| `src/THub.Web` | Blazor UI, Negotiate authentication, permission policies, internal endpoints |
| `src/THub.Publications` | Isolated managed-bearer read-only REST API and process-local request admission |
| `src/THub.Worker` | Windows Service host, Quartz reconciliation, leased workflow execution, Email dispatch, and approved editor apply |
| `src/THub.Application` | Use cases, graph/settings validation, execution planning/policies, bounded tabular contracts, and publication/alert ports |
| `src/THub.Domain` | Workflow/version/run/step, connection, alert, and publication domain models |
| `src/THub.Infrastructure` | EF Core/SQL persistence, migrations, connector executors, lease stores, file safety, SMTP, and publication adapters |
| `tests/THub.Domain.Tests` | Domain behavior tests |
| `tests/THub.Application.Tests` | Application service tests |
| `tests/THub.Web.Tests` | ASP.NET Core authentication and request-pipeline integration tests |
| `tests/THub.Publications.Tests` | Publication-host pipeline tests |
| `tests/THub.Worker.Tests` | Quartz schedule mapping and worker integration unit tests |
| `docs` | Architecture, ADRs, product decisions, and documentation index |
| `scripts` | Operational helper scripts |

The dependency direction is `Web/Publications/Worker -> Infrastructure/Application -> Domain`. `Domain` must remain framework-independent.

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

   Development host settings point to the same `THub.Debug` LocalDB database. LocalDB uses the same EF Core SQL Server provider and migrations as the actual SQL Server environment. Web, Worker, and Publications all load their control-plane state from this database in Development.

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

6. Start the publication host in a third terminal when testing a REST publication:

   ```powershell
   dotnet run --project src/THub.Publications --launch-profile https
   ```

   Create and activate a REST publication under `/publications`, create a named expiring token, and copy its plaintext value from the one-time response. The separate host exposes:

   ```text
   GET /api/v1/publications/{slug}/schema
   GET /api/v1/publications/{slug}/rows?pageSize=100&filter=status:eq:Ready&sort=-createdAt
   Authorization: Bearer thub_<selector>.<secret>
   ```

   The rows route accepts only `pageSize`, a server-issued `cursor`, up to 16 repeated `filter` values, and up to 8 repeated `sort` values. Filter syntax is `alias:operator:value`; `isNull` and `isNotNull` omit the value. Publication-version settings and the SQL connection's batch limit bound every request.

Base settings intentionally contain no database connection string. Every non-Development Web, Worker, and Publications environment must provide `ConnectionStrings:THub` through approved deployment configuration. Use separate least-privilege host identities: Publications needs control-plane token/metering access and approved source-read access, while Worker alone receives approved editor source-write access. Development settings are excluded from publish output.

## VS Code: debug everything

1. Open the repository root in VS Code.
2. Install the recommended **C# Dev Kit** extension when prompted.
3. Open **Run and Debug** (`Ctrl+Shift+D`).
4. Select **THub: Debug All** and press `F5`.

The compound profile runs one preparation task, then starts the web host, worker, and publication host under separate .NET debugger sessions. Preparation automatically:

- restores local .NET tools and NuGet packages;
- starts `MSSQLLocalDB`;
- builds the Debug solution;
- applies EF Core migrations to `THub.Debug`.

The web application opens automatically at its HTTPS development URL. Stopping a compound debugger stops the complete session. Web-only, Worker-only, and Publications-only profiles are also available. The VS Code web profiles explicitly enable the loopback-only Development identity so browser differences in Negotiate support do not block F5 debugging. Development logs are written beneath each executable project's `logs` directory; those directories are ignored by Git.

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
- Published Web, Worker, and Publications environments must supply a real SQL Server `ConnectionStrings:THub`; startup fails fast when it is missing.
- The publication host requires its own least-privilege control-plane identity plus Windows-integrated read access only to source objects approved by active REST publications. Keep source-write access on the Worker identity.
- Production logs default to `%PROGRAMDATA%\THub\Logs`. Grant each host identity write access or override `Serilog:FilePath` through deployment configuration.
- Configure initial administrator/developer users or AD groups under `Authorization:Bootstrap`; checked-in production arrays are intentionally empty.
- Persisted custom roles and resource grants are managed under `/settings`. Authenticated users without a bootstrap or persisted role assignment receive no access.
- Never put source-system passwords or tokens in `DataConnection.ConfigurationJson`; store a protected secret reference. Plain FTP is supported only as an explicit compatibility mode and exposes both credentials and data in transit; prefer explicit or implicit FTPS.
- For a database-authenticated connection reference such as `warehouse_reader`, supply `ConnectionCredentials__warehouse_reader__Username` and `ConnectionCredentials__warehouse_reader__Password` to each authorized host through external deployment configuration or a vault-backed .NET configuration provider.
- Managed publication tokens are returned once and stored only as one-way verifiers; their list view exposes status, expiry/revocation, accepted-use count, and last-used time but never the bearer secret. Email delivery profiles store SMTP credential references, never secret values; the checked-in resolver fails closed for referenced credentials until deployment replaces it with an organization-approved provider. Profiles without a reference use only an explicitly approved anonymous relay.
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
