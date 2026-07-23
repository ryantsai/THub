using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using THub.Application.Connections;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;
using THub.Infrastructure.Connections;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Publications;

public sealed class SqlPublicationChangeSetProcessor(
    IDbContextFactory<THubDbContext> contextFactory,
    IPublicationChangeSetClaimStore claimStore,
    IPublicationSourceSchemaInspector schemaInspector,
    ConnectionConfigurationSerializer configurationSerializer,
    SqlServerConnectionStringFactory connectionStringFactory,
    TimeProvider timeProvider,
    ILogger<SqlPublicationChangeSetProcessor> logger) : IPublicationChangeSetProcessor
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);
    private const int LeaseRenewalChangeInterval = 25;

    public async Task<PublicationChangeSetProcessResult> ProcessNextAsync(
        string workerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (workerId.Length > Publication.MaximumIdentityLength)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        var claim = await claimStore.ClaimNextAsync(
                workerId,
                timeProvider.GetUtcNow(),
                LeaseDuration,
                cancellationToken)
            .ConfigureAwait(false);
        if (claim is null)
        {
            return new PublicationChangeSetProcessResult(PublicationChangeSetProcessStatus.NoWork, null);
        }

        try
        {
            var context = await LoadApplyContextAsync(claim.ChangeSet, cancellationToken)
                .ConfigureAwait(false);
            if (context is null)
            {
                return await CompleteAsync(
                    claim,
                    PublicationChangeSetApplyOutcome.Conflict,
                    "The active editor publication or its exact version is no longer available.",
                    PublicationChangeSetProcessStatus.Conflict,
                    cancellationToken).ConfigureAwait(false);
            }

            if (claim.ChangeSet.Changes.Count > context.Configuration.MaximumBatchRows)
            {
                return await CompleteAsync(
                    claim,
                    PublicationChangeSetApplyOutcome.Failed,
                    "The change set exceeds the connector's bounded batch size.",
                    PublicationChangeSetProcessStatus.Failed,
                    cancellationToken).ConfigureAwait(false);
            }

            var inspection = await schemaInspector.InspectObjectAsync(
                    context.Connection,
                    context.Version.SourceSchema,
                    context.Version.SourceObject,
                    cancellationToken)
                .ConfigureAwait(false);
            if (inspection.Status != PublicationSourceInspectionStatus.Success || inspection.Value is null)
            {
                return await CompleteAsync(
                    claim,
                    inspection.Status == PublicationSourceInspectionStatus.NotFound
                        ? PublicationChangeSetApplyOutcome.Conflict
                        : PublicationChangeSetApplyOutcome.Failed,
                    inspection.Status == PublicationSourceInspectionStatus.NotFound
                        ? "The source object no longer exists."
                        : "The source schema could not be revalidated.",
                    inspection.Status == PublicationSourceInspectionStatus.NotFound
                        ? PublicationChangeSetProcessStatus.Conflict
                        : PublicationChangeSetProcessStatus.Failed,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(
                    inspection.Value.SchemaFingerprint,
                    context.Version.SchemaFingerprint,
                    StringComparison.Ordinal))
            {
                return await CompleteAsync(
                    claim,
                    PublicationChangeSetApplyOutcome.Conflict,
                    "The source schema or foreign-key metadata changed after publication activation.",
                    PublicationChangeSetProcessStatus.Conflict,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!await claimStore.RenewAsync(
                    claim.ChangeSet.Id,
                    claim.LeaseOwner,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false))
            {
                return new PublicationChangeSetProcessResult(
                    PublicationChangeSetProcessStatus.LeaseLost,
                    claim.ChangeSet.Id,
                    "The apply lease was lost before source mutation began.");
            }

            await ApplyChangesAsync(context, claim, cancellationToken).ConfigureAwait(false);
            return await CompleteAsync(
                claim,
                PublicationChangeSetApplyOutcome.Applied,
                null,
                PublicationChangeSetProcessStatus.Applied,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PublicationApplyConflictException exception)
        {
            logger.LogInformation(
                "Editor apply conflict for change set {ChangeSetId}: {ConflictCode}.",
                claim.ChangeSet.Id,
                exception.Code);
            return await CompleteAsync(
                claim,
                PublicationChangeSetApplyOutcome.Conflict,
                exception.SafeDetail,
                PublicationChangeSetProcessStatus.Conflict,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627 or 547)
        {
            logger.LogInformation(
                "Editor apply constraint conflict for change set {ChangeSetId}; SQL error {SqlErrorNumber}.",
                claim.ChangeSet.Id,
                exception.Number);
            return await CompleteAsync(
                claim,
                PublicationChangeSetApplyOutcome.Conflict,
                "A unique or foreign-key constraint rejected the staged values.",
                PublicationChangeSetProcessStatus.Conflict,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedApplyFailure(exception))
        {
            logger.LogWarning(
                exception,
                "Editor apply failed for change set {ChangeSetId}.",
                claim.ChangeSet.Id);
            return await CompleteAsync(
                claim,
                PublicationChangeSetApplyOutcome.Failed,
                "The staged changes could not be applied within the configured connector policy.",
                PublicationChangeSetProcessStatus.Failed,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PublicationApplyContext?> LoadApplyContextAsync(
        PublicationChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var publication = await db.Publications
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == changeSet.PublicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null ||
            publication.Kind != PublicationKind.Editor ||
            publication.State != PublicationState.Active ||
            publication.ActiveVersionId != changeSet.PublicationVersionId)
        {
            return null;
        }

        var version = await db.PublicationVersions
            .AsNoTracking()
            .Include("_columns.ForeignKey")
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate =>
                candidate.Id == changeSet.PublicationVersionId &&
                candidate.PublicationId == changeSet.PublicationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (version is null ||
            version.SourceObjectKind != PublicationSourceObjectKind.Table ||
            version.ConcurrencyMode == PublicationConcurrencyMode.ReadOnly)
        {
            return null;
        }

        var connection = await db.Connections
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == version.ConnectionId, cancellationToken)
            .ConfigureAwait(false);
        if (connection is null || !connection.IsEnabled || connection.Kind != ConnectionKind.SqlServer)
        {
            return null;
        }

        var configuration = configurationSerializer.Deserialize(connection) as SqlServerConnectionConfiguration;
        return configuration is null
            ? null
            : new PublicationApplyContext(version, connection, configuration, changeSet);
    }

    private async Task ApplyChangesAsync(
        PublicationApplyContext context,
        PublicationChangeSetClaim claim,
        CancellationToken cancellationToken)
    {
        var connectionString = (await connectionStringFactory.CreateAsync(
            context.Configuration,
            "THub governed publication editor",
            ApplicationIntent.ReadWrite,
            enlist: true,
            cancellationToken).ConfigureAwait(false)).ConnectionString;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            for (var index = 0; index < context.ChangeSet.Changes.Count; index++)
            {
                if (index > 0 && index % LeaseRenewalChangeInterval == 0 &&
                    !await claimStore.RenewAsync(
                        claim.ChangeSet.Id,
                        claim.LeaseOwner,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new PublicationApplyConflictException(
                        "lease_lost",
                        "The apply lease was lost before the source transaction committed.");
                }

                await ApplyChangeAsync(
                        connection,
                        transaction,
                        context.Version,
                        context.ChangeSet.Changes[index],
                        context.Configuration.CommandTimeoutSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!await claimStore.RenewAsync(
                    claim.ChangeSet.Id,
                    claim.LeaseOwner,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false))
            {
                throw new PublicationApplyConflictException(
                    "lease_lost",
                    "The apply lease was lost before the source transaction committed.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException) when (rollbackException is SqlException or InvalidOperationException)
            {
                // Preserve the original apply error. Disposal will close an unusable connection.
            }

            throw;
        }
    }

    private static async Task ApplyChangeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PublicationVersion version,
        PublicationChange change,
        int connectorCommandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var columns = version.Columns.ToDictionary(
            column => column.PublicAlias,
            StringComparer.OrdinalIgnoreCase);
        var key = ParseObject(change.KeyJson, PublicationChange.MaximumKeyJsonLength);
        var before = ParseObject(change.BeforeJson, PublicationChange.MaximumRowJsonLength);
        var after = ParseObject(change.AfterJson, PublicationChange.MaximumRowJsonLength);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = Math.Min(
            connectorCommandTimeoutSeconds,
            version.Settings.CommandTimeoutSeconds);
        command.CommandText = change.Operation switch
        {
            PublicationChangeOperation.Insert => BuildInsert(command, version, columns, after),
            PublicationChangeOperation.Update => BuildUpdate(command, version, columns, key, before, after),
            PublicationChangeOperation.Delete => BuildDelete(command, version, columns, key, before),
            _ => throw new InvalidOperationException("The staged change operation is unsupported."),
        };
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new PublicationApplyConflictException(
                "row_concurrency",
                "The source row changed or no longer exists.");
        }
    }

    internal static string BuildInsert(
        SqlCommand command,
        PublicationVersion version,
        IReadOnlyDictionary<string, PublicationColumn> columns,
        IReadOnlyDictionary<string, JsonElement> after)
    {
        if (after.Count == 0 || after.Any(value =>
                !columns.TryGetValue(value.Key, out var column) ||
                !PublicationColumnMutationPolicy.CanSupplyOnInsert(column)))
        {
            throw new InvalidOperationException("Insert values contain a non-insertable publication alias.");
        }

        var required = version.Columns
            .Where(column =>
                PublicationColumnMutationPolicy.CanSupplyOnInsert(column) &&
                !column.IsNullable)
            .Select(column => column.PublicAlias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!required.IsSubsetOf(after.Keys))
        {
            throw new InvalidOperationException("Insert values omit a required insertable column.");
        }

        var ordered = after
            .Select(value => (Column: columns[value.Key], Value: value.Value))
            .OrderBy(value => value.Column.Ordinal)
            .ToArray();
        var parameterNames = new List<string>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var parameterName = $"@value{index}";
            AddJsonParameter(command, parameterName, ordered[index].Value, ordered[index].Column);
            parameterNames.Add(parameterName);
        }

        return $"INSERT INTO {QualifiedName(version)} ({string.Join(", ", ordered.Select(value => SqlPublicationQueryPlanner.QuoteIdentifier(value.Column.SourceName)))}) VALUES ({string.Join(", ", parameterNames)});";
    }

    internal static string BuildUpdate(
        SqlCommand command,
        PublicationVersion version,
        IReadOnlyDictionary<string, PublicationColumn> columns,
        IReadOnlyDictionary<string, JsonElement> key,
        IReadOnlyDictionary<string, JsonElement> before,
        IReadOnlyDictionary<string, JsonElement> after)
    {
        if (after.Count == 0 || after.Any(value =>
                !columns.TryGetValue(value.Key, out var column) ||
                !PublicationColumnMutationPolicy.CanSetOnUpdate(column)))
        {
            throw new InvalidOperationException("Update values contain a non-mutable or key publication alias.");
        }
        if (version.ConcurrencyMode == PublicationConcurrencyMode.OriginalValues &&
            after.Keys.Any(alias => !before.ContainsKey(alias)))
        {
            throw new InvalidOperationException("Original values are missing for an updated column.");
        }

        var assignments = new List<string>();
        foreach (var value in after.OrderBy(value => columns[value.Key].Ordinal))
        {
            var column = columns[value.Key];
            var parameterName = $"@set{assignments.Count}";
            AddJsonParameter(command, parameterName, value.Value, column);
            assignments.Add($"{SqlPublicationQueryPlanner.QuoteIdentifier(column.SourceName)} = {parameterName}");
        }

        var predicates = BuildPredicates(command, version, columns, key, before);
        return $"UPDATE {QualifiedName(version)} SET {string.Join(", ", assignments)} WHERE {string.Join(" AND ", predicates)};";
    }

    private static string BuildDelete(
        SqlCommand command,
        PublicationVersion version,
        IReadOnlyDictionary<string, PublicationColumn> columns,
        IReadOnlyDictionary<string, JsonElement> key,
        IReadOnlyDictionary<string, JsonElement> before)
    {
        if (version.ConcurrencyMode == PublicationConcurrencyMode.OriginalValues &&
            version.Columns
                .Where(column => column.IsWritable)
                .Any(column => !before.ContainsKey(column.PublicAlias)))
        {
            throw new InvalidOperationException("Original values are incomplete for a delete operation.");
        }

        var predicates = BuildPredicates(command, version, columns, key, before);
        return $"DELETE FROM {QualifiedName(version)} WHERE {string.Join(" AND ", predicates)};";
    }

    private static IReadOnlyList<string> BuildPredicates(
        SqlCommand command,
        PublicationVersion version,
        IReadOnlyDictionary<string, PublicationColumn> columns,
        IReadOnlyDictionary<string, JsonElement> key,
        IReadOnlyDictionary<string, JsonElement> before)
    {
        var keyColumns = version.Columns
            .Where(column => column.IsKey)
            .OrderBy(column => column.KeyOrdinal)
            .ToArray();
        if (key.Count != keyColumns.Length || keyColumns.Any(column => !key.ContainsKey(column.PublicAlias)))
        {
            throw new InvalidOperationException("The staged key does not match the active publication key.");
        }

        var values = new List<(PublicationColumn Column, JsonElement Value)>();
        values.AddRange(keyColumns.Select(column => (column, key[column.PublicAlias])));
        if (version.ConcurrencyMode == PublicationConcurrencyMode.RowVersion)
        {
            var concurrencyColumn = version.Columns.Single(column => column.IsConcurrencyToken);
            if (!before.TryGetValue(concurrencyColumn.PublicAlias, out var concurrencyValue))
            {
                throw new InvalidOperationException("The staged row-version value is missing.");
            }

            values.Add((concurrencyColumn, concurrencyValue));
        }
        else
        {
            foreach (var value in before.OrderBy(value => columns.TryGetValue(value.Key, out var column)
                         ? column.Ordinal
                         : int.MaxValue))
            {
                if (!columns.TryGetValue(value.Key, out var column) || !column.IsReadable)
                {
                    throw new InvalidOperationException("Original values contain an unapproved publication alias.");
                }

                if (values.All(existing => !string.Equals(
                        existing.Column.PublicAlias,
                        column.PublicAlias,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    values.Add((column, value.Value));
                }
            }
        }

        var predicates = new List<string>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var identifier = SqlPublicationQueryPlanner.QuoteIdentifier(value.Column.SourceName);
            if (value.Value.ValueKind == JsonValueKind.Null)
            {
                if (!value.Column.IsNullable)
                {
                    throw new InvalidOperationException("A non-nullable predicate value cannot be null.");
                }

                predicates.Add($"{identifier} IS NULL");
                continue;
            }

            var parameterName = $"@where{index}";
            AddJsonParameter(command, parameterName, value.Value, value.Column);
            predicates.Add($"{identifier} = {parameterName}");
        }

        return predicates;
    }

    private static void AddJsonParameter(
        SqlCommand command,
        string parameterName,
        JsonElement value,
        PublicationColumn column)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!column.IsNullable)
            {
                throw new InvalidOperationException("A non-nullable publication value cannot be null.");
            }

            command.Parameters.Add(SqlPublicationValueConverter.CreateParameter(parameterName, null, column));
            return;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new InvalidOperationException("Publication values must be scalar JSON values."),
        };
        if (!SqlPublicationValueConverter.TryParse(text, column, out var parsed))
        {
            throw new InvalidOperationException("A publication value is incompatible with its immutable column type.");
        }

        command.Parameters.Add(SqlPublicationValueConverter.CreateParameter(parameterName, parsed, column));
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseObject(string? json, int maximumLength)
    {
        if (json is null)
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        if (json.Length > maximumLength)
        {
            throw new InvalidOperationException("A staged JSON value exceeds its bound.");
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("A staged value must be a JSON object.");
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!values.TryAdd(property.Name, property.Value.Clone()))
            {
                throw new InvalidOperationException("A staged JSON object contains duplicate aliases.");
            }
        }

        return values;
    }

    private async Task<PublicationChangeSetProcessResult> CompleteAsync(
        PublicationChangeSetClaim claim,
        PublicationChangeSetApplyOutcome outcome,
        string? detail,
        PublicationChangeSetProcessStatus completedStatus,
        CancellationToken cancellationToken)
    {
        var completed = await claimStore.CompleteAsync(
                claim.ChangeSet.Id,
                claim.LeaseOwner,
                outcome,
                detail,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        return completed
            ? new PublicationChangeSetProcessResult(completedStatus, claim.ChangeSet.Id, detail)
            : new PublicationChangeSetProcessResult(
                PublicationChangeSetProcessStatus.LeaseLost,
                claim.ChangeSet.Id,
                "The apply outcome could not be committed because the lease was lost.");
    }

    private static string QualifiedName(PublicationVersion version) =>
        $"{SqlPublicationQueryPlanner.QuoteIdentifier(version.SourceSchema)}.{SqlPublicationQueryPlanner.QuoteIdentifier(version.SourceObject)}";

    private static bool IsExpectedApplyFailure(Exception exception) => exception is SqlException
        or TimeoutException
        or InvalidOperationException
        or ArgumentException
        or JsonException
        or OverflowException
        or DatabaseCredentialUnavailableException
        or ConnectionConfigurationException;

    private sealed record PublicationApplyContext(
        PublicationVersion Version,
        DataConnection Connection,
        SqlServerConnectionConfiguration Configuration,
        PublicationChangeSet ChangeSet);

    private sealed class PublicationApplyConflictException(
        string code,
        string safeDetail) : Exception(safeDetail)
    {
        public string Code { get; } = code;

        public string SafeDetail { get; } = safeDetail;
    }
}
