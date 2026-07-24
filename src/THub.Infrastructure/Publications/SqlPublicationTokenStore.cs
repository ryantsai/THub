using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Publications;
using THub.Domain.Publications;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Publications;

public sealed class SqlPublicationTokenStore(
    IDbContextFactory<THubDbContext> contextFactory) : IPublicationTokenStore
{
    public async Task<PublicationAccessToken?> FindBySelectorAsync(
        string selector,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.PublicationAccessTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(token => token.Selector == selector, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PublicationAccessToken>> ListAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.PublicationAccessTokens
            .AsNoTracking()
            .Where(token => token.PublicationId == publicationId)
            .OrderByDescending(token => token.CreatedAtUtc)
            .ThenBy(token => token.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PublicationTokenWriteStatus> AddAsync(
        PublicationAccessToken accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accessToken);
        try
        {
            return await PublicationDbExecution.InTransactionAsync(
                contextFactory,
                async (db, token) =>
                {
                    db.PublicationAccessTokens.Add(accessToken);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    return PublicationTokenWriteStatus.Saved;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(
            exception,
            "IX_PublicationAccessTokens_Selector"))
        {
            return PublicationTokenWriteStatus.DuplicateSelector;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return PublicationTokenWriteStatus.Conflict;
        }
        catch (DbUpdateConcurrencyException)
        {
            return PublicationTokenWriteStatus.Conflict;
        }
    }

    public async Task<PublicationTokenRevocationStatus> RevokeAsync(
        Guid publicationId,
        Guid tokenId,
        string revokedBy,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revokedBy);
        try
        {
            return await PublicationDbExecution.InTransactionAsync(
                contextFactory,
                async (db, token) =>
                {
                    var accessToken = await db.PublicationAccessTokens
                        .SingleOrDefaultAsync(
                            candidate => candidate.Id == tokenId &&
                                candidate.PublicationId == publicationId,
                            token)
                        .ConfigureAwait(false);
                    if (accessToken is null)
                    {
                        return PublicationTokenRevocationStatus.NotFound;
                    }

                    if (accessToken.RevokedAtUtc is not null)
                    {
                        return PublicationTokenRevocationStatus.AlreadyRevoked;
                    }

                    accessToken.Revoke(revokedBy, revokedAtUtc);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    return PublicationTokenRevocationStatus.Revoked;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return PublicationTokenRevocationStatus.Conflict;
        }
    }

    public async Task<PublicationAcceptedUseStatus> TryRecordAcceptedUseAsync(
        Guid tokenId,
        Guid publicationId,
        Guid publicationVersionId,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken)
    {
        var acceptedAt = acceptedAtUtc.ToUniversalTime();
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connection = (SqlConnection)db.Database.GetDbConnection();

            // Execute directly so the configured retrying EF execution strategy cannot replay a
            // counter increment after an ambiguous network failure. One guarded transaction
            // performs every state check, the counter update, and its audit insert atomically;
            // an uncertain result fails closed.
            const string updateText = """
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                DECLARE @affected int;

                BEGIN TRANSACTION;

                UPDATE token
                SET token.[AcceptedRequestCount] = token.[AcceptedRequestCount] + CONVERT(bigint, 1),
                    token.[LastUsedAtUtc] = CASE
                        WHEN token.[LastUsedAtUtc] IS NULL OR token.[LastUsedAtUtc] < @acceptedAtUtc
                            THEN @acceptedAtUtc
                        ELSE token.[LastUsedAtUtc]
                    END
                FROM [thub].[PublicationAccessTokens] AS token
                WHERE token.[Id] = @tokenId
                  AND token.[PublicationId] = @publicationId
                  AND token.[RevokedAtUtc] IS NULL
                  AND token.[CreatedAtUtc] <= @acceptedAtUtc
                  AND token.[ExpiresAtUtc] > @acceptedAtUtc
                  AND EXISTS
                  (
                      SELECT 1
                      FROM [thub].[Publications] AS publication
                      WHERE publication.[Id] = @publicationId
                        AND publication.[Kind] = N'RestApi'
                        AND publication.[State] = N'Active'
                        AND publication.[ActiveVersionId] = @publicationVersionId
                  );

                SET @affected = @@ROWCOUNT;
                IF @affected = 1
                BEGIN
                    INSERT INTO [thub].[AuditRecords]
                    (
                        [Id],
                        [OccurredAtUtc],
                        [ActorKind],
                        [ActorIdentifier],
                        [Source],
                        [Action],
                        [Outcome],
                        [ResourceType],
                        [ResourceIdentifier],
                        [CorrelationIdentifier]
                    )
                    VALUES
                    (
                        NEWID(),
                        @acceptedAtUtc,
                        N'ApiToken',
                        CONVERT(nvarchar(36), @tokenId),
                        N'thub.publications',
                        N'publication-api.request.accepted',
                        N'Succeeded',
                        N'publication',
                        CONVERT(nvarchar(36), @publicationId),
                        CONVERT(nvarchar(36), @publicationVersionId)
                    );
                END

                COMMIT TRANSACTION;

                SELECT @affected;
                """;
            await using var update = connection.CreateCommand();
            update.CommandText = updateText;
            update.CommandTimeout = 30;
            AddMeterParameters(update, tokenId, publicationId, publicationVersionId, acceptedAt);
            var affected = Convert.ToInt32(
                await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (affected == 1)
            {
                return PublicationAcceptedUseStatus.Recorded;
            }

            const string statusText = """
                SELECT
                    CASE WHEN EXISTS
                    (
                        SELECT 1
                        FROM [thub].[PublicationAccessTokens] AS token
                        WHERE token.[Id] = @tokenId
                          AND token.[PublicationId] = @publicationId
                          AND token.[RevokedAtUtc] IS NULL
                          AND token.[CreatedAtUtc] <= @acceptedAtUtc
                          AND token.[ExpiresAtUtc] > @acceptedAtUtc
                    ) THEN 1 ELSE 0 END,
                    CASE WHEN EXISTS
                    (
                        SELECT 1
                        FROM [thub].[Publications] AS publication
                        WHERE publication.[Id] = @publicationId
                          AND publication.[Kind] = N'RestApi'
                          AND publication.[State] = N'Active'
                          AND publication.[ActiveVersionId] = @publicationVersionId
                    ) THEN 1 ELSE 0 END;
                """;
            await using var status = connection.CreateCommand();
            status.CommandText = statusText;
            status.CommandTimeout = 30;
            AddMeterParameters(status, tokenId, publicationId, publicationVersionId, acceptedAt);
            await using var reader = await status.ExecuteReaderAsync(
                System.Data.CommandBehavior.SingleRow,
                cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return PublicationAcceptedUseStatus.MeteringUnavailable;
            }

            var tokenUsable = reader.GetInt32(0) == 1;
            if (!tokenUsable)
            {
                return PublicationAcceptedUseStatus.TokenUnavailable;
            }

            return reader.GetInt32(1) == 1
                ? PublicationAcceptedUseStatus.MeteringUnavailable
                : PublicationAcceptedUseStatus.PublicationUnavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqlException)
        {
            return PublicationAcceptedUseStatus.MeteringUnavailable;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string? indexName = null) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 } sqlException &&
        (indexName is null || sqlException.Message.Contains(indexName, StringComparison.Ordinal));

    private static void AddMeterParameters(
        SqlCommand command,
        Guid tokenId,
        Guid publicationId,
        Guid publicationVersionId,
        DateTimeOffset acceptedAtUtc)
    {
        command.Parameters.Add(new SqlParameter("@tokenId", System.Data.SqlDbType.UniqueIdentifier)
        {
            Value = tokenId,
        });
        command.Parameters.Add(new SqlParameter("@publicationId", System.Data.SqlDbType.UniqueIdentifier)
        {
            Value = publicationId,
        });
        command.Parameters.Add(new SqlParameter("@publicationVersionId", System.Data.SqlDbType.UniqueIdentifier)
        {
            Value = publicationVersionId,
        });
        command.Parameters.Add(new SqlParameter("@acceptedAtUtc", System.Data.SqlDbType.DateTimeOffset)
        {
            Value = acceptedAtUtc,
        });
    }
}
