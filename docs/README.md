# THub documentation

## Architecture set

- [Agent architecture guidance](agent-architecture.md): conditional project-boundary,
  persistence, execution, security, UI, and validation-routing guidance referenced by
  the root behavioral constitution.
- [Architecture overview](architecture.md): management, publication, worker, and SQL boundaries; current-versus-target flows; failure model; observability; and roadmap.
- [Data model](data-model.md): current THub/Quartz schemas plus accepted immutable workflow, lease/step, publication/token/grant/change-set, Email outbox, and audit persistence targets.
- [Security architecture](security.md): Windows management authentication, isolated managed-bearer publications, Spreadsheet role grants/staging, connectors, Email secrets/outbox, and gated executables/webhooks.
- [Deployment and operations](deployment.md): separate Web/Publications IIS boundaries, worker/database deployment, Quartz and Serilog configuration, least-privilege identities, health, and recovery.
- [UI localization](localization.md): supported English and Taiwan-Traditional-Chinese
  locales, culture-cookie behavior, resources, terminology, and the required UI
  contribution checklist.
- [UI design principles](ui-design.md): the streamlined-experience golden rule,
  progressive-disclosure boundary, and core workflow, publication, and spreadsheet
  interaction patterns.
- [Workflow operation tracing convention](operation-tracing.md): required lifecycle,
  structured fields, levels, and data-safety rules for every current and future
  executable operation.
- [Architecture Decision Records](adr/README.md): accepted, proposed, and superseded decisions with rationale and consequences, including ADR-0010 through ADR-0012.

## Product planning

- [Open product decisions](product-decisions.md): remaining owner decisions plus the authoritative records for resolved publication, editor, and execution choices.

## Maintenance rules

- Update the focused architecture document when implementation or operational assumptions change.
- Add/supersede an ADR for a material technology, boundary, security, persistence, or execution decision.
- Keep planned and implemented behavior distinguishable.
- Keep every first-party UI change complete in both `en` and `zh-TW`; do not introduce
  `zh-CN` resources or Mainland China terminology.
- Keep the primary task visible and move advanced, administrative, historical, and
  diagnostic options behind contextual progressive disclosure.
- Require every new executable operation to follow the workflow operation tracing
  convention.
- Prefer links to one authoritative explanation over duplicating instructions across documents.
