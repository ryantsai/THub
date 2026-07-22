# ADR-0001: Build a modular monolith on .NET 10

- Status: Accepted
- Date: 2026-07-22
- Deciders: Project maintainers

## Context

THub needs a web control plane, durable worker, shared workflow model, SQL persistence, and connector implementations. The initial team and v1 scope do not justify independently deployed domain services, but a single undifferentiated project would couple UI, orchestration rules, and infrastructure.

.NET 10 is the current stable target selected for this greenfield project and is installed in the development environment.

## Decision

Build THub as a modular monolith in one solution targeting `net10.0`:

- `THub.Domain` owns domain state and invariants.
- `THub.Application` owns use cases, ports, and orchestration policy.
- `THub.Infrastructure` implements persistence and external-system adapters.
- `THub.Web` and `THub.Worker` are separate executable composition roots.

Dependencies point inward toward Application and Domain. Web and Worker may share the same code and database while remaining separately deployable processes.

## Consequences

### Positive

- Simple local development, deployment, refactoring, and transactions.
- Shared domain/application rules without network calls.
- Separate web and worker lifecycles without premature service boundaries.
- Connector and persistence implementations remain replaceable behind application ports.

### Negative

- One database and codebase increase coordination between modules.
- A poorly enforced boundary can degrade into a layered monolith with cross-project shortcuts.
- Independent scaling is limited to the web and worker processes, not individual domain services.
- Upgrading the target framework affects the whole solution.

## Alternatives considered

- **Microservices:** rejected for v1 because operational and consistency costs exceed current scale needs.
- **Single ASP.NET Core project with hosted services:** rejected because web recycling must not own durable scheduling/execution.
- **Separate repositories for web and worker:** rejected because workflow contracts and versioning are evolving together.

## Follow-up

- Enforce project boundaries in reviews and tests where valuable.
- Revisit service extraction only when measured scaling, ownership, or isolation requirements demand it.

