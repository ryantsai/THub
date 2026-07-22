# AGENTS.md

This file defines repository-wide instructions for AI coding agents working on THub. It applies to every file below the repository root unless a deeper `AGENTS.md` provides more specific rules.

## Mission and current scope

THub is a Windows-hosted visual data workflow orchestration platform. The v1 product boundary is SQL Server plus local CSV and modern Excel files (`.xlsx`/`.xlsm`). The web app is the control plane; a separate Windows worker owns durable scheduling and future execution; SQL Server is authoritative state.

This repository is a foundation under active development. Do not describe planned features as implemented. The current designer is in-memory, the worker enqueues due runs, and connector execution/publication runtimes are not yet present.

## Read before changing code

For any non-trivial task, read:

1. `README.md`
2. `docs/architecture.md`
3. The relevant focused document:
   - persistence/graph changes: `docs/data-model.md`
   - authentication, connectors, processes, webhooks, or publications: `docs/security.md`
   - hosting, configuration, worker, or operations: `docs/deployment.md`
4. `docs/adr/README.md` and each ADR that governs the task
5. `docs/product-decisions.md` when the task touches an unresolved choice

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
- The current scheduler assumes one active instance. Do not imply multi-worker scheduling safety until claim/lease support exists.

## Platform and coding conventions

- Target `net10.0`; do not introduce preview .NET APIs or retarget projects without an accepted ADR.
- Nullable reference types and implicit usings are enabled.
- Warnings are errors. Do not suppress warnings broadly to make a build pass.
- Prefer clear feature-oriented namespaces and small cohesive types.
- Use dependency injection and options binding/validation for configuration.
- Use `DateTimeOffset` and UTC for persisted instants. Store an explicit time-zone ID for schedules.
- Pass `CancellationToken` through I/O and long-running application paths.
- Use structured `ILogger` messages; never concatenate or log secrets/row payloads.
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

For meaningful UI changes, test in a real browser. Check page title, navigation, primary interaction, authorization behavior, responsive layout when relevant, and browser console errors.

## Tests and validation

Behavior changes require proportionate tests:

- Domain invariants/state transitions: `THub.Domain.Tests`.
- Application validation, scheduling, and use cases: `THub.Application.Tests`.
- SQL behavior: add integration tests against a relational database; do not rely on EF's in-memory provider for SQL semantics.
- Authentication/HTTP pipeline: use ASP.NET Core integration tests.
- Blazor interaction: use browser automation for high-value flows.

Before handing off a code change, run from the repository root:

```powershell
dotnet format THub.slnx --verify-no-changes
dotnet build THub.slnx
dotnet test THub.slnx --no-build
```

For dependency changes, also run:

```powershell
dotnet list THub.slnx package --vulnerable --include-transitive
```

If a required check cannot run, state exactly what was not verified and why.

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

## Source control and generated files

- Keep commits focused when the user asks for commits; do not commit unless requested.
- Do not discard unrelated or pre-existing user changes.
- Do not commit `bin`, `obj`, `.playwright-cli`, `output`, `artifacts`, secrets, or local settings.
- EF migration files are generated but reviewed source and should be committed with their model change.
- Avoid editing generated migration designer files except through a deliberate regenerated migration.

## Definition of done

A task is complete when:

- implementation matches the requested scope and accepted ADRs;
- security boundaries are enforced, not merely documented;
- tests cover new behavior at the appropriate layer;
- format/build/tests pass without new warnings;
- relevant docs/configuration/migrations are updated;
- unfinished behavior is explicitly labeled rather than represented as working.
