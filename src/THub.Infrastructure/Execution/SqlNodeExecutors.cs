using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Domain.Connections;
using THub.Domain.Runs;
using THub.Domain.Workflows;
using THub.Infrastructure.Connections;

namespace THub.Infrastructure.Execution;

public sealed class SqlSourceNodeExecutor(
    ExecutionConnectionResolver connectionResolver,
    WorkflowNodeSettingsValidator settingsValidator,
    SqlServerConnectionStringFactory connectionStringFactory) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Source(WorkflowNodeKind.SqlSource);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (SqlSourceNodeSettings)settingsValidator.Parse(context.Node);
        var connection = await connectionResolver.ResolveAsync<SqlServerConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.SqlServer,
            cancellationToken);
        var connectionString = (await connectionStringFactory.CreateAsync(
            connection,
            "THub workflow execution",
            ApplicationIntent.ReadWrite,
            enlist: true,
            cancellationToken)).ConnectionString;
        var metadata = await SqlExecutionSupport.LoadObjectMetadataAsync(
            connectionString,
            connection.CommandTimeoutSeconds,
            settings.Schema,
            settings.Object,
            allowView: true,
            cancellationToken);
        var selected = SqlExecutionSupport.SelectColumns(metadata, settings.Columns);
        var schema = new TabularSchema(selected.Select(column =>
            new TabularColumn(column.Name, column.DataType, column.IsNullable)));
        var batchSize = Math.Min(
            settings.BatchSize,
            Math.Min(connection.MaximumBatchRows, context.Limits.MaximumRowsPerBatch));
        return WorkflowNodeExecutionResult.WithOutput(
            schema,
            ReadAsync(
                connectionString,
                connection.CommandTimeoutSeconds,
                settings,
                selected,
                schema,
                batchSize,
                context,
                cancellationToken));
    }

    private static async IAsyncEnumerable<TabularBatch> ReadAsync(
        string connectionString,
        int commandTimeoutSeconds,
        SqlSourceNodeSettings settings,
        IReadOnlyList<SqlColumnMetadata> columns,
        TabularSchema schema,
        int batchSize,
        WorkflowNodeExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await SqlExecutionSupport.OpenSourceAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(column => SqlExecutionSupport.Quote(column.Name)))} FROM {SqlExecutionSupport.Quote(settings.Schema)}.{SqlExecutionSupport.Quote(settings.Object)};";
        await using var reader = await SqlExecutionSupport.ExecuteSourceReaderAsync(
            command,
            cancellationToken);
        var rows = new List<TabularRow>(batchSize);
        while (await SqlExecutionSupport.ReadSourceAsync(reader, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new TabularValue[columns.Count];
            for (var index = 0; index < columns.Count; index++)
            {
                values[index] = reader.IsDBNull(index)
                    ? TabularValue.Null
                    : SqlExecutionSupport.FromSqlValue(
                        reader.GetValue(index),
                        schema.Columns[index],
                        context.Limits);
            }

            rows.Add(new TabularRow(values));
            if (rows.Count == batchSize)
            {
                var batch = new TabularBatch(rows);
                await context.Progress.ReportAsync(
                    new WorkflowNodeProgress(
                        RowsRead: batch.Rows.Count,
                        BatchesProcessed: 1,
                        BytesRead: batch.EstimatedByteCount),
                    cancellationToken);
                yield return batch;
                rows = new(batchSize);
            }
        }

        if (rows.Count > 0)
        {
            var batch = new TabularBatch(rows);
            await context.Progress.ReportAsync(
                new WorkflowNodeProgress(
                    RowsRead: batch.Rows.Count,
                    BatchesProcessed: 1,
                    BytesRead: batch.EstimatedByteCount),
                cancellationToken);
            yield return batch;
        }
    }
}

public sealed class SqlTargetNodeExecutor(
    ExecutionConnectionResolver connectionResolver,
    WorkflowNodeSettingsValidator settingsValidator,
    SqlServerConnectionStringFactory connectionStringFactory,
    IWorkflowExpressionSessionFactory? expressionSessionFactory = null) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Target(WorkflowNodeKind.SqlTarget);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (SqlTargetNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context).DataSet;
        var connection = await connectionResolver.ResolveAsync<SqlServerConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.SqlServer,
            cancellationToken);
        var connectionString = (await connectionStringFactory.CreateAsync(
            connection,
            "THub workflow execution",
            ApplicationIntent.ReadWrite,
            enlist: true,
            cancellationToken)).ConnectionString;
        var metadata = await SqlExecutionSupport.LoadObjectMetadataAsync(
            connectionString,
            connection.CommandTimeoutSeconds,
            settings.Schema,
            settings.Object,
            allowView: false,
            cancellationToken);
        using var expressionSession = settings.Bindings.Any(binding =>
            binding.Kind == WorkflowValueBindingKind.JavaScript)
            ? (expressionSessionFactory ?? throw ExecutionFailure.Configuration(
                "execution.javascript.unavailable",
                "JavaScript value expressions are not available in this host.")).Create(
                context.Functions,
                context.Variables,
                cancellationToken)
            : null;
        var mappings = BuildMappings(
            input.Schema,
            metadata,
            settings,
            context,
            expressionSession);
        await WriteAsync(
            connectionString,
            connection.CommandTimeoutSeconds,
            settings,
            input,
            mappings,
            context,
            cancellationToken);
        return WorkflowNodeExecutionResult.WithoutOutput;
    }

    private static IReadOnlyList<SqlTargetMapping> BuildMappings(
        TabularSchema sourceSchema,
        IReadOnlyList<SqlColumnMetadata> targetColumns,
        SqlTargetNodeSettings settings,
        WorkflowNodeExecutionContext context,
        IWorkflowExpressionSession? expressionSession)
    {
        ValidateKeyConfiguration(settings, targetColumns);
        var configured = settings.Bindings;
        var keySet = settings.KeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetByName = targetColumns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var mappings = new List<SqlTargetMapping>();
        var mappedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (configured.Count == 0)
        {
            foreach (var source in sourceSchema.Columns.Select((column, index) => (column, index)))
            {
                if (targetByName.TryGetValue(source.column.Name, out var target)
                    && (target.CanWrite || settings.Mode == "delete" && target.IsKey))
                {
                    mappings.Add(new(
                        target,
                        (row, _) => row.Values[source.index]));
                    mappedTargets.Add(target.Name);
                }
            }
        }
        else
        {
            foreach (var binding in configured)
            {
                if (!targetByName.TryGetValue(binding.TargetColumn, out var target))
                {
                    throw ExecutionFailure.Configuration(
                        "execution.sql.target.column",
                        $"Configured target column '{binding.TargetColumn}' does not exist.");
                }

                if (!target.CanWrite
                    && !(settings.Mode == "delete" && target.IsKey))
                {
                    throw ExecutionFailure.Configuration(
                        "execution.sql.target.generated",
                        $"Configured target column '{target.Name}' is identity, computed, or generated.");
                }

                if (!mappedTargets.Add(target.Name))
                {
                    throw ExecutionFailure.Configuration(
                        "execution.sql.target.duplicate",
                        $"Target column '{target.Name}' is mapped more than once.");
                }

                mappings.Add(new(
                    target,
                    CreateValueFactory(
                        binding,
                        sourceSchema,
                        target,
                        context,
                        expressionSession)));
            }
        }

        if (mappings.Count == 0)
        {
            throw ExecutionFailure.Configuration(
                "execution.sql.target.mappings",
                "No writable SQL target columns match the input schema.");
        }

        if (settings.Mode is "upsert" or "delete"
            && settings.KeyColumns.Any(key => !mappedTargets.Contains(key)))
        {
            throw ExecutionFailure.Configuration(
                "execution.sql.target.keys.missing",
                "Every configured target key column must be mapped from an input column.");
        }

        if (settings.Mode == "upsert"
            && mappings.All(mapping => keySet.Contains(mapping.Target.Name)))
        {
            throw ExecutionFailure.Configuration(
                "execution.sql.target.upsert.values",
                "Upsert requires at least one mapped non-key target column.");
        }

        var missingRequired = settings.Mode == "delete"
            ? []
            : targetColumns.Where(column =>
                column.CanWrite
                && !column.IsNullable
                && !column.HasDefault
                && !mappedTargets.Contains(column.Name)).ToArray();
        if (missingRequired.Length > 0)
        {
            throw ExecutionFailure.Configuration(
                "execution.sql.target.required",
                "One or more required SQL target columns are not mapped.");
        }

        return mappings;
    }

    private static void ValidateKeyConfiguration(
        SqlTargetNodeSettings settings,
        IReadOnlyList<SqlColumnMetadata> targetColumns)
    {
        if (settings.Mode == "insert")
        {
            return;
        }

        var primaryKey = targetColumns
            .Where(column => column.IsKey)
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (primaryKey.Count == 0 || !primaryKey.SetEquals(settings.KeyColumns))
        {
            throw ExecutionFailure.Configuration(
                "execution.sql.target.keys",
                "Configured key columns must exactly match the discovered primary key.");
        }
    }

    private static Func<TabularRow, CancellationToken, TabularValue> CreateValueFactory(
        WorkflowValueBinding binding,
        TabularSchema sourceSchema,
        SqlColumnMetadata target,
        WorkflowNodeExecutionContext context,
        IWorkflowExpressionSession? expressionSession) => binding.Kind switch
        {
            WorkflowValueBindingKind.Column => CreateColumnFactory(
                sourceSchema,
                binding.Value),
            WorkflowValueBindingKind.Variable => context.Variables.TryGetValue(
                binding.Value,
                out var value)
                ? (_, _) => value
                : throw ExecutionFailure.Configuration(
                    "execution.sql.target.variable",
                    $"Workflow variable '{binding.Value}' was not resolved."),
            WorkflowValueBindingKind.JavaScript when expressionSession is not null =>
                (row, cancellationToken) => expressionSession.Evaluate(
                    binding.Value,
                    sourceSchema,
                    row,
                    target.DataType,
                    cancellationToken),
            _ => throw ExecutionFailure.Configuration(
                "execution.sql.target.binding",
                "A SQL target value binding is invalid.")
        };

    private static Func<TabularRow, CancellationToken, TabularValue> CreateColumnFactory(
        TabularSchema sourceSchema,
        string sourceColumn)
    {
        var sourceIndex = TabularExecutionSupport.FindColumn(sourceSchema, sourceColumn);
        return (row, _) => row.Values[sourceIndex];
    }

    private static async Task WriteAsync(
        string connectionString,
        int commandTimeoutSeconds,
        SqlTargetNodeSettings settings,
        ITabularDataSet input,
        IReadOnlyList<SqlTargetMapping> mappings,
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = RelationalTargetMutationSql.Build(
                ConnectionKind.SqlServer,
                settings.Schema,
                settings.Object,
                settings.Mode,
                mappings.Select(mapping => mapping.Target.Name).ToArray(),
                settings.KeyColumns);
            for (var index = 0; index < mappings.Count; index++)
            {
                command.Parameters.Add(SqlExecutionSupport.CreateParameter($"@p{index}", mappings[index].Target));
            }

            var keyIndexes = settings.KeyColumns
                .Select(key => mappings
                    .Select((mapping, index) => (mapping, index))
                    .Single(item => string.Equals(
                        item.mapping.Target.Name,
                        key,
                        StringComparison.OrdinalIgnoreCase)).index)
                .ToArray();
            var keyTracker = settings.Mode == "insert"
                ? null
                : new RelationalMutationKeyTracker();
            await foreach (var batch in input.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
            {
                await using (batch.ConfigureAwait(false))
                {
                    long rowsWritten = 0;
                    foreach (var row in batch.Rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var values = new TabularValue[mappings.Count];
                        for (var index = 0; index < mappings.Count; index++)
                        {
                            values[index] = mappings[index].ValueFactory(row, cancellationToken);
                            command.Parameters[index].Value = TabularExecutionSupport.ToProviderValue(
                                values[index]);
                        }

                        keyTracker?.Add(keyIndexes.Select(index => values[index]).ToArray());
                        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                        rowsWritten += settings.Mode == "delete" ? affected : 1;
                    }

                    await context.Progress.ReportAsync(
                        new WorkflowNodeProgress(
                            RowsRead: batch.Rows.Count,
                            RowsWritten: rowsWritten,
                            BatchesProcessed: 1,
                            BytesRead: batch.EstimatedByteCount),
                        cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqlException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw ExecutionFailure.ExternalSideEffect(
                "execution.sql.target.failed",
                "The SQL target mutation failed and its transaction was rolled back.",
                exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private sealed record SqlTargetMapping(
        SqlColumnMetadata Target,
        Func<TabularRow, CancellationToken, TabularValue> ValueFactory);
}

internal sealed record SqlColumnMetadata(
    string Name,
    string SqlTypeName,
    TabularDataType DataType,
    bool IsNullable,
    bool CanWrite,
    bool HasDefault,
    int MaximumLength,
    byte Precision,
    byte Scale,
    bool IsKey);

internal static class SqlExecutionSupport
{
    private const string MetadataSql = """
        SELECT
            columnMetadata.[name],
            typeMetadata.[name],
            columnMetadata.[is_nullable],
            columnMetadata.[is_identity],
            columnMetadata.[is_computed],
            columnMetadata.[generated_always_type],
            CONVERT(bit, CASE WHEN columnMetadata.[default_object_id] = 0 THEN 0 ELSE 1 END),
            columnMetadata.[max_length],
            columnMetadata.[precision],
            columnMetadata.[scale],
            objectMetadata.[type],
            CONVERT(bit, CASE WHEN EXISTS (
                SELECT 1
                FROM sys.indexes AS keyIndex
                INNER JOIN sys.index_columns AS keyColumn
                    ON keyColumn.[object_id] = keyIndex.[object_id]
                    AND keyColumn.[index_id] = keyIndex.[index_id]
                WHERE keyIndex.[object_id] = objectMetadata.[object_id]
                    AND keyIndex.[is_primary_key] = 1
                    AND keyColumn.[column_id] = columnMetadata.[column_id]
            ) THEN 1 ELSE 0 END)
        FROM sys.objects AS objectMetadata
        INNER JOIN sys.schemas AS schemaMetadata ON schemaMetadata.[schema_id] = objectMetadata.[schema_id]
        INNER JOIN sys.columns AS columnMetadata ON columnMetadata.[object_id] = objectMetadata.[object_id]
        INNER JOIN sys.types AS typeMetadata ON typeMetadata.[user_type_id] = columnMetadata.[user_type_id]
        WHERE schemaMetadata.[name] = @schema
            AND objectMetadata.[name] = @object
            AND objectMetadata.[type] IN (N'U', N'V')
        ORDER BY columnMetadata.[column_id];
        """;

    public static async Task OpenSourceAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (SqlException exception)
        {
            throw ToSourceFailure(exception);
        }
    }

    public static async Task<SqlDataReader> ExecuteSourceReaderAsync(
        SqlCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        }
        catch (SqlException exception)
        {
            throw ToSourceFailure(exception);
        }
    }

    public static async Task<bool> ReadSourceAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(cancellationToken);
        }
        catch (SqlException exception)
        {
            throw ToSourceFailure(exception);
        }
    }

    public static async Task<IReadOnlyList<SqlColumnMetadata>> LoadObjectMetadataAsync(
        string connectionString,
        int commandTimeoutSeconds,
        string schema,
        string objectName,
        bool allowView,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = MetadataSql;
            command.CommandTimeout = commandTimeoutSeconds;
            command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = schema });
            command.Parameters.Add(new SqlParameter("@object", SqlDbType.NVarChar, 128) { Value = objectName });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = new List<SqlColumnMetadata>();
            string? objectType = null;
            while (await reader.ReadAsync(cancellationToken))
            {
                objectType ??= reader.GetString(10);
                var sqlType = reader.GetString(1);
                columns.Add(new SqlColumnMetadata(
                    reader.GetString(0),
                    sqlType,
                    ToTabularType(sqlType),
                    reader.GetBoolean(2),
                    !reader.GetBoolean(3) && !reader.GetBoolean(4) && reader.GetByte(5) == 0,
                    reader.GetBoolean(6),
                    reader.GetInt16(7),
                    reader.GetByte(8),
                    reader.GetByte(9),
                    reader.GetBoolean(11)));
            }

            if (columns.Count == 0)
            {
                throw ExecutionFailure.Configuration(
                    "execution.sql.object.not_found",
                    "The configured SQL object does not exist or has no accessible columns.");
            }

            if (!allowView && !string.Equals(objectType?.Trim(), "U", StringComparison.Ordinal))
            {
                throw ExecutionFailure.Configuration(
                    "execution.sql.target.table",
                    "SQL target v1 requires a physical table discovered through metadata.");
            }

            return columns;
        }
        catch (SqlException exception)
        {
            throw ToSourceFailure(exception);
        }
    }

    public static IReadOnlyList<SqlColumnMetadata> SelectColumns(
        IReadOnlyList<SqlColumnMetadata> metadata,
        IReadOnlyList<string>? configured)
    {
        if (configured is null)
        {
            return metadata;
        }

        var byName = metadata.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var selected = new List<SqlColumnMetadata>(configured.Count);
        foreach (var name in configured)
        {
            if (!byName.TryGetValue(name, out var column))
            {
                throw ExecutionFailure.Configuration(
                    "execution.sql.source.column",
                    $"Configured source column '{name}' does not exist.");
            }

            selected.Add(column);
        }

        return selected;
    }

    public static string Quote(string identifier) => new SqlCommandBuilder().QuoteIdentifier(identifier);

    public static TabularValue FromSqlValue(
        object value,
        TabularColumn column,
        TabularExecutionLimits limits)
    {
        try
        {
            return column.DataType switch
            {
                TabularDataType.Boolean => TabularValue.From(Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture)),
                TabularDataType.Int64 => TabularValue.From(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
                TabularDataType.Decimal => TabularValue.From(Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)),
                TabularDataType.Double => TabularValue.From(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
                TabularDataType.String => TabularValue.From(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
                TabularDataType.DateTimeOffset when value is DateTimeOffset offset => TabularValue.From(offset),
                TabularDataType.DateTimeOffset when value is DateTime dateTime => TabularValue.From(new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc))),
                TabularDataType.Guid when value is Guid guid => TabularValue.From(guid),
                TabularDataType.Binary when value is byte[] bytes && bytes.Length <= limits.MaximumBinaryBytesPerValue => TabularValue.From(bytes),
                TabularDataType.Binary => throw new InvalidCastException("The binary value is invalid or exceeds its configured bound."),
                _ => throw new InvalidCastException("The SQL value type is not supported.")
            };
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw ExecutionFailure.Data(
                "execution.sql.value",
                $"A SQL value in column '{column.Name}' could not be converted safely.",
                exception);
        }
    }

    public static SqlParameter CreateParameter(string name, SqlColumnMetadata column)
    {
        var parameter = new SqlParameter(name, ToSqlDbType(column.SqlTypeName));
        if (parameter.SqlDbType is SqlDbType.NVarChar or SqlDbType.VarChar
            or SqlDbType.NChar or SqlDbType.Char
            or SqlDbType.VarBinary or SqlDbType.Binary)
        {
            parameter.Size = column.MaximumLength < 0
                ? -1
                : column.SqlTypeName.StartsWith('n')
                    ? Math.Max(1, column.MaximumLength / 2)
                    : Math.Max(1, column.MaximumLength);
        }

        if (parameter.SqlDbType == SqlDbType.Decimal)
        {
            parameter.Precision = column.Precision;
            parameter.Scale = column.Scale;
        }

        return parameter;
    }

    public static WorkflowNodeExecutionException ToSourceFailure(SqlException exception)
    {
        if (exception.Number == 18456)
        {
            return new WorkflowNodeExecutionException(
                new ExecutionError(
                    "execution.sql.authentication",
                    ExecutionErrorCategory.Authentication,
                    "SQL Server rejected the worker's Windows identity.",
                    isRetryable: false),
                exception);
        }

        return ExecutionFailure.Connectivity(
            "execution.sql.connectivity",
            "SQL Server could not complete the read operation.",
            exception.IsTransient,
            exception);
    }

    private static TabularDataType ToTabularType(string sqlType) => sqlType.ToLowerInvariant() switch
    {
        "bit" => TabularDataType.Boolean,
        "tinyint" or "smallint" or "int" or "bigint" => TabularDataType.Int64,
        "decimal" or "numeric" or "money" or "smallmoney" => TabularDataType.Decimal,
        "real" or "float" => TabularDataType.Double,
        "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml" or "time" => TabularDataType.String,
        "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => TabularDataType.DateTimeOffset,
        "uniqueidentifier" => TabularDataType.Guid,
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => TabularDataType.Binary,
        _ => throw ExecutionFailure.Configuration(
            "execution.sql.type.unsupported",
            $"SQL data type '{sqlType}' is not supported by the v1 tabular contract.")
    };

    private static SqlDbType ToSqlDbType(string sqlType) => sqlType.ToLowerInvariant() switch
    {
        "bit" => SqlDbType.Bit,
        "tinyint" => SqlDbType.TinyInt,
        "smallint" => SqlDbType.SmallInt,
        "int" => SqlDbType.Int,
        "bigint" => SqlDbType.BigInt,
        "decimal" or "numeric" => SqlDbType.Decimal,
        "money" => SqlDbType.Money,
        "smallmoney" => SqlDbType.SmallMoney,
        "real" => SqlDbType.Real,
        "float" => SqlDbType.Float,
        "char" => SqlDbType.Char,
        "nchar" => SqlDbType.NChar,
        "varchar" or "text" => SqlDbType.VarChar,
        "nvarchar" or "ntext" or "xml" => SqlDbType.NVarChar,
        "time" => SqlDbType.Time,
        "date" => SqlDbType.Date,
        "datetime" => SqlDbType.DateTime,
        "datetime2" or "smalldatetime" => SqlDbType.DateTime2,
        "datetimeoffset" => SqlDbType.DateTimeOffset,
        "uniqueidentifier" => SqlDbType.UniqueIdentifier,
        "binary" or "timestamp" or "rowversion" => SqlDbType.Binary,
        "varbinary" or "image" => SqlDbType.VarBinary,
        _ => throw new ArgumentOutOfRangeException(nameof(sqlType))
    };
}
