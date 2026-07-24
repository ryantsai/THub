# ADR-0020: Govern webhooks and executables as SQL-backed trusted actions

- Status: Accepted
- Date: 2026-07-24
- Deciders: Project maintainers
- Amends: [ADR-0008](0008-gate-webhooks-and-executables.md)
- Amends: [ADR-0016](0016-sql-backed-custom-role-and-resource-authorization.md)
- Amends: [ADR-0019](0019-encrypted-sql-connection-credentials.md)

## Context

THub needs operational webhook and executable actions without allowing workflow authors
to turn the Worker into an arbitrary HTTP proxy or command runner. Some approved
executables must run under a Windows identity other than the Worker service identity.
Those credentials cannot be stored in workflow JSON, plaintext configuration, or logs.

## Decision

Represent each approved webhook or executable as a `TrustedAction` resource in SQL
Server. Only a System Administrator may create, change, enable, or disable definitions.
A workflow node stores only the trusted-action identifier and, for a webhook, one bounded
non-secret request body.

Custom roles receive `trusted-action.use` on individual trusted-action resource IDs.
The Web filters the designer selector by the authenticated user's effective roles and
rechecks the grant when publishing. An immutable version that was validly published
remains approved for execution until an administrator disables or changes the trusted
action. The Worker reloads and validates the current enabled definition before every
invocation.

Webhook definitions own the exact destination, HTTP method, content type, fixed
non-secret headers, authentication mode, private-address decision, timeout, and
request/response limits. Redirects, URL overrides, Authorization headers in workflow
JSON, loopback/link-local destinations, and response-body persistence are prohibited.
DNS resolution is repeated in the HTTP connection callback and the socket connects to
the validated address to reduce DNS-rebinding exposure. Basic and bearer authentication
resolve an encrypted credential reference; bearer mode uses the protected password as
the token.

Executable definitions own one canonical local executable path, working directory,
fixed argument template, fixed environment, timeout, combined stdout/stderr limit, and
profile-loading decision. THub uses `ProcessStartInfo.ArgumentList`, never a composed
command line or shell. Shells, PowerShell, script hosts, indirect binary launchers,
UNC/device paths, and reparse-point executable/working-directory roots are rejected.
Templates may use only typed run ID, node ID, attempt number, and input-row-count
placeholders. Cancellation, timeout, or output-limit failure terminates the process
tree.

An executable may omit a credential and run as the least-privilege Worker identity, or
reference an encrypted `DOMAIN\user`/UPN credential. The Worker uses the Windows
alternate-credential process-start boundary and never returns or logs the password.
The deployment administrator owns the target account's local security rights,
filesystem permissions, network access, and profile behavior.

Trusted-action credentials reuse the AES-256-GCM envelope and external versioned
master-key ring accepted by ADR-0019. The credential table and resolver remain
provider-neutral despite their original connection-oriented name. Storage references
use a reserved trusted-action prefix so an action credential cannot accidentally replace
a connector credential with the same administrator-facing reference. Passwords are
replacement-only in the Web UI.

Definition metadata records creator, last updater, and timestamps. Worker logs record
trusted-action ID, run ID, node ID, status category, and bounded outcome metadata, never
URL bodies, headers, arguments after expansion, environment values, credentials, or
stdout/stderr. Final retained security-event history and retention remain governed by
PD-009.

## Consequences

### Positive

- Workflow authors can use administrator-approved integrations without selecting paths,
  URLs, accounts, headers, or command text.
- Per-resource role grants separate workflow editing from privileged action use.
- Disabling one trusted action stops future invocations without rewriting workflow JSON.
- Run-as passwords receive the same authenticated encryption and external-key separation
  as connector credentials.

### Negative

- Updating an enabled trusted action changes what an already-published workflow invokes;
  administrators must treat changes as approvals.
- Alternate Windows process creation depends on deployment-specific logon rights and
  filesystem/network ACLs.
- THub does not provide an operating-system sandbox, CPU quota, or arbitrary output-file
  containment; administrators must use a restricted identity and ACLs.
- ADR-0022 supplies the durable metadata-only definition/execution lifecycle stream;
  PD-009 remains open for retention.

## Alternatives considered

- **Workflow-authored executable paths, commands, URLs, or account names:** rejected
  because author access or workflow import would become remote code execution or SSRF.
- **Store Windows passwords in workflow JSON or action definition JSON:** rejected
  because immutable versions, exports, logs, and database readers would expose them.
- **Invoke `cmd`, PowerShell, or `schtasks.exe`:** rejected because it reintroduces
  command parsing and indirect execution.
- **Require every action to run as the Worker:** rejected because approved tools may
  need narrower, distinct operating-system identities.
