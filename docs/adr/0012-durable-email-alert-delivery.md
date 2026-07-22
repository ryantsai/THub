# ADR-0012: Deliver Email alerts through a durable outbox

- Status: Accepted
- Date: 2026-07-23
- Deciders: Project maintainers

## Context

Workflow failure can occur before a graph is executable, and SMTP acknowledgement can be ambiguous when a worker loses connectivity. Sending Email inline with a run-state transaction would either lose alerts on rollback or hold SQL transactions across network I/O.

THub needs both workflow-event alerts and an Email action without persisting SMTP credentials or sensitive row data in workflow JSON.

## Decision

Support Email as the initial alert channel through an Application `IAlertSender` port and a durable SQL Server outbox.

Administrator-owned Email delivery profiles contain non-secret SMTP relay settings, an approved sender, transport-security requirements, recipient-domain policy, and an optional credential secret reference. The worker resolves the credential only at send time through `ISecretResolver`. Secret values are supplied by an external .NET configuration provider or a future organization-approved provider and are never returned to Blazor or stored in THub metadata.

Workflow alert rules subscribe to terminal run events such as failure, success, and cancellation. The terminal transition and deduplicated alert-delivery row are committed in one THub transaction. This keeps failure alerts available even when graph parsing or planning failed.

Also expose an `EmailAlert` canvas action backed by the same outbox. The action succeeds when a valid durable delivery is queued; the eventual delivery status remains visible separately. It does not claim that the recipient accepted the message synchronously.

An independent worker dispatcher claims outbox rows with leases, sends through an SMTP adapter, and records attempts, provider message identity when available, last error category, and terminal state. Transient failures use bounded exponential backoff with jitter; permanent failures are dead-lettered. A unique rule/run/event or action/run/node key prevents ordinary duplicate enqueueing, including recovered step attempts.

Delivery is at-least-once. A crash after SMTP acceptance but before THub records success can produce a duplicate. Messages carry stable correlation and message identifiers where the relay supports them.

Email v1 has no attachments, arbitrary headers, or raw row/body templating. Templates use an allow-list of bounded workflow/run/error variables. Profiles cap recipients, subject/body size, send concurrency, and allowed recipient domains. Logs and audit records never include credentials or full message bodies.

## Consequences

### Positive

- All terminal run transitions, including queued cancellation, and their alert intent are committed atomically.
- Alert delivery survives worker restarts and can be retried independently.
- SMTP authority, sender identity, and recipient policy remain administrator controlled.
- The same delivery and observability model supports workflow rules and canvas actions.

### Negative

- Email action completion means queued, not synchronously delivered.
- Ambiguous SMTP outcomes can cause duplicate Email.
- The worker needs another leased dispatcher and retention policy.
- Production credential resolution still depends on the organization decision in PD-008.

## Alternatives considered

- **Send directly inside workflow execution:** rejected because network I/O cannot be atomic with run state and a crash can lose alerts.
- **Use `System.Net.Mail.SmtpClient`:** rejected for new development; the .NET API documentation recommends other libraries.
- **Store SMTP passwords in profile JSON:** rejected because metadata, exports, UI, and logs must not contain secrets.
- **Only provide an Email graph node:** rejected because planning and validation failures could never reach it.

## Follow-up

- Select and document the production secret provider under PD-008.
- Configure retention and message classification under PD-009.
- Add a new channel only behind the same policy, outbox, and redaction boundary.
