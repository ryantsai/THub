using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using THub.Application.Connections;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;

namespace THub.Infrastructure.Publications;

public sealed class SqlPublicationSourceSchemaInspector(
    ConnectionConfigurationSerializer configurationSerializer,
    ILogger<SqlPublicationSourceSchemaInspector> logger) : IPublicationSourceSchemaInspector
{
    private const int MaximumColumns = 1_024;
    private const int MaximumForeignKeyMappings = 128;

    public async Task<PublicationSourceInspectionResult<PublicationSourceObjectPageDto>> ListObjectsAsync(
        DataConnection connection,
        string? search,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (take is < 1 or > 200 || search?.Length > 256)
        {
            return new(PublicationSourceInspectionStatus.BoundsExceeded, null);
        }

        try
        {
            var configuration = RequireSqlConfiguration(connection);
            await using var sqlConnection = new SqlConnection(
                SqlPublicationSourceDataReader.BuildConnectionString(configuration).ConnectionString);
            await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            const string commandText = """
                SELECT TOP (@take)
                    s.[name],
                    o.[name],
                    o.[type],
                    COUNT_BIG(c.[column_id])
                FROM sys.objects AS o
                INNER JOIN sys.schemas AS s ON s.[schema_id] = o.[schema_id]
                INNER JOIN sys.columns AS c ON c.[object_id] = o.[object_id]
                WHERE o.[type] IN ('U', 'V')
                  AND o.[is_ms_shipped] = 0
                  AND
                  (
                      @search IS NULL
                      OR s.[name] LIKE @search ESCAPE N'~'
                      OR o.[name] LIKE @search ESCAPE N'~'
                  )
                GROUP BY s.[name], o.[name], o.[type]
                ORDER BY s.[name], o.[name];
                """;
            await using var command = sqlConnection.CreateCommand();
            command.CommandText = commandText;
            command.CommandTimeout = configuration.CommandTimeoutSeconds;
            command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int)
            {
                Value = checked(take + 1),
            });
            var searchParameter = new SqlParameter("@search", SqlDbType.NVarChar, 514)
            {
                IsNullable = true,
                Value = search is null
                    ? DBNull.Value
                    : $"%{SqlPublicationQueryPlanner.EscapeLikeValue(search)}%",
            };
            command.Parameters.Add(searchParameter);
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken).ConfigureAwait(false);
            var objects = new List<PublicationSourceObjectDto>(take);
            var hasMore = false;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (objects.Count == take)
                {
                    hasMore = true;
                    break;
                }

                objects.Add(new PublicationSourceObjectDto(
                    reader.GetString(0),
                    reader.GetString(1),
                    ReadObjectKind(reader.GetString(2)),
                    checked((int)reader.GetInt64(3))));
            }

            return new(
                PublicationSourceInspectionStatus.Success,
                new PublicationSourceObjectPageDto(objects, hasMore));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            logger.LogWarning(
                exception,
                "Publication source object discovery failed for connection {ConnectionId}.",
                connection.Id);
            return new(PublicationSourceInspectionStatus.Unavailable, null);
        }
    }

    public async Task<PublicationSourceInspectionResult<PublicationSourceObjectInspectionDto>> InspectObjectAsync(
        DataConnection connection,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!IsBoundedIdentifier(schema) || !IsBoundedIdentifier(objectName))
        {
            return new(PublicationSourceInspectionStatus.BoundsExceeded, null);
        }

        try
        {
            var configuration = RequireSqlConfiguration(connection);
            await using var sqlConnection = new SqlConnection(
                SqlPublicationSourceDataReader.BuildConnectionString(configuration).ConnectionString);
            await sqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var discovered = await ReadColumnsAsync(
                    sqlConnection,
                    schema,
                    objectName,
                    configuration.CommandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (discovered is null)
            {
                return new(PublicationSourceInspectionStatus.NotFound, null);
            }

            if (discovered.Columns.Count > MaximumColumns)
            {
                return new(PublicationSourceInspectionStatus.BoundsExceeded, null);
            }

            var foreignKeyMappings = await ReadForeignKeysAsync(
                    sqlConnection,
                    schema,
                    objectName,
                    configuration.CommandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (foreignKeyMappings.Count > MaximumForeignKeyMappings)
            {
                return new(PublicationSourceInspectionStatus.BoundsExceeded, null);
            }

            var foreignKeys = new List<PublicationSourceForeignKeyDto>();
            foreach (var group in foreignKeyMappings
                         .GroupBy(mapping => mapping.ConstraintName, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var mappings = group.OrderBy(mapping => mapping.Ordinal).ToArray();
                var first = mappings[0];
                var referenced = await ReadCandidateColumnsAsync(
                        sqlConnection,
                        first.ReferencedSchema,
                        first.ReferencedObject,
                        configuration.CommandTimeoutSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (referenced is null || referenced.Count > MaximumColumns)
                {
                    return new(PublicationSourceInspectionStatus.BoundsExceeded, null);
                }

                var referencedKeys = mappings
                    .Select(mapping => mapping.ReferencedColumn)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var candidates = ChooseDisplayCandidates(referenced, referencedKeys);
                foreignKeys.Add(new PublicationSourceForeignKeyDto(
                    first.ConstraintName,
                    first.ReferencedSchema,
                    first.ReferencedObject,
                    mappings.Select(mapping => new PublicationSourceForeignKeyColumnDto(
                        mapping.Ordinal,
                        mapping.LocalColumn,
                        mapping.ReferencedColumn)).ToArray(),
                    candidates,
                    candidates,
                    candidates[0],
                    PublicationLookupMode.ServerFiltered));
            }

            var columns = discovered.Columns.Select(ToDto).ToArray();
            var fingerprint = ComputeFingerprint(
                schema,
                objectName,
                discovered.Kind,
                columns,
                foreignKeys);
            return new(
                PublicationSourceInspectionStatus.Success,
                new PublicationSourceObjectInspectionDto(
                    connection.Id,
                    schema,
                    objectName,
                    discovered.Kind,
                    fingerprint,
                    columns,
                    foreignKeys));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            logger.LogWarning(
                exception,
                "Publication source inspection failed for connection {ConnectionId}.",
                connection.Id);
            return new(PublicationSourceInspectionStatus.Unavailable, null);
        }
    }

    private SqlServerConnectionConfiguration RequireSqlConfiguration(DataConnection connection)
    {
        if (!connection.IsEnabled || connection.Kind != ConnectionKind.SqlServer)
        {
            throw new ConnectionConfigurationException("An enabled SQL Server connection is required.");
        }

        // The serializer enforces the v1 integrated-security-only shape and rejects unknown fields.
        return configurationSerializer.Deserialize(connection) as SqlServerConnectionConfiguration
            ?? throw new ConnectionConfigurationException("A SQL Server connection is required.");
    }

    private static async Task<DiscoveredObject?> ReadColumnsAsync(
        SqlConnection connection,
        string schema,
        string objectName,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        const string commandText = """
            SELECT TOP (@take)
                o.[type],
                c.[column_id],
                c.[name],
                t.[name],
                c.[max_length],
                c.[precision],
                c.[scale],
                c.[is_nullable],
                c.[is_identity],
                c.[is_computed],
                CASE WHEN t.[system_type_id] = 189 THEN 1 ELSE 0 END,
                key_column.[key_ordinal]
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s ON s.[schema_id] = o.[schema_id]
            INNER JOIN sys.columns AS c ON c.[object_id] = o.[object_id]
            INNER JOIN sys.types AS t ON t.[user_type_id] = c.[user_type_id]
            LEFT JOIN
            (
                SELECT ic.[object_id], ic.[column_id], ic.[key_ordinal]
                FROM sys.indexes AS i
                INNER JOIN sys.index_columns AS ic
                    ON ic.[object_id] = i.[object_id]
                    AND ic.[index_id] = i.[index_id]
                WHERE i.[is_unique] = 1
                  AND i.[has_filter] = 0
                  AND i.[is_disabled] = 0
                  AND i.[is_hypothetical] = 0
                  AND ic.[key_ordinal] > 0
                  AND i.[index_id] =
                  (
                      SELECT TOP (1) selected_index.[index_id]
                      FROM sys.indexes AS selected_index
                      WHERE selected_index.[object_id] = i.[object_id]
                        AND selected_index.[is_unique] = 1
                        AND selected_index.[has_filter] = 0
                        AND selected_index.[is_disabled] = 0
                        AND selected_index.[is_hypothetical] = 0
                      ORDER BY selected_index.[is_primary_key] DESC, selected_index.[index_id]
                  )
            ) AS key_column
                ON key_column.[object_id] = c.[object_id]
                AND key_column.[column_id] = c.[column_id]
            WHERE s.[name] = @schema
              AND o.[name] = @object
              AND o.[type] IN ('U', 'V')
            ORDER BY c.[column_id];
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = commandTimeout;
        command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = MaximumColumns + 1 });
        AddObjectParameters(command, schema, objectName);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken).ConfigureAwait(false);
        PublicationSourceObjectKind? kind = null;
        var columns = new List<DiscoveredColumn>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            kind ??= ReadObjectKind(reader.GetString(0));
            var ordinal = reader.GetInt32(1) - 1;
            var name = reader.GetString(2);
            var typeName = reader.GetString(3);
            var maxLength = reader.GetInt16(4);
            var precision = reader.GetByte(5);
            var scale = reader.GetByte(6);
            var isNullable = reader.GetBoolean(7);
            var isGenerated = reader.GetBoolean(8) || reader.GetBoolean(9);
            var rowVersion = reader.GetInt32(10) == 1;
            int? keyOrdinal = reader.IsDBNull(11) ? null : reader.GetByte(11) - 1;
            columns.Add(new DiscoveredColumn(
                ordinal,
                name,
                FormatSourceType(typeName, maxLength, precision, scale),
                typeName,
                isNullable,
                isGenerated || rowVersion,
                rowVersion,
                keyOrdinal,
                ReadMaximumLength(typeName, maxLength),
                IsNumeric(typeName) ? precision : null,
                IsNumeric(typeName) ? scale : null));
        }

        return kind is null ? null : new DiscoveredObject(kind.Value, columns);
    }

    private static async Task<IReadOnlyList<ForeignKeyMapping>> ReadForeignKeysAsync(
        SqlConnection connection,
        string schema,
        string objectName,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        const string commandText = """
            SELECT TOP (@take)
                fk.[name],
                fkc.[constraint_column_id],
                parent_column.[name],
                referenced_schema.[name],
                referenced_object.[name],
                referenced_column.[name]
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.foreign_key_columns AS fkc
                ON fkc.[constraint_object_id] = fk.[object_id]
            INNER JOIN sys.objects AS parent_object ON parent_object.[object_id] = fk.[parent_object_id]
            INNER JOIN sys.schemas AS parent_schema ON parent_schema.[schema_id] = parent_object.[schema_id]
            INNER JOIN sys.columns AS parent_column
                ON parent_column.[object_id] = fkc.[parent_object_id]
                AND parent_column.[column_id] = fkc.[parent_column_id]
            INNER JOIN sys.objects AS referenced_object
                ON referenced_object.[object_id] = fk.[referenced_object_id]
            INNER JOIN sys.schemas AS referenced_schema
                ON referenced_schema.[schema_id] = referenced_object.[schema_id]
            INNER JOIN sys.columns AS referenced_column
                ON referenced_column.[object_id] = fkc.[referenced_object_id]
                AND referenced_column.[column_id] = fkc.[referenced_column_id]
            WHERE parent_schema.[name] = @schema
              AND parent_object.[name] = @object
            ORDER BY fk.[name], fkc.[constraint_column_id];
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = commandTimeout;
        command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int)
        {
            Value = MaximumForeignKeyMappings + 1,
        });
        AddObjectParameters(command, schema, objectName);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken).ConfigureAwait(false);
        var mappings = new List<ForeignKeyMapping>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            mappings.Add(new ForeignKeyMapping(
                reader.GetString(0),
                reader.GetInt32(1) - 1,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return mappings;
    }

    private static async Task<IReadOnlyList<CandidateColumn>?> ReadCandidateColumnsAsync(
        SqlConnection connection,
        string schema,
        string objectName,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        const string commandText = """
            SELECT TOP (@take) c.[name], t.[name], c.[column_id]
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s ON s.[schema_id] = o.[schema_id]
            INNER JOIN sys.columns AS c ON c.[object_id] = o.[object_id]
            INNER JOIN sys.types AS t ON t.[user_type_id] = c.[user_type_id]
            WHERE s.[name] = @schema
              AND o.[name] = @object
              AND o.[type] = 'U'
            ORDER BY c.[column_id];
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = commandTimeout;
        command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = MaximumColumns + 1 });
        AddObjectParameters(command, schema, objectName);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken).ConfigureAwait(false);
        var columns = new List<CandidateColumn>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new CandidateColumn(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return columns.Count == 0 ? null : columns;
    }

    private static IReadOnlyList<string> ChooseDisplayCandidates(
        IReadOnlyList<CandidateColumn> columns,
        IReadOnlySet<string> referencedKeys)
    {
        var preferredNames = new[] { "DisplayName", "Name", "Title", "Code", "Description" };
        var strings = columns.Where(column => MapDataType(column.TypeName) == PublicationDataType.String);
        var candidates = strings
            .OrderBy(column =>
            {
                var index = Array.FindIndex(preferredNames, name =>
                    string.Equals(name, column.Name, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? preferredNames.Length : index;
            })
            .ThenBy(column => column.Ordinal)
            .Select(column => column.Name)
            .Concat(columns.Where(column => referencedKeys.Contains(column.Name)).Select(column => column.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(PublicationForeignKey.MaximumSearchColumns)
            .ToArray();
        return candidates.Length > 0
            ? candidates
            : [columns.OrderBy(column => column.Ordinal).First().Name];
    }

    private static PublicationSourceColumnDto ToDto(DiscoveredColumn column)
    {
        var dataType = MapDataType(column.BaseTypeName);
        var maximumLength = dataType is PublicationDataType.String or PublicationDataType.Binary
            ? column.MaximumLength is > PublicationColumn.MaximumDeclaredValueLength
                ? null
                : column.MaximumLength
            : null;
        return new PublicationSourceColumnDto(
            column.Ordinal,
            column.Name,
            column.SourceTypeName,
            dataType,
            dataType is not null,
            column.IsNullable,
            column.IsGenerated,
            column.IsRowVersion,
            column.KeyOrdinal is not null,
            column.KeyOrdinal,
            maximumLength,
            column.NumericPrecision,
            column.NumericScale);
    }

    private static PublicationDataType? MapDataType(string typeName) =>
        typeName.ToLowerInvariant() switch
        {
            "bit" => PublicationDataType.Boolean,
            "tinyint" => PublicationDataType.Byte,
            "smallint" => PublicationDataType.Int16,
            "int" => PublicationDataType.Int32,
            "bigint" => PublicationDataType.Int64,
            "decimal" or "numeric" or "money" or "smallmoney" => PublicationDataType.Decimal,
            "real" => PublicationDataType.Single,
            "float" => PublicationDataType.Double,
            "date" => PublicationDataType.Date,
            "datetime" or "datetime2" or "smalldatetime" => PublicationDataType.DateTime,
            "datetimeoffset" => PublicationDataType.DateTimeOffset,
            "time" => PublicationDataType.Time,
            "uniqueidentifier" => PublicationDataType.Guid,
            "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext" or "xml" =>
                PublicationDataType.String,
            "binary" or "varbinary" or "image" or "rowversion" or "timestamp" =>
                PublicationDataType.Binary,
            _ => null,
        };

    private static string FormatSourceType(
        string typeName,
        short maximumLength,
        byte precision,
        byte scale)
    {
        if (typeName.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("numeric", StringComparison.OrdinalIgnoreCase))
        {
            return $"{typeName}({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})";
        }

        if (typeName is "varchar" or "char" or "varbinary" or "binary" or "nvarchar" or "nchar")
        {
            var length = maximumLength < 0
                ? "max"
                : ReadMaximumLength(typeName, maximumLength)?.ToString(CultureInfo.InvariantCulture) ?? "max";
            return $"{typeName}({length})";
        }

        if (typeName is "datetime2" or "datetimeoffset" or "time")
        {
            return $"{typeName}({scale.ToString(CultureInfo.InvariantCulture)})";
        }

        return typeName;
    }

    private static int? ReadMaximumLength(string typeName, short maximumLength)
    {
        if (maximumLength < 0 || typeName is "text" or "ntext" or "image" or "xml")
        {
            return null;
        }

        return typeName is "nvarchar" or "nchar" ? maximumLength / 2 : maximumLength;
    }

    private static bool IsNumeric(string typeName) =>
        typeName is "decimal" or "numeric" or "money" or "smallmoney";

    private static string ComputeFingerprint(
        string schema,
        string objectName,
        PublicationSourceObjectKind kind,
        IReadOnlyList<PublicationSourceColumnDto> columns,
        IReadOnlyList<PublicationSourceForeignKeyDto> foreignKeys)
    {
        var canonical = new StringBuilder()
            .Append(schema).Append('|').Append(objectName).Append('|').Append((int)kind).AppendLine();
        foreach (var column in columns.OrderBy(column => column.Ordinal))
        {
            canonical.Append(column.Ordinal).Append('|')
                .Append(column.Name).Append('|')
                .Append(column.SourceTypeName).Append('|')
                .Append(column.IsNullable).Append('|')
                .Append(column.IsGenerated).Append('|')
                .Append(column.IsRowVersion).Append('|')
                .Append(column.KeyOrdinal).AppendLine();
        }

        foreach (var foreignKey in foreignKeys.OrderBy(value => value.ConstraintName, StringComparer.OrdinalIgnoreCase))
        {
            canonical.Append(foreignKey.ConstraintName).Append('|')
                .Append(foreignKey.ReferencedSchema).Append('|')
                .Append(foreignKey.ReferencedObject).Append('|');
            foreach (var mapping in foreignKey.Columns.OrderBy(value => value.Ordinal))
            {
                canonical.Append(mapping.Ordinal).Append(':')
                    .Append(mapping.LocalColumn).Append('>')
                    .Append(mapping.ReferencedColumn).Append(',');
            }

            canonical.AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AddObjectParameters(SqlCommand command, string schema, string objectName)
    {
        command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = schema });
        command.Parameters.Add(new SqlParameter("@object", SqlDbType.NVarChar, 128) { Value = objectName });
    }

    private static PublicationSourceObjectKind ReadObjectKind(string value) => value.Trim() switch
    {
        "U" => PublicationSourceObjectKind.Table,
        "V" => PublicationSourceObjectKind.View,
        _ => throw new InvalidOperationException("Unsupported SQL Server object type."),
    };

    private static bool IsBoundedIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);

    private static bool IsExpectedFailure(Exception exception) => exception is SqlException
        or TimeoutException
        or InvalidOperationException
        or ArgumentException
        or ConnectionConfigurationException
        or OverflowException;

    private sealed record DiscoveredObject(
        PublicationSourceObjectKind Kind,
        IReadOnlyList<DiscoveredColumn> Columns);

    private sealed record DiscoveredColumn(
        int Ordinal,
        string Name,
        string SourceTypeName,
        string BaseTypeName,
        bool IsNullable,
        bool IsGenerated,
        bool IsRowVersion,
        int? KeyOrdinal,
        int? MaximumLength,
        byte? NumericPrecision,
        byte? NumericScale);

    private sealed record ForeignKeyMapping(
        string ConstraintName,
        int Ordinal,
        string LocalColumn,
        string ReferencedSchema,
        string ReferencedObject,
        string ReferencedColumn);

    private sealed record CandidateColumn(string Name, string TypeName, int Ordinal);
}
