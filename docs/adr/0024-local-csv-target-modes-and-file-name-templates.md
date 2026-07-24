# ADR-0024: Local CSV target modes and file-name templates

- Status: Accepted
- Date: 2026-07-24
- Deciders: Project maintainer
- Amends: [ADR-0014](0014-expand-relational-and-ftp-connectors.md)

## Context

A Worker-local SQL-to-CSV workflow needs to support recurring exports without requiring
an operator to edit JSON or delete the previous file. The create-new-only local CSV
target cannot express either a stable append/replace destination or a unique file name
derived from the run time.

An unrestricted absolute path or general expression evaluator would bypass the approved
file-root boundary and expose the Worker filesystem. Append and replace also have
different collision and recovery consequences from create-new.

## Decision

Local CSV targets support three explicit modes:

- `createNew` fails when the rendered target already exists;
- `append` preserves an existing file, adds rows, and writes a header only for a new or
  empty file;
- `replace` stages a complete new file beside the destination and replaces the
  destination only after the bounded write succeeds.

The designer accepts and displays the complete Worker path, but the immutable workflow
continues to store a relative path beneath an approved CSV connection root. File-name
placeholders may reference `runId`, `runStartedAtUtc`, `utcToday`, or a declared
workflow variable. Date/time and scalar formats use invariant .NET formatting.

Placeholder values are filename segments, not paths: separators, device/path
punctuation, control characters, null, binary values, and unknown variables are
rejected. The Worker expands the frozen execution-attempt values, then performs the
normal canonical approved-root and reparse-point checks on the rendered path before
writing.

Append and replace remain non-retryable node side effects. A recovered whole run can
still repeat them under THub's at-least-once execution model. Operators must use unique
run-derived names when duplicate effects are unacceptable and must assign one
operational owner to any stable append/replace target.

This decision changes only Worker-local CSV targets. Excel and FTP/FTPS targets remain
create-new-only.

## Consequences

### Positive

- Recurring SQL-to-CSV exports can create timestamped files without draft edits.
- Stable CSV destinations can append or replace through an explicit visible choice.
- Full Worker paths are understandable in the UI without weakening approved-root
  containment.
- Replace does not destroy the prior destination until the staged output is complete.

### Negative

- Append copies the existing file into a bounded temporary file before publishing the
  combined result, requiring temporary disk space up to the configured file limit.
- Append does not infer or convert the encoding, delimiter, or schema of an externally
  created file.
- Concurrent workflows targeting the same stable path can overwrite each other's
  snapshots; operational ownership is required.
- Whole-run recovery can repeat append or replace effects because execution remains
  at-least-once.

## Alternatives considered

- **Allow unrestricted absolute paths in workflow JSON:** rejected because it bypasses
  administrator-approved filesystem roots.
- **Use arbitrary JavaScript in file names:** rejected because filename substitution
  only needs bounded scalar formatting and should not add an expression runtime to the
  filesystem boundary.
- **Directly append to the destination stream:** rejected because a failed node could
  leave a visibly partial CSV.
- **Make replace or append implicitly retryable:** rejected because their external
  outcome can be ambiguous after a process or host failure.
