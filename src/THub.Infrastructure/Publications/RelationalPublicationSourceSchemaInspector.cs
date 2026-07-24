using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using THub.Application.Connections;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;
using THub.Infrastructure.Connections;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Publications;

public sealed class RelationalPublicationSourceSchemaInspector(
    ConnectionConfigurationSerializer serializer,
    RelationalConnectionFactory connectionFactory,
    ILogger<RelationalPublicationSourceSchemaInspector> logger)
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
            var source = await OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var database = source.Connection;
            await using var command = database.CreateCommand();
            ConfigureCommand(command, source.Configuration.CommandTimeoutSeconds);
            command.CommandText = BuildListObjectsSql(
                source.Configuration.Kind,
                checked(take + 1));
            if (source.Configuration.Kind == ConnectionKind.MySql)
            {
                AddParameter(
                    command,
                    source.Configuration.Kind,
                    "database",
                    source.Configuration.Database);
            }
            AddParameter(
                command,
                source.Configuration.Kind,
                "search",
                string.IsNullOrWhiteSpace(search) ? DBNull.Value : $"%{search.Trim()}%");

            var objects = new List<PublicationSourceObjectDto>(take);
            var hasMore = false;
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken).ConfigureAwait(false);
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
                    Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture)));
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
                "Publication source discovery failed for connection {ConnectionId} ({ConnectionKind}).",
                connection.Id,
                connection.Kind);
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
            var source = await OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var database = source.Connection;
            var kind = await ReadObjectKindAsync(
                    database,
                    source.Configuration,
                    schema,
                    objectName,
                    cancellationToken)
                .ConfigureAwait(false);
            if (kind is null)
            {
                return new(PublicationSourceInspectionStatus.NotFound, null);
            }

            var keyOrdinals = await ReadStableKeyOrdinalsAsync(
                    database,
                    source.Configuration,
                    schema,
                    objectName,
                    cancellationToken)
                .ConfigureAwait(false);
            var columns = await ReadColumnsAsync(
                    database,
                    source.Configuration,
                    schema,
                    objectName,
                    keyOrdinals,
                    cancellationToken)
                .ConfigureAwait(false);
            if (columns.Count is 0 or > MaximumColumns)
            {
                return new(PublicationSourceInspectionStatus.BoundsExceeded, null);
            }

            var mappings = await ReadForeignKeysAsync(
                    database,
                    source.Configuration,
                    schema,
                    objectName,
                    cancellationToken)
                .ConfigureAwait(false);
            if (mappings.Count > MaximumForeignKeyMappings)
            {
                return new(PublicationSourceInspectionStatus.BoundsExceeded, null);
            }

            var foreignKeys = new List<PublicationSourceForeignKeyDto>();
            foreach (var group in mappings
                         .GroupBy(mapping => mapping.ConstraintName, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var ordered = group.OrderBy(mapping => mapping.Ordinal).ToArray();
                var first = ordered[0];
                var referenced = await ReadColumnsAsync(
                        database,
                        source.Configuration,
                        first.ReferencedSchema,
                        first.ReferencedObject,
                        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (referenced.Count is 0 or > MaximumColumns)
                {
                    return new(PublicationSourceInspectionStatus.BoundsExceeded, null);
                }

                var referencedKeys = ordered
                    .Select(mapping => mapping.ReferencedColumn)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var candidates = ChooseDisplayCandidates(referenced, referencedKeys);
                foreignKeys.Add(new PublicationSourceForeignKeyDto(
                    first.ConstraintName,
                    first.ReferencedSchema,
                    first.ReferencedObject,
                    ordered.Select(mapping => new PublicationSourceForeignKeyColumnDto(
                        mapping.Ordinal,
                        mapping.LocalColumn,
                        mapping.ReferencedColumn)).ToArray(),
                    candidates,
                    candidates,
                    candidates[0],
                    PublicationLookupMode.ServerFiltered));
            }

            var fingerprint = ComputeFingerprint(schema, objectName, kind.Value, columns, foreignKeys);
            return new(
                PublicationSourceInspectionStatus.Success,
                new PublicationSourceObjectInspectionDto(
                    connection.Id,
                    schema,
                    objectName,
                    kind.Value,
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
                "Publication source inspection failed for connection {ConnectionId} ({ConnectionKind}) and object {Schema}.{Object}.",
                connection.Id,
                connection.Kind,
                schema,
                objectName);
            return new(PublicationSourceInspectionStatus.Unavailable, null);
        }
    }

    private async Task<RelationalSource> OpenAsync(
        DataConnection connection,
        CancellationToken cancellationToken)
    {
        var configuration = serializer.Deserialize(connection) as RelationalDatabaseConnectionConfiguration
            ?? throw new ConnectionConfigurationException(
                "A relational database connection configuration is required.");
        var database = await connectionFactory.CreateAsync(configuration, cancellationToken)
            .ConfigureAwait(false);
        await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return new RelationalSource(configuration, database);
    }

    private static async Task<PublicationSourceObjectKind?> ReadObjectKindAsync(
        DbConnection connection,
        RelationalDatabaseConnectionConfiguration configuration,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        ConfigureCommand(command, configuration.CommandTimeoutSeconds);
        command.CommandText = configuration.Kind switch
        {
            ConnectionKind.MySql => """
                SELECT table_type
                FROM information_schema.tables
                WHERE table_schema = @schema AND table_name = @object
                  AND table_type IN ('BASE TABLE', 'VIEW')
                """,
            ConnectionKind.PostgreSql => """
                SELECT table_type
                FROM information_schema.tables
                WHERE table_catalog = current_database()
                  AND table_schema = @schema AND table_name = @object
                  AND table_type IN ('BASE TABLE', 'VIEW')
                """,
            ConnectionKind.Oracle => """
                SELECT object_type
                FROM all_objects
                WHERE owner = :schema AND object_name = :object
                  AND object_type IN ('TABLE', 'VIEW')
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(configuration))
        };
        AddParameter(command, configuration.Kind, "schema", schema);
        AddParameter(command, configuration.Kind, "object", objectName);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : ReadObjectKind(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadStableKeyOrdinalsAsync(
        DbConnection connection,
        RelationalDatabaseConnectionConfiguration configuration,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        ConfigureCommand(command, configuration.CommandTimeoutSeconds);
        command.CommandText = configuration.Kind switch
        {
            ConnectionKind.MySql => """
                SELECT selected.column_name, selected.seq_in_index
                FROM information_schema.statistics selected
                WHERE selected.table_schema = @schema
                  AND selected.table_name = @object
                  AND selected.index_name =
                  (
                      SELECT candidate.index_name
                      FROM information_schema.statistics candidate
                      WHERE candidate.table_schema = @schema
                        AND candidate.table_name = @object
                        AND candidate.non_unique = 0
                      GROUP BY candidate.index_name
                      HAVING SUM(CASE WHEN candidate.nullable = 'YES' OR candidate.column_name IS NULL THEN 1 ELSE 0 END) = 0
                         AND COUNT(*) <= 16
                      ORDER BY CASE WHEN candidate.index_name = 'PRIMARY' THEN 0 ELSE 1 END,
                               candidate.index_name
                      LIMIT 1
                  )
                ORDER BY selected.seq_in_index
                """,
            ConnectionKind.PostgreSql => """
                WITH eligible AS
                (
                    SELECT tc.constraint_name, tc.constraint_type
                    FROM information_schema.table_constraints tc
                    INNER JOIN information_schema.key_column_usage kcu
                      ON kcu.constraint_catalog = tc.constraint_catalog
                     AND kcu.constraint_schema = tc.constraint_schema
                     AND kcu.constraint_name = tc.constraint_name
                    INNER JOIN information_schema.columns c
                      ON c.table_catalog = kcu.table_catalog
                     AND c.table_schema = kcu.table_schema
                     AND c.table_name = kcu.table_name
                     AND c.column_name = kcu.column_name
                    WHERE tc.table_catalog = current_database()
                      AND tc.table_schema = @schema AND tc.table_name = @object
                      AND tc.constraint_type IN ('PRIMARY KEY', 'UNIQUE')
                    GROUP BY tc.constraint_name, tc.constraint_type
                    HAVING BOOL_AND(c.is_nullable = 'NO') AND COUNT(*) <= 16
                ),
                selected AS
                (
                    SELECT constraint_name
                    FROM eligible
                    ORDER BY CASE WHEN constraint_type = 'PRIMARY KEY' THEN 0 ELSE 1 END,
                             constraint_name
                    LIMIT 1
                )
                SELECT kcu.column_name, kcu.ordinal_position
                FROM information_schema.key_column_usage kcu
                INNER JOIN selected ON selected.constraint_name = kcu.constraint_name
                WHERE kcu.table_catalog = current_database()
                  AND kcu.table_schema = @schema AND kcu.table_name = @object
                ORDER BY kcu.ordinal_position
                """,
            ConnectionKind.Oracle => """
                WITH eligible AS
                (
                    SELECT c.owner, c.constraint_name, c.constraint_type
                    FROM all_constraints c
                    INNER JOIN all_cons_columns cc
                      ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
                    INNER JOIN all_tab_columns tc
                      ON tc.owner = cc.owner
                     AND tc.table_name = cc.table_name
                     AND tc.column_name = cc.column_name
                    WHERE c.owner = :schema AND c.table_name = :object
                      AND c.constraint_type IN ('P', 'U')
                      AND c.status = 'ENABLED'
                    GROUP BY c.owner, c.constraint_name, c.constraint_type
                    HAVING SUM(CASE WHEN tc.nullable = 'Y' THEN 1 ELSE 0 END) = 0
                       AND COUNT(*) <= 16
                ),
                selected AS
                (
                    SELECT owner, constraint_name
                    FROM eligible
                    ORDER BY CASE WHEN constraint_type = 'P' THEN 0 ELSE 1 END,
                             constraint_name
                    FETCH FIRST 1 ROW ONLY
                )
                SELECT cc.column_name, cc.position
                FROM all_cons_columns cc
                INNER JOIN selected
                  ON selected.owner = cc.owner
                 AND selected.constraint_name = cc.constraint_name
                ORDER BY cc.position
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(configuration))
        };
        AddParameter(command, configuration.Kind, "schema", schema);
        AddParameter(command, configuration.Kind, "object", objectName);
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ordinals.Add(
                reader.GetString(0),
                checked(Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) - 1));
        }

        return ordinals;
    }

    private static async Task<IReadOnlyList<PublicationSourceColumnDto>> ReadColumnsAsync(
        DbConnection connection,
        RelationalDatabaseConnectionConfiguration configuration,
        string schema,
        string objectName,
        IReadOnlyDictionary<string, int> keyOrdinals,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        ConfigureCommand(command, configuration.CommandTimeoutSeconds);
        command.CommandText =
            $"SELECT * FROM {RelationalExecutionSupport.QualifiedName(configuration.Kind, schema, objectName)} WHERE 1 = 0";
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo,
            cancellationToken).ConfigureAwait(false);
        var schemaColumns = await reader.GetColumnSchemaAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<PublicationSourceColumnDto>(schemaColumns.Count);
        foreach (var column in schemaColumns)
        {
            var ordinal = column.ColumnOrdinal ?? columns.Count;
            var name = column.ColumnName
                ?? throw new InvalidOperationException("A publication source column has no name.");
            var dataType = MapDataType(
                configuration.Kind,
                column.DataType,
                column.DataTypeName);
            var maximumLength = ReadMaximumLength(dataType, column.ColumnSize);
            columns.Add(new PublicationSourceColumnDto(
                ordinal,
                name,
                column.DataTypeName ?? column.DataType?.Name ?? "unknown",
                dataType,
                dataType is not null,
                column.AllowDBNull ?? true,
                column.IsAutoIncrement == true || column.IsReadOnly == true,
                false,
                keyOrdinals.TryGetValue(name, out var keyOrdinal),
                keyOrdinals.TryGetValue(name, out keyOrdinal) ? keyOrdinal : null,
                maximumLength,
                ToByte(column.NumericPrecision),
                ToByte(column.NumericScale)));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<ForeignKeyMapping>> ReadForeignKeysAsync(
        DbConnection connection,
        RelationalDatabaseConnectionConfiguration configuration,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        ConfigureCommand(command, configuration.CommandTimeoutSeconds);
        command.CommandText = configuration.Kind switch
        {
            ConnectionKind.MySql => """
                SELECT constraint_name, ordinal_position, column_name,
                       referenced_table_schema, referenced_table_name, referenced_column_name
                FROM information_schema.key_column_usage
                WHERE table_schema = @schema AND table_name = @object
                  AND referenced_table_name IS NOT NULL
                ORDER BY constraint_name, ordinal_position
                """,
            ConnectionKind.PostgreSql => """
                SELECT fk.constraint_name, fk.ordinal_position, fk.column_name,
                       pk.table_schema, pk.table_name, pk.column_name
                FROM information_schema.referential_constraints rc
                INNER JOIN information_schema.key_column_usage fk
                  ON fk.constraint_catalog = rc.constraint_catalog
                 AND fk.constraint_schema = rc.constraint_schema
                 AND fk.constraint_name = rc.constraint_name
                INNER JOIN information_schema.key_column_usage pk
                  ON pk.constraint_catalog = rc.unique_constraint_catalog
                 AND pk.constraint_schema = rc.unique_constraint_schema
                 AND pk.constraint_name = rc.unique_constraint_name
                 AND pk.ordinal_position = fk.position_in_unique_constraint
                WHERE fk.table_catalog = current_database()
                  AND fk.table_schema = @schema AND fk.table_name = @object
                ORDER BY fk.constraint_name, fk.ordinal_position
                """,
            ConnectionKind.Oracle => """
                SELECT c.constraint_name, local_columns.position, local_columns.column_name,
                       referenced.owner, referenced.table_name, referenced_columns.column_name
                FROM all_constraints c
                INNER JOIN all_cons_columns local_columns
                  ON local_columns.owner = c.owner AND local_columns.constraint_name = c.constraint_name
                INNER JOIN all_constraints referenced
                  ON referenced.owner = c.r_owner AND referenced.constraint_name = c.r_constraint_name
                INNER JOIN all_cons_columns referenced_columns
                  ON referenced_columns.owner = referenced.owner
                 AND referenced_columns.constraint_name = referenced.constraint_name
                 AND referenced_columns.position = local_columns.position
                WHERE c.owner = :schema AND c.table_name = :object
                  AND c.constraint_type = 'R'
                ORDER BY c.constraint_name, local_columns.position
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(configuration))
        };
        AddParameter(command, configuration.Kind, "schema", schema);
        AddParameter(command, configuration.Kind, "object", objectName);
        var mappings = new List<ForeignKeyMapping>();
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            mappings.Add(new ForeignKeyMapping(
                reader.GetString(0),
                checked(Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) - 1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return mappings;
    }

    private static string BuildListObjectsSql(ConnectionKind kind, int take) => kind switch
    {
        ConnectionKind.MySql => $"""
            SELECT t.table_schema, t.table_name, t.table_type,
                   (SELECT COUNT(*) FROM information_schema.columns c
                    WHERE c.table_schema = t.table_schema AND c.table_name = t.table_name)
            FROM information_schema.tables t
            WHERE t.table_schema = @database
              AND t.table_type IN ('BASE TABLE', 'VIEW')
              AND (@search IS NULL OR t.table_name LIKE @search)
            ORDER BY t.table_schema, t.table_name
            LIMIT {take.ToString(CultureInfo.InvariantCulture)}
            """,
        ConnectionKind.PostgreSql => $"""
            SELECT t.table_schema, t.table_name, t.table_type,
                   (SELECT COUNT(*) FROM information_schema.columns c
                    WHERE c.table_catalog = t.table_catalog
                      AND c.table_schema = t.table_schema AND c.table_name = t.table_name)
            FROM information_schema.tables t
            WHERE t.table_catalog = current_database()
              AND t.table_schema NOT IN ('pg_catalog', 'information_schema')
              AND t.table_type IN ('BASE TABLE', 'VIEW')
              AND (@search IS NULL OR t.table_name ILIKE @search)
            ORDER BY t.table_schema, t.table_name
            LIMIT {take.ToString(CultureInfo.InvariantCulture)}
            """,
        ConnectionKind.Oracle => $"""
            SELECT o.owner, o.object_name, o.object_type,
                   (SELECT COUNT(*) FROM all_tab_columns c
                    WHERE c.owner = o.owner AND c.table_name = o.object_name)
            FROM all_objects o
            WHERE o.object_type IN ('TABLE', 'VIEW')
              AND (:search IS NULL OR UPPER(o.object_name) LIKE UPPER(:search))
            ORDER BY o.owner, o.object_name
            FETCH FIRST {take.ToString(CultureInfo.InvariantCulture)} ROWS ONLY
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static IReadOnlyList<string> ChooseDisplayCandidates(
        IReadOnlyList<PublicationSourceColumnDto> columns,
        IReadOnlySet<string> referencedKeys)
    {
        var preferredNames = new[] { "DisplayName", "Name", "Title", "Code", "Description" };
        var candidates = columns
            .Where(column => column.DataType == PublicationDataType.String)
            .OrderBy(column =>
            {
                var index = Array.FindIndex(preferredNames, value =>
                    string.Equals(value, column.Name, StringComparison.OrdinalIgnoreCase));
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

    private static PublicationDataType? MapDataType(
        ConnectionKind kind,
        Type? type,
        string? sourceTypeName)
    {
        type = Nullable.GetUnderlyingType(type ?? typeof(string)) ?? type;
        if (type == typeof(bool)) return PublicationDataType.Boolean;
        if (type == typeof(byte)) return PublicationDataType.Byte;
        if (type == typeof(short)) return PublicationDataType.Int16;
        if (type == typeof(int)) return PublicationDataType.Int32;
        if (type == typeof(uint) || type == typeof(long)) return PublicationDataType.Int64;
        if (type == typeof(ulong) || type == typeof(decimal)) return PublicationDataType.Decimal;
        if (type == typeof(float)) return PublicationDataType.Single;
        if (type == typeof(double)) return PublicationDataType.Double;
        if (type == typeof(DateOnly)) return PublicationDataType.Date;
        if (type == typeof(DateTime)) return IsDateSourceType(kind, sourceTypeName)
            ? PublicationDataType.Date
            : PublicationDataType.DateTime;
        if (type == typeof(DateTimeOffset)) return PublicationDataType.DateTimeOffset;
        if (type == typeof(TimeOnly) || type == typeof(TimeSpan)) return PublicationDataType.Time;
        if (type == typeof(Guid)) return PublicationDataType.Guid;
        if (type == typeof(string) || type == typeof(char)) return PublicationDataType.String;
        if (type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>)) return PublicationDataType.Binary;
        return MapSourceType(kind, sourceTypeName);
    }

    private static PublicationDataType? MapSourceType(
        ConnectionKind kind,
        string? sourceTypeName)
    {
        var normalized = sourceTypeName?
            .Split('(', 2, StringSplitOptions.TrimEntries)[0]
            .Trim()
            .ToLowerInvariant();
        return normalized switch
        {
            "bool" or "boolean" => PublicationDataType.Boolean,
            "tinyint" or "int1" => PublicationDataType.Byte,
            "smallint" or "int2" => PublicationDataType.Int16,
            "integer" or "int" or "int4" or "mediumint" => PublicationDataType.Int32,
            "bigint" or "int8" => PublicationDataType.Int64,
            "decimal" or "numeric" or "number" or "money" => PublicationDataType.Decimal,
            "real" or "float4" or "binary_float" => PublicationDataType.Single,
            "double" or "double precision" or "float8" or "binary_double" =>
                PublicationDataType.Double,
            "date" when kind == ConnectionKind.Oracle => PublicationDataType.DateTime,
            "date" => PublicationDataType.Date,
            "datetime" or "datetime2" or "smalldatetime" or "timestamp" or
                "timestamp without time zone" => PublicationDataType.DateTime,
            "datetimeoffset" or "timestamp with time zone" or "timestamp with local time zone" =>
                PublicationDataType.DateTimeOffset,
            "time" or "time without time zone" or "interval" or "interval day to second" =>
                PublicationDataType.Time,
            "uuid" or "uniqueidentifier" => PublicationDataType.Guid,
            "char" or "character" or "varchar" or "varchar2" or "nvarchar" or "nvarchar2" or
                "text" or "tinytext" or "mediumtext" or "longtext" or "clob" or "nclob" or
                "json" or "jsonb" or "xml" => PublicationDataType.String,
            "binary" or "varbinary" or "bytea" or "raw" or "long raw" or "blob" or
                "tinyblob" or "mediumblob" or "longblob" => PublicationDataType.Binary,
            _ => null
        };
    }

    private static int? ReadMaximumLength(PublicationDataType? dataType, int? size) =>
        dataType is PublicationDataType.String or PublicationDataType.Binary &&
        size is > 0 and <= PublicationColumn.MaximumDeclaredValueLength
            ? size
            : null;

    private static byte? ToByte(int? value) =>
        value is >= byte.MinValue and <= byte.MaxValue ? checked((byte)value.Value) : null;

    private static bool IsDateSourceType(ConnectionKind kind, string? sourceTypeName) =>
        kind != ConnectionKind.Oracle &&
        string.Equals(sourceTypeName, "date", StringComparison.OrdinalIgnoreCase);

    private static PublicationSourceObjectKind ReadObjectKind(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "BASE TABLE" or "TABLE" => PublicationSourceObjectKind.Table,
            "VIEW" => PublicationSourceObjectKind.View,
            _ => throw new InvalidOperationException("Unsupported relational publication object type.")
        };

    private static void ConfigureCommand(DbCommand command, int timeout)
    {
        command.CommandTimeout = timeout;
        if (command is OracleCommand oracle)
        {
            oracle.BindByName = true;
        }
    }

    private static void AddParameter(
        DbCommand command,
        ConnectionKind kind,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool IsBoundedIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        !value.Any(char.IsControl);

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

    private static bool IsExpectedFailure(Exception exception) => exception is
        ConnectionConfigurationException
        or ConnectionCredentialUnavailableException
        or DbException
        or InvalidOperationException
        or ArgumentException
        or OverflowException;

    private sealed record RelationalSource(
        RelationalDatabaseConnectionConfiguration Configuration,
        DbConnection Connection);

    private sealed record ForeignKeyMapping(
        string ConstraintName,
        int Ordinal,
        string LocalColumn,
        string ReferencedSchema,
        string ReferencedObject,
        string ReferencedColumn);
}
