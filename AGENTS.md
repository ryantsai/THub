# AGENTS.md

This file is the repository-wide behavioral constitution for AI coding agents working
on THub. It applies below the repository root unless a deeper `AGENTS.md` overrides it.

## Truthfulness and scope

- Treat the repository as a foundation under active development.
- Inspect the current implementation before describing a capability as implemented.
- Never present planned, scaffolded, gated, or partially implemented behavior as working.
- Keep changes within the user's request. Do not silently expand product scope.
- Do not silently resolve a product choice that materially changes security, deployment,
  execution semantics, or the data model. Use conservative scaffolding or ask.

## Read only what the task requires

- Read `README.md` for non-trivial repository work.
- Read [docs/agent-architecture.md](docs/agent-architecture.md) only when the task touches
  architecture, project boundaries, persistence, workflow semantics, authentication,
  connectors, execution, publications, hosting, deployment, or other system design.
- When that architecture guide routes to a focused document or ADR, read only the
  relevant material before changing code.
- Read `docs/product-decisions.md` when the task touches an unresolved product choice.

## Working behavior

- Preserve user changes and avoid unrelated formatting, refactors, generated files, or
  dependency churn.
- Prefer the smallest cohesive change that satisfies the request.
- Keep secrets, credentials, tokens, row payloads, and sensitive headers out of source,
  logs, errors, tests, fixtures, commands, and generated artifacts.
- Treat persisted configuration, workflow JSON, paths, identifiers, and external data as
  untrusted at their execution boundaries.
- Require every new executable workflow operation to follow
  [docs/operation-tracing.md](docs/operation-tracing.md), including its lifecycle,
  structured-field, and data-safety conventions.
- Keep documentation synchronized when behavior, configuration, commands, support
  boundaries, or operational assumptions change.
- Add or supersede an ADR for an architectural reversal; never rewrite an accepted ADR
  to hide a changed decision.
- If required authority, external coordination, or a material product choice is missing,
  stop and request direction instead of guessing.

## UI localization

- Every first-party UI addition or change must ship in both supported locales: English
  (`en`) and Taiwan Traditional Chinese (`zh-TW`).
- Use the shared localization resources and `IStringLocalizer`; do not hard-code new
  user-facing text or rely on English fallback as the completed translation.
- Localize visible text, titles, placeholders, validation and notification messages,
  empty/error states, and accessibility labels. Keep user-entered values, identifiers,
  paths, database metadata, and secrets unmodified.
- Use placeholders for values rather than concatenating translated sentence fragments.
- Use Taiwan terminology and punctuation. Do not add `zh-CN`, Simplified Chinese, or
  Mainland China wording.
- Keep the neutral resource, `SharedResource.zh-TW.resx`, and the post-render JSON mirror
  synchronized as described in [docs/localization.md](docs/localization.md).

## UI/UX golden rule: streamline the primary task

- Favor the shortest clear path to the user's primary task over exposing every available
  option at once.
- Start each operational page with the working surface itself. Remove explanatory
  headers, status facts, history, and secondary controls when they do not help the
  immediate task.
- Keep one obvious primary action. Reveal advanced, administrative, historical, and
  governance controls progressively through contextual panels, drawers, or an
  explicitly labeled advanced section.
- Prefer familiar interaction models for familiar work: spreadsheet editing should feel
  like a spreadsheet, and workflow design should be canvas-first with configuration
  shown for the selected step.
- Prompt for validation, saving, or discarding only when the user has made changes or is
  about to leave the current context.
- Do not use streamlined UI as a reason to weaken server-side authorization, validation,
  staging, approval, audit, or immutable-version boundaries. See
  [docs/ui-design.md](docs/ui-design.md).

## Validation requires explicit user authorization

- Never initiate a build, compile, restore, test run, browser/UI test, package audit, or
  formatting command on your own.
- Never start an application host merely to validate a change unless the user explicitly
  authorizes it.
- When validation would be useful, state the exact commands or browser checks you
  recommend and why, then wait for the user to decide whether to run them.
- A request to implement or fix something does not implicitly authorize validation.
- Static inspection and read-only source review are allowed.
- If validation was not authorized, clearly report what remains unverified. Never imply
  that a build, test, migration, or browser check passed when it was not run.

## Filesystem and source control

- Preserve unrelated and pre-existing working-tree changes.
- Do not discard changes with `git reset --hard`, `git checkout --`, or equivalent
  commands unless the user explicitly requests that exact destructive operation.
- Before recursive deletion or movement, resolve and verify every target is inside the
  intended workspace or explicitly named directory.
- Do not commit unless requested.
- Do not commit `bin`, `obj`, `.playwright-cli`, `output`, `artifacts`, secrets, or local
  settings.
- Keep custom build, test, publish, and `--artifacts-path` output outside every project's
  `bin` and `obj` trees; use a repository-root ignored directory such as `artifacts/`.
- Treat EF migrations as reviewed source; generate them deliberately and do not edit only
  the model snapshot.

## Handoff

- Lead with the outcome and identify material limitations.
- Report destructive cleanup and whether it is recoverable.
- Report validation actually performed and validation merely recommended as separate
  facts.
- Do not call work complete when requested behavior, required documentation, or an
  explicitly authorized validation step is unfinished.
