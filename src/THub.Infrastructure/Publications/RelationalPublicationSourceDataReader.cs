using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using THub.Application.Connections;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;
using THub.Infrastructure.Connections;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Publications;

public sealed class RelationalPublicationSourceDataReader(
    IDbContextFactory<THubDbContext> contextFactory,
    ConnectionConfigurationSerializer serializer,
    RelationalConnectionFactory connectionFactory,
    IPublicationSourceSchemaInspector schemaInspector,
    ILogger<RelationalPublicationSourceDataReader> logger)
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
                return new(
                    inspection.Status == PublicationSourceInspectionStatus.Unavailable
                        ? PublicationSourceReadStatus.Unavailable
                        : PublicationSourceReadStatus.SchemaChanged,
                    null);
            }

            var plan = RelationalPublicationQueryPlanner.BuildRows(
                source.Configuration.Kind,
                version,
                query,
                source.Configuration.MaximumBatchRows);
            if (plan.Status == SqlPublicationPlanStatus.InvalidCursor)
            {
                return new(PublicationSourceReadStatus.InvalidCursor, null);
            }

            if (plan.Plan is null)
            {
                return UnavailableRows();
            }

            await using var connection = await connectionFactory.CreateAsync(
                    source.Configuration,
                    cancellationToken)
                .ConfigureAwait(false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ExecuteRowsAsync(
                    connection,
                    source.Configuration,
                    version,
                    plan.Plan,
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
                "Relational publication source read failed for version {PublicationVersionId} and connection {ConnectionId}.",
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
        try
        {
            var source = await LoadAndValidateAsync(version, cancellationToken).ConfigureAwait(false);
            if (source.Status != PublicationSourceReadStatus.Success || source.Source is null)
            {
                return new(source.Status, null);
            }

            var plan = RelationalPublicationLookupPlanner.Build(
                source.Source.Configuration.Kind,
                version,
                column,
                query);
            if (plan.Status == SqlPublicationPlanStatus.InvalidCursor)
            {
                return new(PublicationSourceReadStatus.InvalidCursor, null);
            }

            if (plan.Plan is null)
            {
                return UnavailableLookup();
            }

            await using var connection = await connectionFactory.CreateAsync(
                    source.Source.Configuration,
                    cancellationToken)
                .ConfigureAwait(false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            ConfigureCommand(
                command,
                source.Source.Configuration,
                version.Settings.CommandTimeoutSeconds);
            command.CommandText = plan.Plan.CommandText;
            AddParameters(command, plan.Plan.Parameters);
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken).ConfigureAwait(false);
            var items = new List<PublicationLookupItemDto>(plan.Plan.Take);
            var cursorRows = new List<IReadOnlyDictionary<string, object?>>(plan.Plan.Take);
            long responseBytes = 2;
            var hasMore = false;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (items.Count == plan.Plan.Take)
                {
                    hasMore = true;
                    break;
                }

                var display = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
                    ? string.Empty
                    : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
                var keys = new Dictionary<string, object?>(StringComparer.Ordinal);
                var cursorRow = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [plan.Plan.DisplayAlias] = display
                };
                for (var index = 0; index < plan.Plan.KeyColumns.Count; index++)
                {
                    var key = plan.Plan.KeyColumns[index];
                    var value = await reader.IsDBNullAsync(index + 1, cancellationToken).ConfigureAwait(false)
                        ? null
                        : NormalizeValue(reader.GetValue(index + 1), key.LocalColumn.DataType);
                    keys.Add(key.LocalColumn.PublicAlias, value);
                    cursorRow.Add(key.LocalColumn.PublicAlias, value);
                }

                responseBytes = checked(
                    responseBytes + EstimateRowBytes(keys) + Encoding.UTF8.GetByteCount(display) + 8);
                if (responseBytes > version.Settings.MaximumResponseBytes)
                {
                    return UnavailableLookup();
                }

                items.Add(new PublicationLookupItemDto(keys, display));
                cursorRows.Add(cursorRow);
            }

            var nextCursor = hasMore && cursorRows.Count > 0
                ? SqlPublicationCursorCodec.Encode(
                    version,
                    plan.Plan.CursorFilters,
                    plan.Plan.Sorts,
                    cursorRows[^1])
                : null;
            return new(
                PublicationSourceReadStatus.Success,
                new PublicationSourceLookupPage(items, nextCursor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedSourceFailure(exception))
        {
            logger.LogWarning(
                exception,
                "Relational publication lookup failed for version {PublicationVersionId}.",
                version.Id);
            return UnavailableLookup();
        }
    }

    public async Task<PublicationSourceReadResult<PublicationSourceForeignKeyResolution>> ResolveForeignKeysAsync(
        PublicationVersion version,
        IReadOnlyList<PublicationForeignKeyTuple> tuples,
        CancellationToken cancellationToken)
    {
        if (tuples.Count == 0)
        {
            return new(
                PublicationSourceReadStatus.Success,
                new PublicationSourceForeignKeyResolution([]));
        }

        try
        {
            var source = await LoadAndValidateAsync(version, cancellationToken).ConfigureAwait(false);
            if (source.Status != PublicationSourceReadStatus.Success || source.Source is null)
            {
                return new(source.Status, null);
            }

            await using var connection = await connectionFactory.CreateAsync(
                    source.Source.Configuration,
                    cancellationToken)
                .ConfigureAwait(false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var labels = new List<PublicationForeignKeyLabelDto>(tuples.Count);
            long responseBytes = 2;
            foreach (var group in tuples.GroupBy(
                         tuple => tuple.ConstraintName,
                         StringComparer.OrdinalIgnoreCase))
            {
                var keyColumns = version.Columns
                    .Where(column => string.Equals(
                        column.ForeignKey?.ConstraintName,
                        group.Key,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(column => column.ForeignKey!.Ordinal)
                    .ToArray();
                if (keyColumns.Length == 0 ||
                    keyColumns.Any(column => column.ForeignKey is null))
                {
                    return UnavailableResolution();
                }

                var foreignKey = keyColumns[0].ForeignKey!;
                foreach (var batch in group.Chunk(100))
                {
                    var resolved = await ResolveForeignKeyBatchAsync(
                            connection,
                            source.Source.Configuration,
                            version,
                            foreignKey,
                            keyColumns,
                            batch,
                            cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var label in resolved)
                    {
                        responseBytes = checked(
                            responseBytes + Encoding.UTF8.GetByteCount(label.DisplayText) + 12);
                        if (responseBytes > version.Settings.MaximumResponseBytes)
                        {
                            return UnavailableResolution();
                        }

                        labels.Add(label);
                    }
                }
            }

            return new(
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
                "Relational publication foreign-key validation failed for version {PublicationVersionId}.",
                version.Id);
            return UnavailableResolution();
        }
    }

    private async Task<ValidatedRelationalSource> LoadAndValidateAsync(
        PublicationVersion version,
        CancellationToken cancellationToken)
    {
        var source = await LoadSourceAsync(version.ConnectionId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return new(PublicationSourceReadStatus.Unavailable, null);
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
            return new(
                inspection.Status == PublicationSourceInspectionStatus.Unavailable
                    ? PublicationSourceReadStatus.Unavailable
                    : PublicationSourceReadStatus.SchemaChanged,
                null);
        }

        return new(PublicationSourceReadStatus.Success, source);
    }

    private static async Task<IReadOnlyList<PublicationForeignKeyLabelDto>> ResolveForeignKeyBatchAsync(
        DbConnection connection,
        RelationalDatabaseConnectionConfiguration configuration,
        PublicationVersion version,
        PublicationForeignKey foreignKey,
        IReadOnlyList<PublicationColumn> keyColumns,
        IReadOnlyList<PublicationForeignKeyTuple> tuples,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        ConfigureCommand(command, configuration, version.Settings.CommandTimeoutSeconds);
        var sql = new StringBuilder("SELECT ")
            .Append(RelationalPublicationLookupPlanner.DisplayExpression(
                configuration.Kind,
                foreignKey.DisplayColumn))
            .Append(" AS ")
            .Append(RelationalPublicationLookupPlanner.Quote(configuration.Kind, "__thub_display"));
        foreach (var column in keyColumns)
        {
            sql.Append(", ")
                .Append(RelationalPublicationLookupPlanner.Quote(
                    configuration.Kind,
                    column.ForeignKey!.ReferencedColumn))
                .Append(" AS ")
                .Append(RelationalPublicationLookupPlanner.Quote(
                    configuration.Kind,
                    column.PublicAlias));
        }

        sql.Append(" FROM ")
            .Append(THub.Infrastructure.Execution.RelationalExecutionSupport.QualifiedName(
                configuration.Kind,
                foreignKey.ReferencedSchema,
                foreignKey.ReferencedObject))
            .Append(" WHERE ");
        var tuplesBySignature = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var branches = new List<string>(tuples.Count);
        for (var tupleIndex = 0; tupleIndex < tuples.Count; tupleIndex++)
        {
            var tuple = tuples[tupleIndex];
            var predicates = new List<string>(keyColumns.Count);
            var signatureValues = new List<object?>(keyColumns.Count);
            for (var keyIndex = 0; keyIndex < keyColumns.Count; keyIndex++)
            {
                var column = keyColumns[keyIndex];
                if (!tuple.KeyValues.TryGetValue(column.PublicAlias, out var supplied) ||
                    supplied is null or DBNull)
                {
                    throw new InvalidOperationException(
                        "A foreign-key tuple is incomplete.");
                }

                var value = NormalizeParameterValue(supplied, column);
                var name = $"fk_{tupleIndex}_{keyIndex}";
                AddParameter(command, name, value, column);
                predicates.Add(
                    $"{RelationalPublicationLookupPlanner.Quote(configuration.Kind, column.ForeignKey!.ReferencedColumn)} = " +
                    RelationalPublicationLookupPlanner.Marker(configuration.Kind, name));
                signatureValues.Add(value);
            }

            var signature = CreateKeySignature(keyColumns, signatureValues);
            if (!tuplesBySignature.TryGetValue(signature, out var requestIds))
            {
                requestIds = [];
                tuplesBySignature.Add(signature, requestIds);
            }

            requestIds.Add(tuple.RequestId);
            branches.Add($"({string.Join(" AND ", predicates)})");
        }

        sql.AppendJoin(" OR ", branches);
        command.CommandText = sql.ToString();
        var labels = new List<PublicationForeignKeyLabelDto>(tuples.Count);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var display = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
                ? string.Empty
                : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
            var values = new List<object?>(keyColumns.Count);
            for (var index = 0; index < keyColumns.Count; index++)
            {
                values.Add(await reader.IsDBNullAsync(index + 1, cancellationToken).ConfigureAwait(false)
                    ? null
                    : NormalizeValue(reader.GetValue(index + 1), keyColumns[index].DataType));
            }

            if (tuplesBySignature.TryGetValue(
                    CreateKeySignature(keyColumns, values),
                    out var requestIds))
            {
                labels.AddRange(requestIds.Select(
                    requestId => new PublicationForeignKeyLabelDto(requestId, display)));
            }
        }

        return labels;
    }

    private async Task<RelationalPublicationSource?> LoadSourceAsync(
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var connection = await db.Connections
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == connectionId, cancellationToken)
            .ConfigureAwait(false);
        if (connection is null ||
            !connection.IsEnabled ||
            connection.Kind is not (
                ConnectionKind.MySql or
                ConnectionKind.PostgreSql or
                ConnectionKind.Oracle) ||
            serializer.Deserialize(connection) is not RelationalDatabaseConnectionConfiguration configuration)
        {
            return null;
        }

        return new RelationalPublicationSource(connection, configuration);
    }

    private static async Task<PublicationSourceReadResult<PublicationSourceRowPage>> ExecuteRowsAsync(
        DbConnection connection,
        RelationalDatabaseConnectionConfiguration configuration,
        PublicationVersion version,
        RelationalPublicationReadPlan plan,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = plan.CommandText;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = Math.Min(
            configuration.CommandTimeoutSeconds,
            version.Settings.CommandTimeoutSeconds);
        if (command is OracleCommand oracle)
        {
            oracle.BindByName = true;
        }

        foreach (var specification in plan.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = specification.Name;
            parameter.DbType = MapDbType(specification.Column.DataType);
            parameter.Value = specification.Value ?? DBNull.Value;
            parameter.IsNullable = specification.Column.IsNullable;
            if (specification.Column.DataType is PublicationDataType.String or PublicationDataType.Binary &&
                specification.Column.MaximumLength is int maximumLength)
            {
                parameter.Size = maximumLength;
            }
            else if (specification.Column.DataType == PublicationDataType.Decimal)
            {
                parameter.Precision = specification.Column.NumericPrecision ?? 38;
                parameter.Scale = specification.Column.NumericScale ?? 0;
            }

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

            if (Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) != 0)
            {
                return UnavailableRows();
            }

            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < plan.OutputColumns.Count; index++)
            {
                var column = plan.OutputColumns[index];
                var value = await reader.IsDBNullAsync(index + 1, cancellationToken).ConfigureAwait(false)
                    ? null
                    : NormalizeValue(reader.GetValue(index + 1), column.DataType);
                row.Add(column.PublicAlias, value);
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
        return new(
            PublicationSourceReadStatus.Success,
            new PublicationSourceRowPage(rows, nextCursor));
    }

    private static object NormalizeValue(object value, PublicationDataType dataType)
    {
        value = value switch
        {
            OracleString oracle => oracle.Value,
            OracleDecimal oracle => oracle.Value,
            OracleDate oracle => oracle.Value,
            OracleTimeStamp oracle => oracle.Value,
            OracleTimeStampTZ oracle => oracle.Value,
            OracleBinary oracle => oracle.Value,
            OracleBlob oracle => oracle.Value,
            OracleClob oracle => oracle.Value,
            _ => value
        };
        return dataType switch
        {
            PublicationDataType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            PublicationDataType.Byte => Convert.ToByte(value, CultureInfo.InvariantCulture),
            PublicationDataType.Int16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            PublicationDataType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            PublicationDataType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            PublicationDataType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            PublicationDataType.Single => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            PublicationDataType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            PublicationDataType.Guid when value is Guid guid => guid,
            PublicationDataType.Guid => Guid.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!),
            PublicationDataType.String => Convert.ToString(value, CultureInfo.InvariantCulture)
                ?? string.Empty,
            _ => value
        };
    }

    private static object NormalizeParameterValue(object value, PublicationColumn column)
    {
        if (value is string text && column.DataType != PublicationDataType.String)
        {
            if (!SqlPublicationValueConverter.TryParse(text, column, out var parsed) ||
                parsed is null)
            {
                throw new InvalidOperationException(
                    "A foreign-key value is incompatible with the active publication type.");
            }

            return parsed;
        }

        return value;
    }

    private static string CreateKeySignature(
        IReadOnlyList<PublicationColumn> columns,
        IReadOnlyList<object?> values)
    {
        var parts = new string[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            parts[index] = values[index] is null
                ? "<NULL>"
                : SqlPublicationValueConverter.FormatCursorValue(
                    values[index]!,
                    columns[index].DataType);
        }

        return string.Join('\u001f', parts);
    }

    private static void ConfigureCommand(
        DbCommand command,
        RelationalDatabaseConnectionConfiguration configuration,
        int publicationTimeout)
    {
        command.CommandType = CommandType.Text;
        command.CommandTimeout = Math.Min(
            configuration.CommandTimeoutSeconds,
            publicationTimeout);
        if (command is OracleCommand oracle)
        {
            oracle.BindByName = true;
        }
    }

    private static void AddParameters(
        DbCommand command,
        IReadOnlyList<RelationalPublicationParameter> specifications)
    {
        foreach (var specification in specifications)
        {
            AddParameter(
                command,
                specification.Name,
                specification.Value,
                specification.Column);
        }
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value,
        PublicationColumn column)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = MapDbType(column.DataType);
        parameter.Value = value ?? DBNull.Value;
        parameter.IsNullable = column.IsNullable;
        if (column.DataType is PublicationDataType.String or PublicationDataType.Binary &&
            column.MaximumLength is int maximumLength)
        {
            parameter.Size = maximumLength;
        }
        else if (column.DataType == PublicationDataType.Decimal)
        {
            parameter.Precision = column.NumericPrecision ?? 38;
            parameter.Scale = column.NumericScale ?? 0;
        }

        command.Parameters.Add(parameter);
    }

    private static DbType MapDbType(PublicationDataType type) => type switch
    {
        PublicationDataType.Boolean => DbType.Boolean,
        PublicationDataType.Byte => DbType.Byte,
        PublicationDataType.Int16 => DbType.Int16,
        PublicationDataType.Int32 => DbType.Int32,
        PublicationDataType.Int64 => DbType.Int64,
        PublicationDataType.Decimal => DbType.Decimal,
        PublicationDataType.Single => DbType.Single,
        PublicationDataType.Double => DbType.Double,
        PublicationDataType.Date => DbType.Date,
        PublicationDataType.DateTime => DbType.DateTime,
        PublicationDataType.DateTimeOffset => DbType.DateTimeOffset,
        PublicationDataType.Time => DbType.Time,
        PublicationDataType.Guid => DbType.Guid,
        PublicationDataType.String => DbType.String,
        PublicationDataType.Binary => DbType.Binary,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

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
        _ => 64
    };

    private static bool IsExpectedSourceFailure(Exception exception) => exception is
        DbException
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

    private sealed record RelationalPublicationSource(
        DataConnection DataConnection,
        RelationalDatabaseConnectionConfiguration Configuration);

    private sealed record ValidatedRelationalSource(
        PublicationSourceReadStatus Status,
        RelationalPublicationSource? Source);
}
