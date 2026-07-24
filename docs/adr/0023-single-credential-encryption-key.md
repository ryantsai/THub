# ADR-0023: Use one credential encryption key

- Status: Accepted
- Date: 2026-07-24
- Deciders: Project maintainers
- Supersedes: [ADR-0019](0019-encrypted-sql-connection-credentials.md)

## Context

THub has not been released, its development database is disposable, and deployments do
not need compatibility with previously encrypted credential rows. The versioned key-ring
contract and persisted key version add configuration and schema complexity without a
current operational requirement.

THub must still keep database, FTP, webhook, and executable credentials encrypted in SQL
with a root secret stored outside the database.

## Decision

Use one external Base64-encoded 32-byte key:

```text
CredentialEncryption:Key
```

Continue encrypting each credential payload with AES-256-GCM, a fresh 96-bit nonce, a
128-bit authentication tag, and its secret reference as associated data. Persist only
the reference, nonce, ciphertext, authentication tag, and update time. Do not persist a
key version or accept a versioned key ring.

The Web, Worker, and Publications hosts receive the same key when their approved work
requires credential access. A missing or malformed key and an authentication failure
fail closed. The key must never be stored in THub SQL or checked-in configuration.

Changing the key deliberately provides no compatibility path for existing ciphertext.
Operators must clear and re-enter stored credentials, or reinitialize the disposable
database, when changing it.

## Consequences

- Local and initial deployment configuration needs only one secret.
- Credential rows and encryption code contain no rotation/version compatibility state.
- Losing or replacing the key makes every existing encrypted credential unreadable.
- Future zero-downtime key rotation would require a new schema and ADR.
- A compromise containing both the SQL ciphertext and the key can decrypt credentials.

## Alternatives considered

- Retain the versioned key ring from ADR-0019: rejected because the unreleased product
  has no ciphertext compatibility requirement.
- Store the key in SQL: rejected because a database disclosure would then expose both
  ciphertext and its decryption key.
- Store credentials as plaintext: rejected because database access alone would disclose
  all referenced credentials.
