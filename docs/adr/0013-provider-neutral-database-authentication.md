# ADR-0013: Use provider-neutral referenced database credentials

- Status: Superseded
- Date: 2026-07-23
- Deciders: Project maintainers
- Superseded by: [ADR-0019](0019-encrypted-sql-connection-credentials.md)

## Context

THub originally required Windows integrated authentication for every configured SQL Server connection. Some approved databases require a database user name and password, while future database connector types may use different authentication mechanisms. Web, Worker, and Publications run under separate identities, so a user-profile-scoped credential store is not a shared deployment solution.

The project must not persist raw credentials in connection metadata, workflow graphs, logs, or checked-in configuration. It should also avoid coupling Application contracts to a particular commercial or self-hosted vault.

## Decision

Database connection metadata contains a provider-neutral authentication kind and, when required, a credential secret reference. V1 supports:

- `Integrated`, which uses the executing host identity;
- `UserPassword`, which resolves a referenced user name and password at use time.

Application owns `IConnectionCredentialResolver` and the authentication configuration contract. Infrastructure owns provider connection construction and the initial resolver. The initial resolver reads `ConnectionCredentials:{reference}:Username` and `Password` through `IConfiguration`; deployments supply those values through environment variables, key-per-file, Azure Key Vault, or another approved .NET configuration provider. Raw secret values are never returned to Blazor or serialized into `DataConnection.ConfigurationJson`.

Resolution is asynchronous so a future Vault, OpenBao, Key Vault, or dynamic database-credential adapter can replace the initial resolver without changing connector configuration or execution contracts. A missing referenced credential fails closed.

## Consequences

### Positive

- Windows-integrated connections remain supported through the current schema-v1 contract.
- Database authentication works consistently in Web probes, Worker execution/editor apply, and Publications reads.
- Future database connectors can reuse the authentication contract without depending on SQL Server connection strings.
- Secret storage remains a deployment choice behind standard configuration providers or a replacement resolver.

### Negative

- Operators must provision a referenced credential for every host that uses the connection.
- Environment variables expose values to sufficiently privileged local process administrators; higher-assurance deployments should use an approved vault-backed configuration provider or resolver.
- Credential rotation behavior depends on the selected configuration provider and host reload policy.

## Alternatives considered

- **Windows Credential Manager packages:** rejected as the default because credentials are scoped to a Windows profile and do not naturally span THub's separate service identities.
- **Encrypted local secret-file packages:** rejected as the default because distributing and rotating the shared decryption key remains an external secret problem.
- **VaultSharp plus HashiCorp Vault:** viable as a future adapter, but rejected as a mandatory dependency because it imposes a separate server, authentication, token-renewal, and operations model.
- **ASP.NET Core Data Protection ciphertext in THub SQL:** rejected for connection credentials because every consuming host would need access to a shared key ring, widening the credential trust boundary.
