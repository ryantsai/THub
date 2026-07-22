# ADR-0008: Gate webhook and executable actions behind explicit policies

- Status: Accepted
- Date: 2026-07-22
- Deciders: Project maintainers

## Context

THub requirements include calling webhooks and running external executables. These actions cross major network and operating-system trust boundaries. Arbitrary user-configurable URLs, command lines, paths, or environment settings could enable SSRF, credential exposure, remote-code execution, persistence, or data exfiltration under the worker service identity.

The designer exposes webhook and executable node concepts, and the general workflow engine now executes lower-risk, bounded node kinds. Webhook and executable settings are still rejected by publish validation and execution preflight because no administrator policy model or runtime executor has been approved for either trust boundary.

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

- Visible action concepts remain draft-only and non-executable until the required administrator policies and executors are implemented.
- Administrators must configure and maintain policies.
- Some integrations require additional infrastructure/network coordination.

## Alternatives considered

- **Permit arbitrary executable paths, command strings, or webhook URLs for Designers:** rejected due to code-execution, SSRF, injection, and data-exposure risks.
- **Rely only on service-account permissions:** rejected because operating-system ACLs do not provide per-workflow destination, arguments, secret, timeout, or audit policy.
- **Remove the nodes entirely:** rejected because they are product requirements and affect graph/settings design.

## Follow-up

- Select the secret provider and audit model.
- Define administrator policy schemas and management UI.
- Threat-model each executor before implementation.
- Add integration tests for destination/path constraints, cancellation, redaction, and policy denial.
