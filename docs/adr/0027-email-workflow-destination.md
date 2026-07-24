# ADR-0027: Add a bounded Email workflow destination

- Status: Accepted
- Date: 2026-07-25
- Deciders: Project maintainers
- Supersedes: [ADR-0012](0012-durable-email-alert-delivery.md)

## Context

ADR-0012 established durable Email alerts and actions but explicitly excluded attachments
and row/body templating. Workflows also need Email to behave as a data destination: the
single incoming tabular data set must be deliverable either in the message or as a file.
Sending SMTP directly from the node would weaken the existing durable outbox and recovery
model.

Row data in an Email destination is message content. Preserving durable delivery therefore
means the bounded content exists in the SQL outbox until it is delivered and later removed
by the deployment's retention process. It must never enter logs, normalized errors, audit
metadata, or SMTP rejection text.

## Decision

Retain every profile, policy, secret, lease, deduplication, retry, and at-least-once
boundary from ADR-0012, and add `EmailTarget` as an executable workflow destination.

`EmailTarget` consumes exactly one tabular input and supports two modes:

- `inline` renders an HTML table into exactly one `{{data}}` body-template placeholder;
  column names and cell values are HTML encoded;
- `attachment` creates one UTF-8 CSV attachment with RFC 4180-style quoting and a safe
  workflow-configured `.csv` leaf file name.

Subject and HTML body templates are workflow configuration. They may use the existing
bounded `{{run.id}}` variable. Only inline mode accepts `{{data}}`; attachment mode rejects
it so data is not accidentally duplicated into the body.

The target fails rather than truncates. It accepts at most 10,000 rows, one attachment of
at most 10 MiB, and the existing absolute Email body limit. Binary cells are Base64,
date/time and numeric cells use invariant formatting, and null cells are empty. The
prepared HTML or attachment is stored in the existing `AlertDeliveries.MessageJson`;
this payload-contract extension does not require a new database column.

The node succeeds when its deduplicated delivery intent is durable, as the `EmailAlert`
action does. SMTP delivery remains asynchronous and at-least-once.

MailKit SMTP command failures are classified without persisting or logging the relay's
free-form response. Status 552 is normalized as `smtp.message_too_large`; other 4xx and
5xx command failures retain transient/permanent classification. Structured error logs
contain only delivery/run/node identity, numeric SMTP status, MailKit command-error enum,
normalized error code/category, and retryability.

## Consequences

### Positive

- Email is a real tabular workflow destination without a second SMTP or retry stack.
- Inline values cannot inject HTML, and CSV attachment names cannot escape into paths.
- Relay size rejection is visible as a stable persisted and logged error category.
- Recovery uses the existing run/node deduplication identity.

### Negative

- The SQL outbox can contain bounded row payloads and Base64-encoded attachments; database
  permissions, backups, capacity, classification, and retention must reflect that.
- MIME/Base64 transport overhead means a relay can reject an attachment below THub's
  10 MiB content limit.
- HTML and CSV are the only data representations in this decision.
- Delivery remains at-least-once after ambiguous SMTP acceptance.

## Alternatives considered

- **Send synchronously from the workflow node:** rejected because it loses durable retry
  and creates ambiguous workflow effects outside the established outbox.
- **Store a pointer to transient in-memory workflow data:** rejected because the data does
  not survive Worker restart or outbox retry.
- **Silently truncate oversized data:** rejected because recipients could mistake an
  incomplete export for the complete workflow result.
- **Log the SMTP response text:** rejected because relays can echo recipient or message
  content; normalized status is sufficient for operations.

## Follow-up

- Resolve outbox/message retention and data classification under PD-009.
- Validate representative relay size limits, HTML clients, and UTF-8 CSV consumers before
  production enablement.
