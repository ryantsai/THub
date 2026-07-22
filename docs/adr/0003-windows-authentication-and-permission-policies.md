# ADR-0003: Use Windows Authentication with permission policies

- Status: Accepted
- Date: 2026-07-22
- Deciders: Project maintainers

## Context

THub is initially an intranet application for Windows/Active Directory users. It requires role-based control while keeping authorization meaningful at server boundaries. UI-only role checks would not protect management endpoints or privileged operations.

## Decision

Authenticate production users using ASP.NET Core Negotiate/Windows Authentication. Resolve configured AD/Windows groups into application roles, then authorize named permission policies such as workflow view/edit/execute, schedule management, connection management, publication management, and administration.

Use four initial roles: Viewer, Operator, Designer, and Administrator. Enforce permissions on pages/endpoints/use cases; navigation visibility is convenience only.

Retain an explicitly enabled loopback-only development handler solely for automation in the Development environment.

## Consequences

### Positive

- Users receive integrated sign-in under existing enterprise identity controls.
- Permission policies avoid hard-coding role names throughout the application.
- AD groups support centralized onboarding and removal.
- Server-side Blazor can use the authenticated principal without putting credentials in client storage.

### Negative

- The production model assumes a compatible Windows/domain environment.
- Kerberos/SPN/delegation setup can be operationally complex.
- Global role mapping does not provide per-workflow or per-connection grants.
- Group resolution can be slow or complicated in large/nested-domain environments.
- A default authenticated role can grant more than intended if deployment configuration is not reviewed.

## Alternatives considered

- **ASP.NET Core Identity:** rejected because first-party passwords/accounts are not required for the intranet v1.
- **Microsoft Entra ID/OIDC:** viable for cloud/external access but not selected for the initial on-premises requirement.
- **Raw role checks only:** rejected because permissions are a more stable application contract.

## Follow-up

- Configure real organization groups before deployment; checked-in production mapping arrays remain empty.
- Decide whether default authenticated Viewer access remains acceptable.
- Create a new ADR if per-resource grants or Entra/JWT consumers are added.
