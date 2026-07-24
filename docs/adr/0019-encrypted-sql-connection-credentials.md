# ADR-0019: Store connection credentials as encrypted SQL ciphertext

- Status: Superseded by [ADR-0023](0023-single-credential-encryption-key.md)
- Date: 2026-07-23
- Deciders: Project maintainers
- Supersedes: [ADR-0013](0013-provider-neutral-database-authentication.md)
- Amends: [ADR-0014](0014-expand-relational-and-ftp-connectors.md)

## Context

Supplying a separate username and password through deployment configuration for every
connection and every consuming host creates substantial provisioning and rotation work.
THub already has an administrator-only connection-management surface and a SQL Server
control plane shared by Web, Worker, and Publications.

Storing plaintext credentials, reversible obfuscation, or the encryption key beside the
ciphertext would make a control-plane database disclosure a credential disclosure. The
application still needs one external root secret and must preserve the provider-neutral
resolver boundary.

## Decision

Keep a non-secret credential reference in connection metadata and workflow JSON. Store
the referenced username/password payload in `thub.EncryptedConnectionCredentials`,
encrypted with AES-256-GCM before it reaches SQL Server. Each row stores:

- the credential reference;
- the key version;
- a fresh 96-bit nonce;
- ciphertext;
- a 128-bit authentication tag;
- the last-updated UTC instant.

The credential reference and payload schema version are authenticated as associated
data, so ciphertext cannot be moved to another reference without detection. Connection
metadata and a supplied credential are committed in one EF Core transaction. Existing
passwords are never returned to Blazor; the connection editor accepts a replacement
username/password pair or leaves an existing stored credential unchanged.

The encryption key ring is external configuration:

```text
CredentialEncryption:CurrentKeyVersion
CredentialEncryption:Keys:{version}
```

Each key is a Base64-encoded random 32-byte value. Deployments supply these settings
through environment variables or another .NET configuration provider; they must never
be checked in or stored in the THub database. Web, Worker, and Publications receive only
the control-plane permissions and key ring required for their approved connection use.
A missing key or a failed authentication tag fails credential resolution closed.

New writes use the current key version. Older configured key versions remain available
for decryption during staged rotation. An operator replaces each stored credential to
re-encrypt it under the current version before removing an old key. Automated bulk
re-encryption is not implemented.

The provider-neutral `IConnectionCredentialResolver` remains the Application boundary.
Infrastructure owns SQL persistence, cryptography, and provider-specific connection
construction. SMTP credentials remain outside this decision and continue to use the
separate `ISecretResolver` policy from ADR-0012.

## Consequences

### Positive

- Operators provision one versioned host key ring instead of username/password
  variables for every connector.
- A database-only disclosure exposes authenticated ciphertext rather than source-system
  passwords.
- Credential creation and rotation are available through the authorized connection UI.
- Existing connection and workflow serialization remains compatible because references
  are unchanged.

### Negative

- A compromise that obtains both the SQL ciphertext and an authorized host key can
  decrypt credentials.
- Environment variables are visible to sufficiently privileged machine administrators.
- Losing every copy of a required key version makes its credential rows unrecoverable.
- Restoring a database backup requires the matching external key versions.
- Existing externally provisioned references are not migrated automatically; an
  administrator must enter and store each credential after upgrade.
- Shared references intentionally share one credential; replacing one affects every
  connection that uses that reference.

## Alternatives considered

- **Per-connection external configuration:** superseded because its operational burden
  motivated this decision.
- **Store the key in SQL Server:** rejected because a database disclosure would include
  both ciphertext and its decryption key.
- **Encryption without authentication:** rejected because it cannot reliably detect
  tampering or swapped ciphertext.
- **One unversioned key:** rejected because rotation would require an unsafe all-at-once
  database and host change.
- **ASP.NET Core Data Protection:** not selected because THub needs an explicit,
  provider-neutral credential payload and key-version contract shared by three hosts.

## Follow-up

- Add a bounded administrator operation for bulk re-encryption before deployments need
  to retire old key versions at scale.
- ADR-0022 adds metadata-only credential create/replace audit records; PD-009 still defines
  security-event retention and classification.
- Revisit an HSM or vault-backed key provider if the deployment threat model no longer
  accepts environment-held master keys.
