# ADR-0008: Gate webhook and executable actions behind explicit policies

- Status: Accepted
- Date: 2026-07-22
- Deciders: Project maintainers

> Implementation note: [ADR-0020](0020-sql-backed-trusted-workflow-actions.md)
> defines the accepted SQL-backed policy, role-grant, encrypted credential, and runtime
> model that satisfies this gate.

## Context

THub requirements include calling webhooks and running external executables. These actions cross major network and operating-system trust boundaries. Arbitrary user-configurable URLs, command lines, paths, or environment settings could enable SSRF, credential exposure, remote-code execution, persistence, or data exfiltration under the worker service identity.

The designer originally exposed webhook and executable node concepts while publish validation
and execution preflight rejected them because no administrator policy model or runtime executor
had been approved for either trust boundary. ADR-0020 subsequently accepted and implemented the
governed policy model required by this decision.

## Decision

Keep the node types visible for workflow design and format evolution, but do not enable their runtime executors until explicit administrator-owned policies exist.

Webhook execution requires:

- approved schemes and destination/redirect policy;
- protection appropriate to the environment against SSRF and DNS rebinding;
- typed or named clients through `IHttpClientFactory`;
- bounded request/response bodies, timeouts, cancellation, and retry/idempotency rules;
- authentication headers resolved from secret references;
- redacted structured logs and invocation audit.

Executable execution requires:

- an administrator-owned allow-list of canonical executable paths;
- fixed argument templates with typed placeholders, never a composed shell command;
- configured working-directory and environment-variable policy;
- a restricted non-interactive Windows service account;
- timeout, process-tree cancellation, and output/resource limits;
- invocation definition/approval/run audit.

UI presence, Designer role membership, or a node stored in workflow JSON does not grant execution authority.

## Consequences

### Positive

- High-risk actions cannot silently inherit broad worker authority.
- Security review and administration have concrete boundaries.
- The workflow format can evolve without prematurely enabling unsafe runtime behavior.

### Negative

- Enabling these actions requires administrators to define and maintain trusted-action policies.
- Administrators must configure and maintain policies.
- Some integrations require additional infrastructure/network coordination.

## Alternatives considered

- **Permit arbitrary executable paths, command strings, or webhook URLs for Designers:** rejected due to code-execution, SSRF, injection, and data-exposure risks.
- **Rely only on service-account permissions:** rejected because operating-system ACLs do not provide per-workflow destination, arguments, secret, timeout, or audit policy.
- **Remove the nodes entirely:** rejected because they are product requirements and affect graph/settings design.

## Follow-up

- ADR-0020 defines the implemented secret, policy, role-grant, and management model.
- Audit-retention policy remains unresolved under PD-009.
- Integration validation remains required for destination/path constraints, cancellation,
  redaction, impersonation, and policy denial.
