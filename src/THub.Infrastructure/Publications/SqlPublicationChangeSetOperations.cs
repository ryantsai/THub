using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Publications;
using THub.Domain.Publications;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Publications;

public sealed class SqlPublicationChangeSetQueryStore(
    IDbContextFactory<THubDbContext> contextFactory) : IPublicationChangeSetQueryStore
{
    public async Task<PublicationChangeSetQueryPage> ListAsync(
        Guid publicationId,
        IReadOnlyCollection<PublicationChangeSetStatus> statuses,
        int take,
        DateTimeOffset? beforeSubmittedAtUtc,
        Guid? beforeId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var query = db.PublicationChangeSets
            .AsNoTracking()
            .Where(changeSet => changeSet.PublicationId == publicationId);
        if (statuses.Count > 0)
        {
            query = query.Where(changeSet => statuses.Contains(changeSet.Status));
        }

        if (beforeSubmittedAtUtc is DateTimeOffset submittedAt && beforeId is Guid id)
        {
            query = query.Where(changeSet =>
                changeSet.SubmittedAtUtc < submittedAt ||
                (changeSet.SubmittedAtUtc == submittedAt && changeSet.Id.CompareTo(id) < 0));
        }

        var rows = await query
            .OrderByDescending(changeSet => changeSet.SubmittedAtUtc)
            .ThenByDescending(changeSet => changeSet.Id)
            .Take(checked(take + 1))
            .Include("_changes")
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new PublicationChangeSetQueryPage(rows, hasMore);
    }

    public async Task<PublicationChangeSet?> FindDetailAsync(
        Guid publicationId,
        Guid changeSetId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.PublicationChangeSets
            .AsNoTracking()
            .Include("_changes")
            .AsSplitQuery()
            .SingleOrDefaultAsync(
                changeSet => changeSet.PublicationId == publicationId && changeSet.Id == changeSetId,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class SqlPublicationChangeSetClaimStore(
    IDbContextFactory<THubDbContext> contextFactory) : IPublicationChangeSetClaimStore
{
    public async Task<PublicationChangeSetClaim?> ClaimNextAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseOwner.Length > Publication.MaximumIdentityLength ||
            leaseDuration < TimeSpan.FromMinutes(1) ||
            leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var now = nowUtc.ToUniversalTime();
        var staleBefore = now - leaseDuration;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        const string claimText = """
            SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

            UPDATE [thub].[PublicationChangeSets]
            SET [Status] = N'Failed',
                [CompletedAtUtc] = @nowUtc,
                [OutcomeDetail] = N'Apply lease expired; reconcile the source before resubmitting.',
                [UpdatedAtUtc] = @nowUtc
            WHERE [Status] = N'Applying'
              AND [UpdatedAtUtc] < @staleBeforeUtc;

            ;WITH candidate AS
            (
                SELECT TOP (1) change_set.[Id]
                FROM [thub].[PublicationChangeSets] AS change_set WITH (UPDLOCK, READPAST, ROWLOCK)
                INNER JOIN [thub].[Publications] AS publication
                    ON publication.[Id] = change_set.[PublicationId]
                WHERE change_set.[Status] = N'Approved'
                  AND publication.[Kind] = N'Editor'
                  AND publication.[State] = N'Active'
                  AND publication.[ActiveVersionId] = change_set.[PublicationVersionId]
                ORDER BY
                    change_set.[SubmittedAtUtc],
                    change_set.[Id]
            )
            UPDATE change_set
            SET change_set.[Status] = N'Applying',
                change_set.[ApplyStartedBy] = @leaseOwner,
                change_set.[ApplyStartedAtUtc] = @nowUtc,
                change_set.[UpdatedAtUtc] = @nowUtc
            OUTPUT inserted.[Id]
            FROM [thub].[PublicationChangeSets] AS change_set
            INNER JOIN candidate ON candidate.[Id] = change_set.[Id];
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = claimText;
        command.CommandTimeout = 30;
        command.Parameters.Add(new SqlParameter("@staleBeforeUtc", System.Data.SqlDbType.DateTimeOffset)
        {
            Value = staleBefore,
        });
        command.Parameters.Add(new SqlParameter("@nowUtc", System.Data.SqlDbType.DateTimeOffset)
        {
            Value = now,
        });
        command.Parameters.Add(new SqlParameter(
            "@leaseOwner",
            System.Data.SqlDbType.NVarChar,
            Publication.MaximumIdentityLength)
        {
            Value = leaseOwner,
        });
        var claimedId = (Guid?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (claimedId is null)
        {
            return null;
        }

        var changeSet = await db.PublicationChangeSets
            .AsNoTracking()
            .Include("_changes")
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == claimedId.Value, cancellationToken)
            .ConfigureAwait(false);
        return changeSet is null
            ? null
            : new PublicationChangeSetClaim(changeSet, leaseOwner, now, now + leaseDuration);
    }

    public async Task<bool> RenewAsync(
        Guid changeSetId,
        string leaseOwner,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        const string commandText = """
            UPDATE [thub].[PublicationChangeSets]
            SET [UpdatedAtUtc] = @nowUtc
            WHERE [Id] = @changeSetId
              AND [Status] = N'Applying'
              AND [ApplyStartedBy] = @leaseOwner;
            """;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 30;
        AddLeaseParameters(command, changeSetId, leaseOwner, nowUtc);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> CompleteAsync(
        Guid changeSetId,
        string leaseOwner,
        PublicationChangeSetApplyOutcome outcome,
        string? detail,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(outcome) || detail?.Length > PublicationChangeSet.MaximumCommentLength)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        var status = outcome switch
        {
            PublicationChangeSetApplyOutcome.Applied => "Applied",
            PublicationChangeSetApplyOutcome.Conflict => "Conflict",
            PublicationChangeSetApplyOutcome.Failed => "Failed",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        const string commandText = """
            UPDATE [thub].[PublicationChangeSets]
            SET [Status] = @status,
                [CompletedAtUtc] = @nowUtc,
                [OutcomeDetail] = @detail,
                [UpdatedAtUtc] = @nowUtc
            WHERE [Id] = @changeSetId
              AND [Status] = N'Applying'
              AND [ApplyStartedBy] = @leaseOwner;
            """;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 30;
        AddLeaseParameters(command, changeSetId, leaseOwner, completedAtUtc);
        command.Parameters.Add(new SqlParameter("@status", System.Data.SqlDbType.NVarChar, 32)
        {
            Value = status,
        });
        command.Parameters.Add(new SqlParameter(
            "@detail",
            System.Data.SqlDbType.NVarChar,
            PublicationChangeSet.MaximumCommentLength)
        {
            IsNullable = true,
            Value = detail is null ? DBNull.Value : detail,
        });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void AddLeaseParameters(
        SqlCommand command,
        Guid changeSetId,
        string leaseOwner,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseOwner.Length > Publication.MaximumIdentityLength)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseOwner));
        }

        command.Parameters.Add(new SqlParameter("@changeSetId", System.Data.SqlDbType.UniqueIdentifier)
        {
            Value = changeSetId,
        });
        command.Parameters.Add(new SqlParameter(
            "@leaseOwner",
            System.Data.SqlDbType.NVarChar,
            Publication.MaximumIdentityLength)
        {
            Value = leaseOwner,
        });
        command.Parameters.Add(new SqlParameter("@nowUtc", System.Data.SqlDbType.DateTimeOffset)
        {
            Value = nowUtc.ToUniversalTime(),
        });
    }
}
