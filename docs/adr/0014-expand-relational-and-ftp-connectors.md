# ADR-0014: Expand relational and FTP workflow connectors

- Status: Accepted
- Date: 2026-07-23
- Deciders: Project maintainers
- Supersedes: [ADR-0006](0006-v1-sql-server-and-local-file-connectors.md)
- Relational target write-mode restriction partially superseded by:
  [ADR-0018](0018-primary-key-relational-target-mutations.md)
- Credential-storage decision amended by:
  [ADR-0019](0019-encrypted-sql-connection-credentials.md)
- Publication-provider restriction superseded by:
  [ADR-0021](0021-provider-neutral-governed-publications.md)
- File-target create-new restriction superseded by:
  [ADR-0024](0024-local-csv-target-modes-and-file-name-templates.md) and
  [ADR-0025](0025-excel-and-ftp-target-modes-and-file-name-templates.md)

## Context

THub needs operational sources and targets for MySQL, PostgreSQL, Oracle Database, and FTP in addition to SQL Server and local files. These systems have different providers, identifier rules, transport security, and authentication details. A generic ODBC adapter would hide those differences and make safe metadata validation difficult.

FTP is also required for CSV, tab-delimited text, and modern Excel files. Some existing endpoints permit only unencrypted FTP, even though it exposes credentials and file contents in transit.

## Decision

Use provider-specific open-source or vendor-maintained .NET libraries:

- MySqlConnector for MySQL;
- Npgsql for PostgreSQL;
- Oracle Managed Data Access Core for Oracle Database;
- FluentFTP for FTP and FTPS;
- the existing CsvHelper and ClosedXML parsers after an FTP download has passed configured size bounds.

All four connection types use the provider-neutral `IConnectionCredentialResolver`.
Connection metadata and workflow JSON store only a secret reference. ADR-0023 defines
the built-in encrypted SQL credential store and its single external master key.

Relational workflow nodes expose discovered table/view identifiers, bounded reads, and transactional parameterized inserts. They do not accept arbitrary SQL or implement update, merge, replace, or delete.

FTP connections explicitly select plain FTP, explicit FTPS, or implicit FTPS. Plain FTP is supported only as an administrator-selected compatibility mode and must be presented as unencrypted. FTP workflow nodes:

- use absolute traversal-free remote paths;
- support CSV, tab-delimited text, and `.xlsx`/`.xlsm` input;
- create new CSV, tab-delimited, or `.xlsx` targets without overwrite;
- enforce connection and execution file, row, column, timeout, and memory bounds;
- stage data in a unique worker temporary directory and remove it on completion on a best-effort basis;
- do not create arbitrary remote directories or provide a watcher.

SQL Server remains the authoritative THub control plane. This ADR originally kept
governed publications and Spreadsheet editors SQL Server-only; ADR-0021 supersedes that
restriction and defines publication support for all four relational providers.

## Consequences

### Positive

- Provider-specific metadata and quoting remain explicit and testable.
- All database and FTP passwords use one replaceable authentication boundary.
- The same bounded file parsing behavior applies to local files and FTP transfers.
- FTPS certificate validation is the default secure posture while required legacy FTP endpoints remain reachable by explicit choice.

### Negative

- Four provider dependencies and their transitive dependencies require vulnerability and compatibility maintenance.
- Plain FTP cannot protect credentials or data from network observers.
- Live interoperability still requires contract testing against supported server versions and TLS configurations.
- Temporary disk space must be sized and monitored for bounded FTP transfers and crash remnants.

## Alternatives considered

- **Generic ODBC/OleDb adapter:** rejected because provider metadata, quoting, types, TLS, and errors differ materially.
- **Implement database wire protocols:** rejected as unnecessary and unsafe compared with maintained providers.
- **Implement FTP from sockets:** rejected because FluentFTP supplies maintained FTP/FTPS protocol behavior under an MIT license.
- **SFTP through the FTP connector:** rejected because SFTP is SSH-based and needs a separate connector and security decision.
- **Disallow plain FTP:** safer, but rejected because the explicit compatibility requirement cannot be met with FTPS alone.

## Follow-up

- Add live connector contract tests for representative server versions, encodings, schemas, certificate modes, cancellation, and large bounded transfers.
- Define supported server-version matrices after deployment targets are known.
- Monitor provider advisories and package vulnerability scans.
