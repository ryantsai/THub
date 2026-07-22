# ADR-0002: Use Blazor Interactive Server and Radzen

- Status: Accepted
- Date: 2026-07-22
- Deciders: Project maintainers

## Context

THub is a Windows-authenticated line-of-business application with a highly interactive workflow designer, data grids, forms, schedules, and operational dashboards. The requested frontend stack is ASP.NET Core Blazor with Radzen as the main UI kit.

The application must keep privileged data access on the server and should not require a separate JavaScript SPA/API codebase for v1.

## Decision

Use the ASP.NET Core 10 Blazor Web App model with global Interactive Server rendering. Use Radzen Blazor as the primary component library and custom CSS/SVG/HTML where the workflow canvas needs purpose-built behavior.

Keep database access and business rules out of Razor components. Components call application services; server-side authorization is still required at use-case and endpoint boundaries.

## Consequences

### Positive

- One C# component model across UI and server.
- Natural integration with Windows Authentication and server-side permissions.
- No secrets or privileged connector execution in downloaded client code.
- Radzen supplies mature forms, grids, dialogs, schedulers, and feedback components.

### Negative

- Each interactive session maintains a server circuit and persistent connection.
- Network interruptions affect interactive state and must be handled deliberately.
- Horizontal scale may require sticky sessions or a suitable circuit-aware topology.
- Complex canvas interaction can still require focused JavaScript interop.
- Radzen version changes can affect generated markup and styling.

## Alternatives considered

- **Blazor WebAssembly:** rejected for v1 due to payload, duplicated API/auth concerns, and no offline requirement.
- **React/TypeScript SPA:** rejected because it conflicts with the selected full-stack .NET direction and adds a second application stack.
- **Static SSR only:** rejected because the visual designer needs sustained interactivity.

## Follow-up

- Keep canvas interaction accessible and keyboard operable.
- Add browser tests for authoring, validation, save/publish, and reconnection flows.
- Reassess per-page render modes only if measured circuit cost warrants it.

