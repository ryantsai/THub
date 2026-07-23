# THub architecture guidance for coding agents

This is a conditional architecture reference for coding agents. The root `AGENTS.md`
defines behavioral rules and says when this document must be read.

## Mission and current scope

THub is a Windows-hosted visual data workflow orchestration platform. The Web app is
the management control plane, separate Worker and Publications hosts own their accepted
runtime boundaries, and SQL Server is authoritative state. Use the current
`README.md` capability matrix rather than duplicating implementation status here.

## Read before changing code

After the root `AGENTS.md` routes a task here, read only the architecture material that
governs the requested change:

1. `docs/architecture.md` for system boundaries or cross-project changes.
2. The relevant focused document:
   - persistence/graph changes: `docs/data-model.md`
   - authentication, connectors, processes, webhooks, or publications: `docs/security.md`
   - hosting, configuration, worker, or operations: `docs/deployment.md`
3. `docs/adr/README.md` and only the ADRs that govern the task.
4. `docs/product-decisions.md` when the task touches an unresolved choice.

Do not silently choose an unresolved product option that materially changes security, deployment, execution semantics, or the data model. Implement conservative scaffolding or ask for direction.

## Architecture rules

Dependencies must continue to point inward:

```text
Web/Worker -> Application -> Domain
Web/Worker -> Infrastructure -> Application/Domain
```

### `THub.Domain`

- Own entities, value concepts, invariants, and state transitions.
- Keep it independent of EF Core, ASP.NET Core, Blazor, hosting, filesystem, and network APIs.
- Do not add data annotations for persistence/UI concerns.

### `THub.Application`

- Own use cases, ports/interfaces, validation, scheduling/execution policies, and DTOs that cross application boundaries.
- Depend on Domain, not Infrastructure or Web.
- Keep connector abstractions tabular, bounded, asynchronous, and cancellation-aware.
- Do not expose EF entities or `DbContext` through application contracts.

### `THub.Infrastructure`

- Implement application ports for EF Core/SQL Server, files, HTTP, secrets, and connectors.
- Keep product authorization decisions out of adapters.
- Use `IDbContextFactory<THubDbContext>` in background/long-lived scopes.
- Avoid leaking infrastructure types into Domain/Application contracts.

### `THub.Web`

- Own composition, Blazor components, Windows authentication, authorization handlers, and HTTP boundaries.
- Keep data access and business rules out of Razor components.
- Use Radzen Blazor as the main UI kit; custom CSS/SVG/HTML is appropriate for the workflow canvas.
- Do not perform long-running connector/execution work in a request or Blazor circuit.
- Enforce permissions server-side even if UI elements are hidden with `AuthorizeView`.

### `THub.Worker`

- Own host/service lifecycle and invoke Application services.
- Do not duplicate domain/application rules in the worker project.
- Respect cancellation and use scopes/factories for scoped infrastructure.
- Quartz coordinates schedule firing across clustered worker instances, but queued workflow execution still has no claim/lease model. Do not imply multi-worker execution safety until claim/lease support exists.
- Keep Quartz job/trigger data limited to workflow identity, version, cron/time-zone metadata, and the logical scheduled occurrence. Reload and validate authoritative workflow state through Application ports when a trigger fires.
- Preserve THub's five-field cron contract through `ScheduleCalculator`; do not silently replace it with Quartz cron syntax.

## Platform and coding conventions

- Target `net10.0`; do not introduce preview .NET APIs or retarget projects without an accepted ADR.
- Nullable reference types and implicit usings are enabled.
- Warnings are errors. Do not suppress warnings broadly to make a build pass.
- Prefer clear feature-oriented namespaces and small cohesive types.
- Use dependency injection and options binding/validation for configuration.
- Use `DateTimeOffset` and UTC for persisted instants. Store an explicit time-zone ID for schedules.
- Pass `CancellationToken` through I/O and long-running application paths.
- Use structured `ILogger` messages; never concatenate or log secrets/row payloads.
- Keep application code on `ILogger<T>` abstractions. Serilog bootstrap and sink configuration belong in the executable hosts.
- Do not add a framework/library when the platform already provides a suitable feature unless the tradeoff is documented.
- Preserve user changes and avoid unrelated formatting or refactors.

## Workflow invariants

- A workflow version is a directed acyclic graph.
- Node IDs must be unique and edge endpoints must exist.
- Publishing must validate the graph and freeze an immutable version.
- A run must reference the exact version queued for execution.
- Revalidate at the worker execution boundary; persisted JSON is not automatically trusted.
- Serialized graphs/import-export packages require an explicit schema version.
- Assume at-least-once effects. Do not claim exactly-once delivery across external systems.

When adding a node kind, update the domain enum/model, configuration contract, validation, UI toolbox/properties, serialization compatibility, execution adapter, authorization implications, tests, and relevant documentation. A placeholder UI node must be labeled as non-operational until its executor exists.

## Database and migrations

- SQL Server is the authoritative control plane.
- Quartz persistence lives in the `quartz` schema and is managed by reviewed THub migrations. Do not write directly to Quartz tables from application code.
- Development/debugging uses `THub.Debug` on `(localdb)\MSSQLLocalDB`; published environments must receive a real SQL Server connection from deployment configuration.
- Keep LocalDB settings in `appsettings.Development.json` and excluded from publish output. Do not add a fallback connection string to base settings.
- EF mappings/migrations belong in `THub.Infrastructure`.
- Migrations live in `src/THub.Infrastructure/Persistence/Migrations`.
- Use `THub.Web` as the EF startup project.
- Never use `EnsureCreated` in application startup.
- Never edit only the EF model snapshot; create a meaningful migration.
- Explicitly configure lengths, indexes, conversions, delete behavior, and large text/JSON types.
- Review generated migrations for data loss, table scans, long locks, and broad cascades.
- Runtime identities should not need schema-owner permissions.

Migration command:

```powershell
dotnet tool restore
dotnet tool run dotnet-ef migrations add MeaningfulName `
  --project src/THub.Infrastructure `
  --startup-project src/THub.Web `
  --output-dir Persistence/Migrations
```

## Authentication and authorization

- Production authentication remains Negotiate/Windows Authentication unless an ADR supersedes it.
- Prefer named permission policies from `Permissions` over raw role checks.
- Page/component authorization is not sufficient; protect HTTP/application operations too.
- Never accept a caller-selected identity in the development handler.
- `Authentication:DevelopmentBypass` must remain Development-only and loopback-only.
- Placeholder `CONTOSO` groups must never be presented as production configuration.
- Per-resource grants and external API identities are unresolved architecture work, not something to approximate casually.

## Connector and execution safety

### SQL Server

- Parameterize values.
- Validate identifiers against discovered/approved metadata; identifiers cannot be parameterized safely by treating them as values.
- Bound previews, batches, timeouts, and concurrency.
- Treat insert, merge, replace, update, and delete as distinct capabilities.

### Files

- Resolve canonical paths under configured roots and verify containment.
- Reject traversal, unrestricted absolute paths, device paths, and unapproved UNC roots.
- Bound file/workbook/row/sheet sizes and parsing errors.
- Do not load arbitrarily large workbooks or datasets fully into memory.

### Webhooks

- Use `IHttpClientFactory`, timeouts, bounded bodies, approved destinations, and secret references.
- Account for SSRF and redirects.
- Do not log sensitive headers or bodies.

### Executables

- Follow [ADR-0008](docs/adr/0008-gate-webhooks-and-executables.md) before implementing webhook or executable runtimes.
- Keep execution disabled until an administrator-owned allow-list/sandbox policy is implemented.
- Never build a shell command from workflow input.
- Do not invoke `cmd`, PowerShell, or arbitrary binaries for a workflow author.
- Require canonical paths, typed argument templates, restricted identity, time/output limits, process-tree cancellation, and audit events.

### REST/editor publications

- ADR-0007 is Proposed. Do not implement a generic arbitrary-table/SQL CRUD API.
- Default any future REST publication to read-only.
- Require explicit object/column/operation/auth policies, bounds, rate limits, and audit.
- Do not implement write-capable editors until direct-vs-staged writes and consumer authentication are decided.

## UI expectations

- Keep the interface dense, calm, accessible, and suitable for an operations control room.
- Reuse existing design tokens and layout styles before creating a new visual system.
- Use semantic buttons/labels/headings and preserve keyboard/focus behavior.
- Keep responsive behavior usable; the designer may scroll, but controls must not become unreachable.
- Do not rely on color alone for workflow/run state.
- Move persistence and validation behavior to injected services as those slices are implemented.

For meaningful UI changes, recommend a real-browser check covering page title,
navigation, primary interaction, authorization behavior, responsive layout when
relevant, and browser console errors. Run it only when the user explicitly authorizes
validation under the repository `AGENTS.md`.

## Tests and validation

Behavior changes require proportionate tests:

- Domain invariants/state transitions: `THub.Domain.Tests`.
- Application validation, scheduling, and use cases: `THub.Application.Tests`.
- Quartz schedule mapping and worker composition logic: `THub.Worker.Tests`.
- SQL behavior: add integration tests against a relational database; do not rely on EF's in-memory provider for SQL semantics.
- Authentication/HTTP pipeline: use ASP.NET Core integration tests in `THub.Web.Tests`.
- Blazor interaction: use browser automation for high-value flows.

When the user authorizes full validation, the standard repository commands to suggest
are:

```powershell
dotnet format THub.slnx --verify-no-changes
dotnet build THub.slnx
dotnet test THub.slnx --no-build
```

The supported VS Code compound profile is `THub: Debug All`. Keep `.vscode/launch.json`, `.vscode/tasks.json`, `scripts/prepare-debug.ps1`, launch URLs, target framework paths, Quartz schema setup, and Development database setup synchronized when host projects or framework versions change.

For dependency changes, also suggest:

```powershell
dotnet list THub.slnx package --vulnerable --include-transitive
```

Do not initiate these commands without explicit user authorization. If they were not
authorized or cannot run, state exactly what remains unverified and why.

## Documentation and ADR policy

Update documentation in the same change when behavior, configuration, commands, support boundaries, or operational assumptions change.

Create a new ADR when changing:

- process/service boundaries;
- target framework, UI model, database, queue, scheduler, or connector architecture;
- authentication/authorization or secret strategy;
- workflow serialization/execution semantics;
- publication trust boundaries;
- deployment topology or scale assumptions.

Do not rewrite an accepted ADR to hide a reversed decision. Add a new ADR with `Superseded` links and update the ADR index. Add unresolved choices to `docs/product-decisions.md` until they are decided.

## Definition of done

A task is complete when:

- implementation matches the requested scope and accepted ADRs;
- security boundaries are enforced, not merely documented;
- tests are added at the appropriate layer when behavior changes;
- explicitly authorized validation passes, or the handoff clearly identifies it as
  recommended and unverified;
- relevant docs/configuration/migrations are updated;
- unfinished behavior is explicitly labeled rather than represented as working.
