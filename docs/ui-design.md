# THub UI design principles

## Golden rule

Favor a streamlined path to the user's primary task over visible completeness.

THub is an operations product, but governance should be present when it matters rather
than permanently occupying the workspace. The user should first see the surface they
came to operate: the workflow canvas, spreadsheet, publication source picker, run list,
or settings form.

The workflow experience takes interaction cues from
[n8n](https://github.com/n8n-io/n8n): keep the canvas dominant, make adding a step
obvious, show configuration in context, and keep save/publish actions compact. THub
retains its own visual identity and its stricter governed execution and publication
boundaries.

## Interaction hierarchy

Every page should have:

1. one primary workspace;
2. one obvious primary action;
3. contextual controls for the current selection or state;
4. advanced, administrative, historical, and diagnostic controls behind explicit
   progressive disclosure.

Do not add a header, fact strip, card, step indicator, help panel, or activity list
unless it changes what the user can decide or do at that moment. Prefer a calm layout,
short utility copy, familiar controls, and direct manipulation.

## Core product patterns

### Workflow designer

- Make the canvas the default and largest surface.
- Open the step library only when the user asks to add a step.
- Show configuration only for the selected step.
- Keep workflow-wide schedule, variables, and functions in a separate settings panel.
- Keep save, validation, and publish state compact and close to the workflow name.

### Publication setup

- Use a short path: choose the publication type, choose a source, review the visible
  columns, then publish.
- Generate safe defaults from inspected metadata.
- Keep provider limits, rate limits, foreign-key lookup policy, filter/sort policy, and
  other specialist controls in an Advanced section.
- Explain blocking requirements at the field or action that cannot proceed.

### Published table editor

- Make the spreadsheet fill the working viewport.
- Use a familiar compact toolbar for row actions, undo/redo, search/filter, paging, and
  contextual details.
- Do not show permission facts or staged-change history until requested.
- After the first edit, show a persistent unsaved-change prompt with Save changes and
  Discard actions.
- Saving must capture the workbook, validate typed values against the active immutable
  schema, and create the existing reviewable staged change set. It must not grant the
  browser direct source-write authority.
- Put review history in a drawer or separate context so it never pushes the spreadsheet
  below the fold.

## Progressive disclosure boundary

Progressive disclosure changes visibility, not enforcement. Server-side authorization,
source inspection, bounded reads, stable keys, schema validation, optimistic
concurrency, staged approval, Worker apply, audit, and immutable-version rules remain
authoritative even when their controls or explanations are collapsed.

## Review checklist

- Can a first-time user identify the page's primary task in five seconds?
- Is the working surface visible without scrolling past explanatory content?
- Is there only one dominant action for the current state?
- Are options shown because they are needed now, or merely because they exist?
- Does editing produce an explicit save/discard state?
- Can advanced users still reach governed controls without cluttering the default path?
- Are English and Taiwan Traditional Chinese complete?
- Are keyboard, focus, reduced-motion, and responsive behaviors preserved?
