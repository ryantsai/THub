using System.Data;
using System.Globalization;
using System.Text;
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

public sealed class SqlPublicationSourceDataReader(
    IDbContextFactory<THubDbContext> contextFactory,
    ConnectionConfigurationSerializer configurationSerializer,
    SqlServerConnectionStringFactory connectionStringFactory,
    IPublicationSourceSchemaInspector schemaInspector,
    ILogger<SqlPublicationSourceDataReader> logger) : IPublicationSourceDataReader
{
    public async Task<PublicationSourceReadResult<PublicationSourceRowPage>> ReadRowsAsync(
        PublicationVersion version,
        PublicationSourceReadQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var source = await LoadSourceAsync(version.ConnectionId, cancellationToken)
                .ConfigureAwait(false);
            if (source is null)
            {
                return UnavailableRows();
            }

            var inspection = await schemaInspector.InspectObjectAsync(
                    source.DataConnection,
                    version.SourceSchema,
                    version.SourceObject,
                    cancellationToken)
                .ConfigureAwait(false);
            if (inspection.Status != PublicationSourceInspectionStatus.Success ||
                inspection.Value is null ||
                !string.Equals(
                    inspection.Value.SchemaFingerprint,
                    version.SchemaFingerprint,
                    StringComparison.Ordinal))
            {
                return new PublicationSourceReadResult<PublicationSourceRowPage>(
                    inspection.Status == PublicationSourceInspectionStatus.Unavailable
                        ? PublicationSourceReadStatus.Unavailable
                        : PublicationSourceReadStatus.SchemaChanged,
                    null);
            }

            var planResult = SqlPublicationQueryPlanner.BuildRows(
                version,
                query,
                source.Configuration.MaximumBatchRows);
            if (planResult.Status == SqlPublicationPlanStatus.InvalidCursor)
            {
                return new PublicationSourceReadResult<PublicationSourceRowPage>(
                    PublicationSourceReadStatus.InvalidCursor,
                    null);
            }

            if (planResult.Plan is null)
            {
                return UnavailableRows();
            }

            await using var connection = new SqlConnection(source.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var metadata = await LoadObjectMetadataAsync(
                    connection,
                    version.SourceSchema,
                    version.SourceObject,
                    source.CommandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!MatchesVersionMetadata(version, metadata))
            {
                return new PublicationSourceReadResult<PublicationSourceRowPage>(
                    PublicationSourceReadStatus.SchemaChanged,
                    null);
            }

            return await ExecuteRowsAsync(
                    connection,
                    version,
                    planResult.Plan,
                    source.CommandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedSourceFailure(exception))
        {
            logger.LogWarning(
                exception,
                "Publication source read failed for version {PublicationVersionId} and connection {ConnectionId}.",
                version.Id,
                version.ConnectionId);
            return UnavailableRows();
        }
    }

    public async Task<PublicationSourceReadResult<PublicationSourceLookupPage>> ReadForeignKeyLookupAsync(
        PublicationVersion version,
        PublicationColumn column,
        PublicationForeignKeySourceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var source = await LoadSourceAsync(version.ConnectionId, cancellationToken)
                .ConfigureAwait(false);
            if (source is null || column.ForeignKey is null)
            {
                return UnavailableLookup();
            }

            var inspection = await schemaInspector.InspectObjectAsync(
                    source.DataConnection,
                    version.SourceSchema,
                    version.SourceObject,
                    cancellationToken)
                .ConfigureAwait(false);
            if (inspection.Status != PublicationSourceInspectionStatus.Success ||
                inspection.Value is null ||
                !string.Equals(
                    inspection.Value.SchemaFingerprint,
                    version.SchemaFingerprint,
                    StringComparison.Ordinal))
            {
                return new PublicationSourceReadResult<PublicationSourceLookupPage>(
                    inspection.Status == PublicationSourceInspectionStatus.Unavailable
                        ? PublicationSourceReadStatus.Unavailable
                        : PublicationSourceReadStatus.SchemaChanged,
                    null);
            }

            var planResult = SqlPublicationLookupPlanner.Build(version, column, query);
            if (planResult.Status == SqlPublicationPlanStatus.InvalidCursor)
            {
                return new PublicationSourceReadResult<PublicationSourceLookupPage>(
                    PublicationSourceReadStatus.InvalidCursor,
                    null);
            }

            if (planResult.Plan is null)
            {
                return UnavailableLookup();
            }

            await using var connection = new SqlConnection(source.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var foreignKey = column.ForeignKey;
            var metadata = await LoadObjectMetadataAsync(
                    connection,
                    foreignKey.ReferencedSchema,
                    foreignKey.ReferencedObject,
                    source.CommandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!MatchesLookupMetadata(planResult.Plan, foreignKey, metadata))
            {
                return new PublicationSourceReadResult<PublicationSourceLookupPage>(
                    PublicationSourceReadStatus.SchemaChanged,
                    null);
            }

            return await ExecuteLookupAsync(
                    connection,
                    version,
                    planResult.Plan,
                    source.CommandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedSourceFailure(exception))
        {
            logger.LogWarning(
                exception,
                "Publication lookup failed for version {PublicationVersionId}, column {PublicationColumnId}, and connection {ConnectionId}.",
                version.Id,
                column.Id,
                version.ConnectionId);
            return UnavailableLookup();
        }
    }

    public async Task<PublicationSourceReadResult<PublicationSourceForeignKeyResolution>> ResolveForeignKeysAsync(
        PublicationVersion version,
        IReadOnlyList<PublicationForeignKeyTuple> tuples,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(tuples);
        if (tuples.Count == 0)
        {
            return new PublicationSourceReadResult<PublicationSourceForeignKeyResolution>(
                PublicationSourceReadStatus.Success,
                new PublicationSourceForeignKeyResolution([]));
        }

        try
        {
            var source = await LoadSourceAsync(version.ConnectionId, cancellationToken)
                .ConfigureAwait(false);
            if (source is null)
            {
                return UnavailableResolution();
            }

            var inspection = await schemaInspector.InspectObjectAsync(
                    source.DataConnection,
                    version.SourceSchema,
                    version.SourceObject,
                    cancellationToken)
                .ConfigureAwait(false);
            if (inspection.Status != PublicationSourceInspectionStatus.Success ||
                inspection.Value is null ||
                !string.Equals(
                    inspection.Value.SchemaFingerprint,
                    version.SchemaFingerprint,
                    StringComparison.Ordinal))
            {
                return new PublicationSourceReadResult<PublicationSourceForeignKeyResolution>(
                    inspection.Status == PublicationSourceInspectionStatus.Unavailable
                        ? PublicationSourceReadStatus.Unavailable
                        : PublicationSourceReadStatus.SchemaChanged,
                    null);
            }

            var plan = SqlPublicationForeignKeyResolutionPlanner.Build(version, tuples);
            if (plan.Status != SqlPublicationPlanStatus.Success)
            {
                return UnavailableResolution();
            }

            await using var connection = new SqlConnection(source.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var labels = new List<PublicationForeignKeyLabelDto>(tuples.Count);
            var resolvedIds = new HashSet<int>();
            long responseBytes = 2;
            foreach (var group in plan.Groups)
            {
                var metadata = await LoadObjectMetadataAsync(
                        connection,
                        group.ForeignKey.ReferencedSchema,
                        group.ForeignKey.ReferencedObject,
                        source.CommandTimeoutSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!MatchesResolutionMetadata(group, metadata))
                {
                    return new PublicationSourceReadResult<PublicationSourceForeignKeyResolution>(
                        PublicationSourceReadStatus.SchemaChanged,
                        null);
                }

                foreach (var batch in group.Batches)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = batch.CommandText;
                    command.CommandType = CommandType.Text;
                    command.CommandTimeout = Math.Min(
                        source.CommandTimeoutSeconds,
                        version.Settings.CommandTimeoutSeconds);
                    foreach (var parameter in batch.Parameters)
                    {
                        command.Parameters.Add(parameter);
                    }

                    await using var reader = await command.ExecuteReaderAsync(
                        CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                        cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var requestId = reader.GetInt32(0);
                        if (!resolvedIds.Add(requestId))
                        {
                            return UnavailableResolution();
                        }

                        var display = await ReadValueAsync(reader, 1, cancellationToken)
                            .ConfigureAwait(false);
                        var displayText = Convert.ToString(display, CultureInfo.InvariantCulture) ?? string.Empty;
                        responseBytes = checked(responseBytes + Encoding.UTF8.GetByteCount(displayText) + 12L);
                        if (responseBytes > version.Settings.MaximumResponseBytes)
                        {
                            return UnavailableResolution();
                        }

                        labels.Add(new PublicationForeignKeyLabelDto(requestId, displayText));
                    }
                }
            }

            return new PublicationSourceReadResult<PublicationSourceForeignKeyResolution>(
                PublicationSourceReadStatus.Success,
                new PublicationSourceForeignKeyResolution(labels));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedSourceFailure(exception))
        {
            logger.LogWarning(
                exception,
                "Publication foreign-key resolution failed for version {PublicationVersionId} and connection {ConnectionId}.",
                version.Id,
                version.ConnectionId);
            return UnavailableResolution();
        }
    }

    private async Task<PublicationSqlSource?> LoadSourceAsync(
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var dataConnection = await db.Connections
            .AsNoTracking()
            .SingleOrDefaultAsync(connection => connection.Id == connectionId, cancellationToken)
            .ConfigureAwait(false);
        if (dataConnection is null ||
            !dataConnection.IsEnabled ||
            dataConnection.Kind != ConnectionKind.SqlServer ||
            configurationSerializer.Deserialize(dataConnection) is not SqlServerConnectionConfiguration configuration)
        {
            return null;
        }

        var builder = await connectionStringFactory.CreateAsync(
            configuration,
            "THub governed publication reader",
            ApplicationIntent.ReadOnly,
            enlist: false,
            cancellationToken).ConfigureAwait(false);
        return new PublicationSqlSource(
            builder.ConnectionString,
            dataConnection,
            configuration,
            configuration.CommandTimeoutSeconds);
    }

    private static async Task<PublicationSourceReadResult<PublicationSourceRowPage>> ExecuteRowsAsync(
        SqlConnection connection,
        PublicationVersion version,
        SqlPublicationReadPlan plan,
        int configurationCommandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = plan.CommandText;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = Math.Min(
            configurationCommandTimeoutSeconds,
            version.Settings.CommandTimeoutSeconds);
        foreach (var parameter in plan.Parameters)
        {
            command.Parameters.Add(parameter);
        }

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken).ConfigureAwait(false);
        var rows = new List<IReadOnlyDictionary<string, object?>>(plan.Take);
        long responseBytes = 2;
        var hasMore = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == plan.Take)
            {
                hasMore = true;
                break;
            }

            if (reader.GetBoolean(0))
            {
                return UnavailableRows();
            }

            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < plan.OutputColumns.Count; index++)
            {
                var value = await ReadValueAsync(reader, index + 1, cancellationToken)
                    .ConfigureAwait(false);
                row.Add(plan.OutputColumns[index].PublicAlias, value);
            }

            responseBytes = checked(responseBytes + EstimateRowBytes(row));
            if (responseBytes > version.Settings.MaximumResponseBytes)
            {
                return UnavailableRows();
            }

            rows.Add(row);
        }

        var nextCursor = hasMore && rows.Count > 0
            ? SqlPublicationCursorCodec.Encode(
                version,
                plan.Filters,
                plan.Sorts,
                rows[^1])
            : null;
        return new PublicationSourceReadResult<PublicationSourceRowPage>(
            PublicationSourceReadStatus.Success,
            new PublicationSourceRowPage(rows, nextCursor));
    }

    private static async Task<PublicationSourceReadResult<PublicationSourceLookupPage>> ExecuteLookupAsync(
        SqlConnection connection,
        PublicationVersion version,
        SqlPublicationLookupPlan plan,
        int configurationCommandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = plan.CommandText;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = Math.Min(
            configurationCommandTimeoutSeconds,
            version.Settings.CommandTimeoutSeconds);
        foreach (var parameter in plan.Parameters)
        {
            command.Parameters.Add(parameter);
        }

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken).ConfigureAwait(false);
        var items = new List<PublicationLookupItemDto>(plan.Take);
        Dictionary<string, object?>? lastCursorRow = null;
        long responseBytes = 2;
        var hasMore = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (items.Count == plan.Take)
            {
                hasMore = true;
                break;
            }

            if (reader.GetBoolean(0))
            {
                return UnavailableLookup();
            }

            var display = await ReadValueAsync(reader, 1, cancellationToken).ConfigureAwait(false);
            var displayText = Convert.ToString(display, CultureInfo.InvariantCulture) ?? string.Empty;
            var keys = new Dictionary<string, object?>(StringComparer.Ordinal);
            lastCursorRow = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [plan.DisplayAlias] = displayText,
            };
            for (var index = 0; index < plan.KeyColumns.Count; index++)
            {
                var value = await ReadValueAsync(reader, index + 2, cancellationToken)
                    .ConfigureAwait(false);
                var alias = plan.KeyColumns[index].LocalColumn.PublicAlias;
                keys.Add(alias, value);
                lastCursorRow.Add(alias, value);
            }

            responseBytes = checked(
                responseBytes + EstimateRowBytes(keys) + Encoding.UTF8.GetByteCount(displayText) + 8);
            if (responseBytes > version.Settings.MaximumResponseBytes)
            {
                return UnavailableLookup();
            }

            items.Add(new PublicationLookupItemDto(keys, displayText));
        }

        var nextCursor = hasMore && lastCursorRow is not null
            ? SqlPublicationCursorCodec.Encode(
                version,
                plan.CursorFilters,
                plan.Sorts,
                lastCursorRow)
            : null;
        return new PublicationSourceReadResult<PublicationSourceLookupPage>(
            PublicationSourceReadStatus.Success,
            new PublicationSourceLookupPage(items, nextCursor));
    }

    private static async Task<SqlObjectMetadata?> LoadObjectMetadataAsync(
        SqlConnection connection,
        string schema,
        string objectName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        const string commandText = """
            SELECT o.[type], c.[name], TYPE_NAME(c.[user_type_id]), c.[is_nullable]
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s ON s.[schema_id] = o.[schema_id]
            INNER JOIN sys.columns AS c ON c.[object_id] = o.[object_id]
            WHERE s.[name] = @schema
              AND o.[name] = @object
              AND o.[type] IN ('U', 'V')
            ORDER BY c.[column_id];
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = schema });
        command.Parameters.Add(new SqlParameter("@object", SqlDbType.NVarChar, 128) { Value = objectName });
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken).ConfigureAwait(false);
        string? objectType = null;
        var columns = new Dictionary<string, SqlColumnMetadata>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            objectType ??= reader.GetString(0).Trim();
            var name = reader.GetString(1);
            columns.Add(name, new SqlColumnMetadata(name, reader.GetString(2), reader.GetBoolean(3)));
        }

        return objectType is null ? null : new SqlObjectMetadata(objectType, columns);
    }

    private static bool MatchesVersionMetadata(
        PublicationVersion version,
        SqlObjectMetadata? metadata)
    {
        var expectedObjectType = version.SourceObjectKind == PublicationSourceObjectKind.Table ? "U" : "V";
        if (metadata is null || !string.Equals(metadata.ObjectType, expectedObjectType, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var column in version.Columns)
        {
            if (!metadata.Columns.TryGetValue(column.SourceName, out var actual) ||
                actual.IsNullable != column.IsNullable ||
                !SourceTypesMatch(column.SourceTypeName, actual.TypeName))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesLookupMetadata(
        SqlPublicationLookupPlan plan,
        PublicationForeignKey foreignKey,
        SqlObjectMetadata? metadata)
    {
        if (metadata is null || metadata.ObjectType != "U" ||
            !metadata.Columns.ContainsKey(foreignKey.DisplayColumn) ||
            foreignKey.SearchColumns.Any(searchColumn => !metadata.Columns.ContainsKey(searchColumn)))
        {
            return false;
        }

        return plan.KeyColumns.All(key =>
            metadata.Columns.TryGetValue(key.ReferencedColumn, out var actual) &&
            SourceTypesMatch(key.LocalColumn.SourceTypeName, actual.TypeName));
    }

    private static bool MatchesResolutionMetadata(
        SqlPublicationForeignKeyResolutionGroupPlan plan,
        SqlObjectMetadata? metadata)
    {
        if (metadata is null || metadata.ObjectType != "U" ||
            !metadata.Columns.ContainsKey(plan.ForeignKey.DisplayColumn))
        {
            return false;
        }

        return plan.KeyColumns.All(key =>
            metadata.Columns.TryGetValue(key.ReferencedColumn, out var actual) &&
            SourceTypesMatch(key.LocalColumn.SourceTypeName, actual.TypeName));
    }

    private static bool SourceTypesMatch(string declared, string actual)
    {
        var normalizedDeclared = NormalizeTypeName(declared);
        var normalizedActual = NormalizeTypeName(actual);
        return string.Equals(normalizedDeclared, normalizedActual, StringComparison.OrdinalIgnoreCase) ||
            (normalizedDeclared is "rowversion" or "timestamp" &&
             normalizedActual is "rowversion" or "timestamp");
    }

    private static string NormalizeTypeName(string value)
    {
        var normalized = value.Trim().Trim('[', ']');
        var parenthesis = normalized.IndexOf('(');
        if (parenthesis >= 0)
        {
            normalized = normalized[..parenthesis];
        }

        var dot = normalized.LastIndexOf('.');
        return (dot >= 0 ? normalized[(dot + 1)..] : normalized).Trim('[', ']', ' ');
    }

    private static async Task<object?> ReadValueAsync(
        SqlDataReader reader,
        int ordinal,
        CancellationToken cancellationToken) =>
        await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetValue(ordinal);

    private static long EstimateRowBytes(IReadOnlyDictionary<string, object?> row)
    {
        long bytes = 2;
        foreach (var value in row)
        {
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(value.Key) + 4 + EstimateValueBytes(value.Value));
        }

        return bytes;
    }

    private static long EstimateValueBytes(object? value) => value switch
    {
        null => 4,
        string text => checked(Encoding.UTF8.GetByteCount(text) + 2L),
        byte[] binary => checked(((binary.LongLength + 2) / 3) * 4 + 2),
        bool => 5,
        Guid => 38,
        DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan => 66,
        _ => 64,
    };

    private static bool IsExpectedSourceFailure(Exception exception) => exception is SqlException
        or TimeoutException
        or InvalidOperationException
        or ArgumentException
        or ConnectionConfigurationException
        or ConnectionCredentialUnavailableException
        or OverflowException;

    private static PublicationSourceReadResult<PublicationSourceRowPage> UnavailableRows() =>
        new(PublicationSourceReadStatus.Unavailable, null);

    private static PublicationSourceReadResult<PublicationSourceLookupPage> UnavailableLookup() =>
        new(PublicationSourceReadStatus.Unavailable, null);

    private static PublicationSourceReadResult<PublicationSourceForeignKeyResolution> UnavailableResolution() =>
        new(PublicationSourceReadStatus.Unavailable, null);

    private sealed record PublicationSqlSource(
        string ConnectionString,
        DataConnection DataConnection,
        SqlServerConnectionConfiguration Configuration,
        int CommandTimeoutSeconds);

    private sealed record SqlObjectMetadata(
        string ObjectType,
        IReadOnlyDictionary<string, SqlColumnMetadata> Columns);

    private sealed record SqlColumnMetadata(
        string Name,
        string TypeName,
        bool IsNullable);
}
