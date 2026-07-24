# ADR-0026: Govern full-table XLSX export and larger editor pages

- Status: Accepted
- Date: 2026-07-25
- Deciders: Project owner and maintainers
- Extends: [ADR-0021](0021-provider-neutral-governed-publications.md)
- Supersedes in part: [ADR-0011](0011-isolated-governed-data-publications.md)

## Context

ADR-0011 disabled Spreadsheet import/export and limited an in-memory editor window to
1,000 rows. That remains appropriate for arbitrary workbook import and for database
editing, but authorized users also need a complete Excel extract of the approved shared
table. Exporting only the current 250-row editor window is misleading and does not meet
that reporting need.

Users also need explicit page size and total-page context while navigating a large
shared table. Keyset paging remains preferable to offset paging for stable database
performance, but keyset cursors do not reveal a total page count without a separate
filtered count query.

## Decision

Permit a Windows-authenticated user with the publication's `View` grant to export every
row and every readable column from the active editor publication to one XLSX worksheet.
The export represents source database state, not unsaved browser edits, and does not
enable workbook import or direct source writes.

The Web host generates XLSX incrementally from provider-neutral keyset pages. It counts
the source first, fails rather than truncating when the result exceeds Excel's
1,048,575-data-row single-worksheet limit, rejects cells that exceed Excel's character
limit, and fails if the source row count changes during generation. The temporary XLSX
file uses a random operating-system temporary path and delete-on-close semantics. Row
values, filter values, source credentials, and file contents are never logged.

Add provider-specific parameterized count plans for SQL Server, MySQL, PostgreSQL, and
Oracle. The editor uses the same approved filters for its count and data page, displays
the current and total page count, and retains keyset cursors for navigation.

Editor page sizes are selected from 250, 500, 1,000, and 2,000 rows. An immutable
publication version records the maximum allowed editor page size, which cannot exceed
2,000. The UI offers only choices at or below that active-version limit. New publication
drafts default the maximum to 2,000 while the editor initially loads 250 rows. Connection
batch and response-size limits remain authoritative.

The Radzen demo toolbar is not enabled wholesale. THub retains its server-authoritative
filter/sort controls and continues to disable workbook structure, formulas, formatting,
validation, clipboard, autofill, and import.

Editable cells use the immutable publication column metadata rather than an untyped
text-only editor. Boolean values use an explicit choice, temporal values provide native
date/time pickers alongside locale-aware parseable text entry, and other values expose
type-appropriate input hints. The editor enforces nullability, declared string/binary
length, numeric precision, and numeric scale before committing a cell; server-side
staging validation and source constraints remain authoritative.

## Consequences

### Positive

- An authorized export contains the complete approved shared table rather than one UI page.
- XLSX generation does not retain the entire result in the Blazor circuit or application memory.
- Users can trade page density against load time and can see their position in the result set.
- Count and export semantics remain provider-neutral and permission-checked.
- Data-type-aware controls surface source constraints before a change reaches staging.

### Negative

- A full-table count and export can be expensive on large source objects.
- Concurrent source changes cause an export retry instead of a potentially inconsistent file.
- One worksheet cannot represent more than Excel's format limit.
- Total page counts are a snapshot and may change as source rows are added or removed.

## Rejected alternatives

- **Export only the loaded editor page:** rejected because it silently produces an incomplete report.
- **Load every row into a Radzen workbook before export:** rejected because it couples export size to Blazor circuit memory.
- **Enable the complete Radzen File/View/Data ribbon:** rejected because it exposes operations outside THub's governed editing contract.
- **Use offset paging to jump directly to any page:** rejected because large offsets create provider-specific performance problems.

## Required validation

- Verify View authorization and denial for the export endpoint.
- Verify XLSX contents, headers, data types, freeze/header filtering, and deletion of temporary files.
- Verify all four relational providers, schema-drift failure, Excel row/cell limits, cancellation, and concurrent source changes.
- Verify 250/500/1,000/2,000 page selection, filtered counts, English and `zh-TW`, responsive toolbar overflow, and browser console output.
- Verify Boolean, date, date/time, date/time-offset, time, numeric, text, binary, nullable,
  and required cell editors against SQL Server, MySQL, PostgreSQL, and Oracle metadata.
