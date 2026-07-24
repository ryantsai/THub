# ADR-0016: Use SQL-backed custom roles and resource authorization

- Status: Accepted
- Date: 2026-07-23
- Deciders: Project maintainers
- Supersedes: [ADR-0003](0003-windows-authentication-and-permission-policies.md)
- Amends: [ADR-0011](0011-isolated-governed-data-publications.md)

## Context

The original four configuration-mapped global roles cannot express that one Windows user or group may edit a particular workflow, inspect one connection, or use one governed editor without receiving the same authority over every resource. Configuration-only roles also require a host restart and cannot be administered transactionally with publication grants.

THub remains an intranet application authenticated by Windows/Negotiate. It does not need first-party passwords, but it does need an authoritative, flexible authorization model in the SQL Server control plane.

## Decision

Keep Windows/Negotiate as the production authentication boundary. Store authorization in SQL Server using roles, role permissions, Windows user/group assignments, per-workflow and per-connection grants, and publication-editor operation grants.

Two immutable system roles are created by migration:

- **System Administrator** has every global permission and implicit access to every resource;
- **Developer** can create, view, edit, publish, execute, and schedule workflows, view runs, and inspect approved connections.

System-role names and permissions are fixed, but their user/group assignments are editable. Administrators may create custom roles, assign Windows users or groups, select bounded global permissions, and grant operations on specific workflow or connection identifiers.

Editor publications grant custom or system roles independent View, Insert, Update, Delete, and Approve capabilities. These grants retain the transactional fingerprint and pending-change invalidation rules from ADR-0011. REST API application access remains a publication-scoped managed bearer token rather than a Windows role; a token already grants access only to its one reviewed API publication.

Authorization is deny-by-default. An authenticated principal with no bootstrap or persisted assignment receives no role. Every management policy and resource operation is re-evaluated server-side. UI visibility is only a convenience.

Deployment configuration contains only emergency/bootstrap assignments for System Administrator and Developer:

```text
Authorization:Bootstrap:SystemAdministratorUsers
Authorization:Bootstrap:SystemAdministratorGroups
Authorization:Bootstrap:DeveloperUsers
Authorization:Bootstrap:DeveloperGroups
```

The loopback-only Development identity is a bootstrap System Administrator. Production bootstrap arrays are empty and must be configured deliberately before first use.

SQL Server remains the only authoritative THub control-plane provider. Authorization entities use EF Core mappings and reviewed SQL Server migrations.

## Consequences

### Positive

- System administrators can inspect and manage every THub resource.
- Developers receive a stable publish-capable workflow role without security administration.
- Custom roles can be narrowly assigned to Windows users/groups and individual workflows, connections, or governed editor publications.
- Role deletion cascades its assignments and grants, preventing orphaned authority.
- Publication data operations retain their transactional authorization recheck.

### Negative

- Authorization adds SQL reads; bounded caching may be added later only with explicit invalidation.
- Polymorphic workflow/connection identifiers cannot use one relational foreign key.
- A control-plane database outage fails authorization closed.
- Access-control change audit retention still depends on PD-009.

## Alternatives considered

- **Keep four global configuration roles:** rejected because they cannot express resource isolation.
- **ASP.NET Core Identity:** rejected because THub does not need to own passwords or account recovery.
- **Put all resource grants in deployment configuration:** rejected because grants would not be transactional or manageable.
- **Issue user JWTs:** rejected because Windows Authentication already establishes management-user identity.

## Follow-up

- ADR-0022 adds metadata-only access-control change audit records; PD-009 still governs retention.
- Consider a short-lived authorization cache only with immediate invalidation after role changes.
- Add resource-name selectors to the role editor; the initial backend stores stable identifiers.
