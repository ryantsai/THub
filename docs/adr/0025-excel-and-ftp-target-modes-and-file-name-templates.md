# ADR-0025: Excel and FTP target modes and file-name templates

- Status: Accepted
- Date: 2026-07-24
- Deciders: Project maintainer
- Amends: [ADR-0014](0014-expand-relational-and-ftp-connectors.md)
- Extends: [ADR-0024](0024-local-csv-target-modes-and-file-name-templates.md)

## Context

ADR-0024 added create-new, append, replace, and run-variable file names to local CSV
targets. The same recurring-export problem applies to local Excel targets and to
CSV/tab-delimited/Excel files delivered through FTP or FTPS.

Excel append must preserve an existing workbook and add rows to one configured
worksheet. FTP updates must avoid exposing a partially uploaded final file and must
remain within the existing remote-path, transport, credential, and size boundaries.

## Decision

Local Excel targets and FTP/FTPS file targets support the same explicit modes:

- `createNew` fails if the rendered target exists;
- `append` adds rows to an existing target, or creates it when absent;
- `replace` publishes only the newly generated file.

Local Excel paths remain relative to an approved Excel connection root even though the
designer displays and accepts the complete Worker path. Excel append stages a copy
beside the destination, opens or creates the configured worksheet, adds rows after the
last used row, writes headers only for a new or empty worksheet, saves the staged
workbook, and then replaces the destination.

FTP target paths remain absolute, traversal-free remote paths. Append downloads an
existing bounded target into a unique Worker temporary directory and applies the local
CSV or Excel append behavior. Replace creates a new bounded local file. The Worker
uploads the completed result to a unique `.partial` sibling in the destination
directory and asks the FTP server to move it into place with skip or overwrite
collision behavior. THub does not create remote directories.

Both target types use the bounded file-name placeholder rules from ADR-0024.
Placeholders may occur only in the final file name and may reference frozen run values
or declared workflow variables.

These targets remain non-retryable node side effects. Whole-run recovery is still
at-least-once. Stable append/replace destinations require one operational owner.

## Consequences

### Positive

- Local Excel and FTP deliveries can use readable timestamped or run-specific names.
- Recurring exports no longer require raw JSON edits or manual deletion.
- Excel and FTP reuse the same bounded local writers and filename rules as local CSV.
- A failed FTP upload does not expose a partially uploaded final target name when the
  server supports the required same-directory move.

### Negative

- Excel append loads and saves the bounded workbook and therefore requires memory and
  temporary disk proportional to the configured workbook limit.
- FTP append requires a complete download and upload of the combined file.
- FTP servers must permit a same-directory move/rename. A server without that
  capability fails the target rather than publishing a partial final file.
- A Worker or network failure can leave a clearly named remote `.partial` artifact for
  operator cleanup.
- Concurrent workflows targeting one stable path can overwrite snapshots, and
  whole-run recovery can repeat append effects.

## Alternatives considered

- **Direct FTP APPE or overwrite:** rejected because a failed transfer could leave a
  partial final file and Excel cannot be safely appended as a byte stream.
- **Append directly to the local workbook:** rejected because a failed save could
  damage the prior destination.
- **Allow placeholders in directory segments:** rejected because variable-driven
  directory selection complicates containment, provisioning, and remote ownership.
- **Automatically create local or remote directories:** rejected because directory
  provisioning remains administrator/operator owned.
