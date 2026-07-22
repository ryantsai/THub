# ADR-0004: Use SQL Server as the control plane and a Windows Service worker

- Status: Accepted
- Date: 2026-07-22
- Deciders: Project maintainers

## Context

Scheduled work must survive web restarts and Blazor circuit loss. The requested backend is SQL Server, and the deployment environment is Windows. Adding a separate message broker at initialization would expand operations before queue volume and latency needs are known.

## Decision

Use SQL Server as the authoritative metadata store and durable coordination boundary. Run scheduling and future node execution in a separate .NET Worker executable configured for Windows Service lifetime.

The initial scheduler polls SQL Server, selects a bounded set of published due workflows in a serializable transaction, creates version-specific queued runs, and advances each schedule's next occurrence.

Support only one active scheduler instance until an explicit atomic claim/lease model and scale-out tests are implemented.

## Consequences

### Positive

- Scheduled runs survive web or worker restarts.
- No broker is required for the first deployment.
- Metadata and enqueue state can be committed transactionally.
- Windows Service management provides automatic startup and established service identities.

### Negative

- Polling introduces database load and latency bounded by the polling interval.
- Serializable scheduling transactions can contend as volume grows.
- SQL Server becomes a critical dependency for control-plane availability.
- Multiple active schedulers are unsafe until lease/claim semantics exist.
- Long-term high-throughput execution may require a broker or a different queue design.

## Alternatives considered

- **In-process `BackgroundService` in THub.Web:** rejected because web lifecycle is not a durable execution boundary.
- **Quartz.NET/Hangfire as the primary model:** not selected initially because THub requires its own versioned run/step model and SQL coordination remains necessary.
- **External broker:** deferred until measured scale or isolation needs justify another operational dependency.

## Follow-up

- Design atomic run claims, expiring leases, heartbeats, attempts, and abandoned-run recovery.
- Add readiness and queue-lag metrics.
- Revisit broker use when measured throughput/latency requires it.

