using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Alerts;
using THub.Domain.Alerts;
using THub.Domain.Runs;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Alerts;

public sealed class SqlAlertDeliveryStore(
    IDbContextFactory<THubDbContext> contextFactory) : IAlertDeliveryStore
{
    private const int MaximumClaimScanAttempts = 5;

    private readonly record struct ClaimScanResult(
        ClaimedAlertDelivery? Delivery,
        bool ShouldRescan);

    private readonly IDbContextFactory<THubDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task<AlertEnqueueStoreResult> EnqueueEmailActionAsync(
        AlertDelivery delivery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.Source != AlertDeliverySource.EmailAction)
        {
            throw new ArgumentException(
                "Only Email action deliveries can be enqueued through this operation.",
                nameof(delivery));
        }

        return await THubDbExecution.ExecuteAsync(
            _contextFactory,
            async operationToken =>
            {
                await using var db = await _contextFactory.CreateDbContextAsync(operationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    operationToken);

                var existingId = await db.AlertDeliveries
                    .Where(candidate => candidate.DeduplicationKey == delivery.DeduplicationKey)
                    .Select(candidate => (Guid?)candidate.Id)
                    .SingleOrDefaultAsync(operationToken);
                if (existingId is Guid duplicateId)
                {
                    await transaction.CommitAsync(operationToken);
                    return new AlertEnqueueStoreResult(
                        AlertEnqueueStatus.AlreadyEnqueued,
                        duplicateId);
                }

                var profile = await db.EmailDeliveryProfiles.SingleOrDefaultAsync(
                    candidate => candidate.Id == delivery.EmailDeliveryProfileId,
                    operationToken);
                var runExists = await db.WorkflowRuns.AnyAsync(
                    run => run.Id == delivery.WorkflowRunId,
                    operationToken);
                var stepExists = delivery.WorkflowStepRunId is Guid stepId
                    && await db.WorkflowStepRuns.AnyAsync(
                        step => step.Id == stepId
                            && step.WorkflowRunId == delivery.WorkflowRunId
                            && step.NodeId == delivery.WorkflowNodeId,
                        operationToken);
                if (profile is null || !profile.IsEnabled || !runExists || !stepExists)
                {
                    return new AlertEnqueueStoreResult(
                        AlertEnqueueStatus.ReferencedResourceUnavailable);
                }

                try
                {
                    profile.ValidateMessage(delivery.Message);
                }
                catch (InvalidOperationException)
                {
                    return new AlertEnqueueStoreResult(
                        AlertEnqueueStatus.ReferencedResourceUnavailable);
                }

                db.AlertDeliveries.Add(delivery);
                try
                {
                    await db.SaveChangesAsync(operationToken);
                    await transaction.CommitAsync(operationToken);
                    return new AlertEnqueueStoreResult(AlertEnqueueStatus.Enqueued, delivery.Id);
                }
                catch (DbUpdateException exception) when (IsUniqueViolation(exception))
                {
                    await transaction.RollbackAsync(operationToken);
                    await using var duplicateDb =
                        await _contextFactory.CreateDbContextAsync(operationToken);
                    var retrievedDuplicateId = await duplicateDb.AlertDeliveries
                        .Where(candidate => candidate.DeduplicationKey == delivery.DeduplicationKey)
                        .Select(candidate => (Guid?)candidate.Id)
                        .SingleOrDefaultAsync(operationToken);
                    return retrievedDuplicateId is Guid foundId
                        ? new AlertEnqueueStoreResult(AlertEnqueueStatus.AlreadyEnqueued, foundId)
                        : new AlertEnqueueStoreResult(AlertEnqueueStatus.Conflict);
                }
                catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
                {
                    return new AlertEnqueueStoreResult(
                        AlertEnqueueStatus.ReferencedResourceUnavailable);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return new AlertEnqueueStoreResult(AlertEnqueueStatus.Conflict);
                }
            },
            cancellationToken);
    }

    public async Task<ClaimedAlertDelivery?> TryClaimNextAsync(
        string leaseOwner,
        DateTimeOffset claimedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);

        for (var scan = 0; scan < MaximumClaimScanAttempts; scan++)
        {
            var result = await THubDbExecution.ExecuteAsync(
                _contextFactory,
                async operationToken =>
                {
                    await using var db = await _contextFactory.CreateDbContextAsync(operationToken);
                    await using var transaction = await db.Database.BeginTransactionAsync(
                        IsolationLevel.ReadCommitted,
                        operationToken);
                    // The held profile-row update lock serializes claim admission for one profile
                    // across worker processes, so the persisted MaximumConcurrentSends count
                    // cannot race.
                    var delivery = (await db.AlertDeliveries.FromSqlRaw(
                            """
                            SELECT TOP (1) delivery.*
                            FROM [thub].[AlertDeliveries] AS delivery WITH (UPDLOCK, READPAST, ROWLOCK)
                            INNER JOIN [thub].[EmailDeliveryProfiles] AS profile WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                                ON profile.[Id] = delivery.[EmailDeliveryProfileId]
                            WHERE
                                ((delivery.[Status] IN (@pending, @retryScheduled)
                                    AND delivery.[NextAttemptAtUtc] <= @claimedAtUtc)
                                OR
                                (delivery.[Status] = @sending
                                    AND (delivery.[LeaseExpiresAtUtc] IS NULL
                                        OR delivery.[LeaseExpiresAtUtc] <= @claimedAtUtc)))
                                AND
                                (
                                    SELECT COUNT_BIG(*)
                                    FROM [thub].[AlertDeliveries] AS activeDelivery
                                    WHERE activeDelivery.[EmailDeliveryProfileId] = delivery.[EmailDeliveryProfileId]
                                        AND activeDelivery.[Status] = @sending
                                        AND activeDelivery.[LeaseExpiresAtUtc] > @claimedAtUtc
                                ) < COALESCE(
                                    TRY_CONVERT(
                                        bigint,
                                        JSON_VALUE(profile.[LimitsJson], '$.maximumConcurrentSends')),
                                    0)
                            ORDER BY delivery.[NextAttemptAtUtc], delivery.[CreatedAtUtc], delivery.[Id]
                            """,
                            new SqlParameter("@pending", AlertDeliveryStatus.Pending.ToString()),
                            new SqlParameter("@retryScheduled", AlertDeliveryStatus.RetryScheduled.ToString()),
                            new SqlParameter("@sending", AlertDeliveryStatus.Sending.ToString()),
                            new SqlParameter("@claimedAtUtc", claimedAtUtc.ToUniversalTime()))
                        .ToListAsync(operationToken)).SingleOrDefault();
                    if (delivery is null)
                    {
                        await transaction.CommitAsync(operationToken);
                        return new ClaimScanResult(null, ShouldRescan: false);
                    }

                    if (!delivery.TryClaim(leaseOwner, claimedAtUtc, leaseDuration))
                    {
                        // TryClaim may dead-letter a row that exhausted its maximum attempt count.
                        await db.SaveChangesAsync(operationToken);
                        await transaction.CommitAsync(operationToken);
                        return new ClaimScanResult(null, ShouldRescan: true);
                    }

                    var profile = await db.EmailDeliveryProfiles
                        .AsNoTracking()
                        .SingleAsync(
                            candidate => candidate.Id == delivery.EmailDeliveryProfileId,
                            operationToken);
                    try
                    {
                        await db.SaveChangesAsync(operationToken);
                        await transaction.CommitAsync(operationToken);
                        return new ClaimScanResult(
                            new ClaimedAlertDelivery(delivery, profile),
                            ShouldRescan: false);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        await transaction.RollbackAsync(operationToken);
                        return new ClaimScanResult(null, ShouldRescan: true);
                    }
                },
                cancellationToken);

            if (result.Delivery is not null)
            {
                return result.Delivery;
            }

            if (!result.ShouldRescan)
            {
                return null;
            }
        }

        return null;
    }

    public async Task<AlertDeliveryTransitionStatus> RecordDeliveredAsync(
        Guid deliveryId,
        string leaseOwner,
        DateTimeOffset deliveredAtUtc,
        string? providerMessageId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var delivery = await db.AlertDeliveries.SingleOrDefaultAsync(
            candidate => candidate.Id == deliveryId,
            cancellationToken);
        if (delivery is null)
        {
            return AlertDeliveryTransitionStatus.NotFound;
        }

        try
        {
            delivery.RecordDelivered(leaseOwner, deliveredAtUtc, providerMessageId);
            await db.SaveChangesAsync(cancellationToken);
            return AlertDeliveryTransitionStatus.Saved;
        }
        catch (InvalidOperationException)
        {
            return AlertDeliveryTransitionStatus.LeaseLost;
        }
        catch (DbUpdateConcurrencyException)
        {
            return AlertDeliveryTransitionStatus.Conflict;
        }
    }

    public async Task<AlertDeliveryTransitionStatus> RecordFailureAsync(
        Guid deliveryId,
        string leaseOwner,
        ExecutionError error,
        DateTimeOffset failedAtUtc,
        DateTimeOffset? nextAttemptAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var delivery = await db.AlertDeliveries.SingleOrDefaultAsync(
            candidate => candidate.Id == deliveryId,
            cancellationToken);
        if (delivery is null)
        {
            return AlertDeliveryTransitionStatus.NotFound;
        }

        try
        {
            delivery.RecordFailure(
                leaseOwner,
                error,
                failedAtUtc,
                nextAttemptAtUtc);
            await db.SaveChangesAsync(cancellationToken);
            return AlertDeliveryTransitionStatus.Saved;
        }
        catch (InvalidOperationException)
        {
            return AlertDeliveryTransitionStatus.LeaseLost;
        }
        catch (DbUpdateConcurrencyException)
        {
            return AlertDeliveryTransitionStatus.Conflict;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private static bool IsForeignKeyViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 547 };
}
