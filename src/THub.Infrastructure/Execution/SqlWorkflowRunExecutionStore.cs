using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Execution;
using THub.Domain.Runs;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Execution;

public sealed class SqlWorkflowRunExecutionStore(
    IDbContextFactory<THubDbContext> contextFactory) : IWorkflowRunExecutionStore
{
    private const string ClaimSql = """
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

        DECLARE @CandidateRunId uniqueidentifier;
        DECLARE @CandidateWorkflowId uniqueidentifier;
        DECLARE @ClaimedRunId uniqueidentifier;
        DECLARE @LockResult int;
        DECLARE @LockResource nvarchar(255);

        BEGIN TRANSACTION;

        SELECT TOP (1)
            @CandidateRunId = run.[Id],
            @CandidateWorkflowId = run.[WorkflowId]
        FROM [thub].[WorkflowRuns] AS run WITH (UPDLOCK, READPAST, ROWLOCK)
        INNER JOIN [thub].[WorkflowVersions] AS version
            ON version.[Id] = run.[WorkflowVersionId]
            AND version.[WorkflowId] = run.[WorkflowId]
            AND version.[Version] = run.[WorkflowVersion]
        WHERE
            ((run.[Status] = N'Queued' AND run.[CancellationRequestedAtUtc] IS NULL)
                OR (run.[Status] = N'Running'
                    AND (run.[LeaseExpiresAtUtc] IS NULL OR run.[LeaseExpiresAtUtc] <= @ClaimedAtUtc)))
            AND run.[AttemptCount] < 1000
            AND NOT EXISTS (
                SELECT 1
                FROM [thub].[WorkflowRuns] AS activeRun
                WHERE activeRun.[WorkflowId] = run.[WorkflowId]
                    AND activeRun.[Id] <> run.[Id]
                    AND activeRun.[Status] = N'Running'
                    AND activeRun.[LeaseExpiresAtUtc] > @ClaimedAtUtc)
        ORDER BY
            CASE WHEN run.[Status] = N'Running' THEN 0 ELSE 1 END,
            run.[QueuedAtUtc],
            run.[Id];

        IF @CandidateRunId IS NOT NULL
        BEGIN
            SET @LockResource = CONCAT(N'THub.WorkflowExecution.', CONVERT(nvarchar(36), @CandidateWorkflowId));
            EXEC @LockResult = sys.sp_getapplock
                @Resource = @LockResource,
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 0;

            IF @LockResult >= 0
                AND NOT EXISTS (
                    SELECT 1
                    FROM [thub].[WorkflowRuns] AS activeRun WITH (UPDLOCK, HOLDLOCK)
                    WHERE activeRun.[WorkflowId] = @CandidateWorkflowId
                        AND activeRun.[Id] <> @CandidateRunId
                        AND activeRun.[Status] = N'Running'
                        AND activeRun.[LeaseExpiresAtUtc] > @ClaimedAtUtc)
            BEGIN
                UPDATE [thub].[WorkflowRuns]
                SET [Status] = N'Running',
                    [StartedAtUtc] = COALESCE([StartedAtUtc], @ClaimedAtUtc),
                    [AttemptCount] = [AttemptCount] + 1,
                    [LeaseOwner] = @LeaseOwner,
                    [LastHeartbeatAtUtc] = @ClaimedAtUtc,
                    [LeaseExpiresAtUtc] = @LeaseExpiresAtUtc
                WHERE [Id] = @CandidateRunId
                    AND (([Status] = N'Queued' AND [CancellationRequestedAtUtc] IS NULL)
                        OR ([Status] = N'Running'
                            AND ([LeaseExpiresAtUtc] IS NULL OR [LeaseExpiresAtUtc] <= @ClaimedAtUtc)));

                IF @@ROWCOUNT = 1
                    SET @ClaimedRunId = @CandidateRunId;
            END
        END

        COMMIT TRANSACTION;

        SELECT
            run.[Id],
            run.[WorkflowId],
            run.[WorkflowVersionId],
            run.[WorkflowVersion],
            version.[SchemaVersion],
            version.[GraphJson],
            version.[Checksum],
            run.[LeaseExpiresAtUtc],
            CONVERT(bit, CASE WHEN run.[CancellationRequestedAtUtc] IS NULL THEN 0 ELSE 1 END)
        FROM [thub].[WorkflowRuns] AS run
        INNER JOIN [thub].[WorkflowVersions] AS version ON version.[Id] = run.[WorkflowVersionId]
        WHERE run.[Id] = @ClaimedRunId;
        """;

    private readonly IDbContextFactory<THubDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task<WorkflowRunExecutionClaim?> TryClaimNextAsync(
        string leaseOwner,
        DateTimeOffset claimedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateLease(leaseOwner, leaseDuration);
        var now = claimedAtUtc.ToUniversalTime();
        var expiresAt = now.Add(leaseDuration);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ClaimSql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = 30;
        command.Parameters.Add(new SqlParameter("@ClaimedAtUtc", SqlDbType.DateTimeOffset)
        {
            Value = now
        });
        command.Parameters.Add(new SqlParameter("@LeaseExpiresAtUtc", SqlDbType.DateTimeOffset)
        {
            Value = expiresAt
        });
        command.Parameters.Add(new SqlParameter("@LeaseOwner", SqlDbType.NVarChar, WorkflowRun.MaximumLeaseOwnerLength)
        {
            Value = leaseOwner
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WorkflowRunExecutionClaim(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetBoolean(8));
    }

    public async Task<WorkflowLeaseRenewalStatus> RenewLeaseAsync(
        Guid workflowRunId,
        string leaseOwner,
        DateTimeOffset heartbeatAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateRunId(workflowRunId);
        ValidateLease(leaseOwner, leaseDuration);
        var now = heartbeatAtUtc.ToUniversalTime();

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await db.WorkflowRuns.SingleOrDefaultAsync(
            candidate => candidate.Id == workflowRunId,
            cancellationToken);
        if (run is null)
        {
            return WorkflowLeaseRenewalStatus.NotFound;
        }

        if (run.Status != WorkflowRunStatus.Running
            || !string.Equals(run.LeaseOwner, leaseOwner, StringComparison.Ordinal)
            || run.LeaseExpiresAtUtc is not DateTimeOffset leaseExpiresAt
            || leaseExpiresAt <= now)
        {
            return WorkflowLeaseRenewalStatus.LeaseLost;
        }

        try
        {
            run.RenewLease(leaseOwner, now, leaseDuration);
            await db.SaveChangesAsync(cancellationToken);
            return run.CancellationRequested
                ? WorkflowLeaseRenewalStatus.CancellationRequested
                : WorkflowLeaseRenewalStatus.Renewed;
        }
        catch (DbUpdateConcurrencyException)
        {
            return WorkflowLeaseRenewalStatus.LeaseLost;
        }
    }

    public async Task<WorkflowRun?> LoadOwnedRunAsync(
        Guid workflowRunId,
        string leaseOwner,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateRunId(workflowRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        var now = observedAtUtc.ToUniversalTime();

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await db.WorkflowRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == workflowRunId, cancellationToken);
        if (run is null
            || run.Status != WorkflowRunStatus.Running
            || !string.Equals(run.LeaseOwner, leaseOwner, StringComparison.Ordinal)
            || run.LeaseExpiresAtUtc is not DateTimeOffset expiresAt
            || expiresAt <= now)
        {
            return null;
        }

        return run;
    }

    private static void ValidateLease(string leaseOwner, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseOwner.Length > WorkflowRun.MaximumLeaseOwnerLength)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseOwner));
        }

        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }

    private static void ValidateRunId(Guid workflowRunId)
    {
        if (workflowRunId == Guid.Empty)
        {
            throw new ArgumentException("A workflow run id is required.", nameof(workflowRunId));
        }
    }
}
