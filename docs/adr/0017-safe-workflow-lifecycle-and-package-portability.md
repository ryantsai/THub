# ADR-0017: Preserve workflow history and use redacted portable packages

- Status: Accepted
- Date: 2026-07-23
- Deciders: Project maintainers

## Context

Operators need to remove obsolete workflows and move workflow definitions between THub
installations. Hard-deleting a published workflow would break immutable version and run
history. Exporting connection configuration would create a secret-disclosure boundary,
while retaining installation-specific connection identifiers alone would make packages
unnecessarily difficult to move.

## Decision

Use two removal operations:

- archive any non-archived workflow while preserving its immutable versions, runs, alerts,
  and historical identity;
- permanently delete only an unpublished draft that has no immutable versions, runs, or
  alert rules.

Both operations require the `workflow.delete` permission and are reauthorized for the
specific workflow. Permanent deletion also removes polymorphic workflow resource grants
in the same serializable transaction.

Export the current editable workflow definition as a versioned JSON package containing
only metadata, schedule configuration, schema-versioned graph JSON, and connection
identity hints (identifier, name, and kind). Do not export connection configuration,
credentials, secret references, immutable version history, runs, or alert deliveries.

Import always creates a new unpublished draft owned by the importing identity. Resolve
connection references by a matching identifier or a unique case-insensitive name/kind
pair. Replace unresolved identifiers with an invalid empty placeholder and report warnings
so the user must repair them before publication. Normal graph, schedule, and publication validation still
applies; import never publishes or executes a workflow.

## Consequences

### Positive

- Historical runs remain attributable and reproducible.
- Unused drafts can be cleaned up without a retention-policy decision.
- Packages are portable without becoming a credential-export mechanism.
- Import remains reviewable and cannot silently activate schedules or execution.

### Negative

- Archived workflows remain in authoritative storage until a future retention policy is
  accepted.
- Connection names and kinds are operational metadata and may require handling controls.
- Unresolved references require manual repair before publication.
- Package schema changes require explicit compatibility work.

## Alternatives considered

- **Cascade-delete every workflow:** rejected because it destroys immutable execution
  history and conflicts with the durable-run model.
- **Export full connection records:** rejected because configuration can contain secret
  references and deployment-specific trust settings.
- **Import directly as published:** rejected because it bypasses review, resource
  resolution, and publication validation.

## Follow-up

- ADR-0022 adds metadata-only lifecycle audit records for persisted changes; PD-009 still governs retention.
- Add explicit user-selected connection mapping if automatic mapping is insufficient.
