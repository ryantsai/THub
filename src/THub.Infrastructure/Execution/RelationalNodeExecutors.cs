using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Domain.Connections;
using THub.Domain.Workflows;
using THub.Infrastructure.Connections;

namespace THub.Infrastructure.Execution;

public abstract class RelationalSourceNodeExecutor(
    ExecutionConnectionResolver connectionResolver,
    WorkflowNodeSettingsValidator settingsValidator,
    RelationalConnectionFactory connectionFactory,
    WorkflowNodeKind nodeKind,
    ConnectionKind connectionKind) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Source(nodeKind);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (SqlSourceNodeSettings)settingsValidator.Parse(context.Node);
        var configuration = await connectionResolver.ResolveAsync<RelationalDatabaseConnectionConfiguration>(
            settings.ConnectionId,
            connectionKind,
            cancellationToken);
        await using var metadataConnection = await connectionFactory.CreateAsync(
            configuration,
            cancellationToken);
        var metadata = await RelationalExecutionSupport.LoadMetadataAsync(
            metadataConnection,
            configuration,
            settings.Schema,
            settings.Object,
            cancellationToken);
        var selected = RelationalExecutionSupport.SelectColumns(metadata, settings.Columns);
        var schema = new TabularSchema(selected.Select(column =>
            new TabularColumn(column.Name, column.DataType, column.IsNullable)));
        var batchSize = Math.Min(
            settings.BatchSize,
            Math.Min(configuration.MaximumBatchRows, context.Limits.MaximumRowsPerBatch));
        return WorkflowNodeExecutionResult.WithOutput(
            schema,
            ReadAsync(
                configuration,
                settings,
                selected,
                schema,
                batchSize,
                context,
                connectionFactory,
                cancellationToken));
    }

    private static async IAsyncEnumerable<TabularBatch> ReadAsync(
        RelationalDatabaseConnectionConfiguration configuration,
        SqlSourceNodeSettings settings,
        IReadOnlyList<RelationalColumnMetadata> columns,
        TabularSchema schema,
        int batchSize,
        WorkflowNodeExecutionContext context,
        RelationalConnectionFactory connectionFactory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateAsync(configuration, cancellationToken);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = configuration.CommandTimeoutSeconds;
        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(column => RelationalExecutionSupport.Quote(configuration.Kind, column.Name)))} FROM {RelationalExecutionSupport.QualifiedName(configuration.Kind, settings.Schema, settings.Object)}";
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken);
        var rows = new List<TabularRow>(batchSize);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new TabularValue[columns.Count];
            for (var index = 0; index < columns.Count; index++)
            {
                values[index] = await reader.IsDBNullAsync(index, cancellationToken)
                    ? TabularValue.Null
                    : SqlExecutionSupport.FromSqlValue(
                        reader.GetValue(index),
                        schema.Columns[index],
                        context.Limits);
            }
            rows.Add(new TabularRow(values));
            if (rows.Count == batchSize)
            {
                yield return await ReportAsync(rows, context, cancellationToken);
                rows = new(batchSize);
            }
        }
        if (rows.Count > 0)
        {
            yield return await ReportAsync(rows, context, cancellationToken);
        }
    }

    private static async Task<TabularBatch> ReportAsync(
        IReadOnlyList<TabularRow> rows,
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var batch = new TabularBatch(rows);
        await context.Progress.ReportAsync(
            new WorkflowNodeProgress(
                RowsRead: batch.Rows.Count,
                BatchesProcessed: 1,
                BytesRead: batch.EstimatedByteCount),
            cancellationToken);
        return batch;
    }
}

public sealed class MySqlSourceNodeExecutor(
    ExecutionConnectionResolver resolver,
    WorkflowNodeSettingsValidator validator,
    RelationalConnectionFactory factory)
    : RelationalSourceNodeExecutor(
        resolver, validator, factory, WorkflowNodeKind.MySqlSource, ConnectionKind.MySql);

public sealed class PostgreSqlSourceNodeExecutor(
    ExecutionConnectionResolver resolver,
    WorkflowNodeSettingsValidator validator,
    RelationalConnectionFactory factory)
    : RelationalSourceNodeExecutor(
        resolver, validator, factory, WorkflowNodeKind.PostgreSqlSource, ConnectionKind.PostgreSql);

public sealed class OracleSourceNodeExecutor(
    ExecutionConnectionResolver resolver,
    WorkflowNodeSettingsValidator validator,
    RelationalConnectionFactory factory)
    : RelationalSourceNodeExecutor(
        resolver, validator, factory, WorkflowNodeKind.OracleSource, ConnectionKind.Oracle);

public abstract class RelationalTargetNodeExecutor(
    ExecutionConnectionResolver connectionResolver,
    WorkflowNodeSettingsValidator settingsValidator,
    RelationalConnectionFactory connectionFactory,
    IWorkflowExpressionSessionFactory? expressionSessionFactory,
    WorkflowNodeKind nodeKind,
    ConnectionKind connectionKind) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Target(nodeKind);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (SqlTargetNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context).DataSet;
        var configuration = await connectionResolver.ResolveAsync<RelationalDatabaseConnectionConfiguration>(
            settings.ConnectionId,
            connectionKind,
            cancellationToken);
        await using var connection = await connectionFactory.CreateAsync(configuration, cancellationToken);
        var metadata = await RelationalExecutionSupport.LoadMetadataAsync(
            connection,
            configuration,
            settings.Schema,
            settings.Object,
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
            settings.Bindings,
            context,
            expressionSession);
        await InsertAsync(
            connection,
            configuration,
            settings,
            input,
            mappings,
            context,
            cancellationToken);
        return WorkflowNodeExecutionResult.WithoutOutput;
    }

    private static IReadOnlyList<RelationalTargetMapping> BuildMappings(
        TabularSchema source,
        IReadOnlyList<RelationalColumnMetadata> target,
        IReadOnlyList<WorkflowValueBinding> configured,
        WorkflowNodeExecutionContext context,
        IWorkflowExpressionSession? expressionSession)
    {
        var targetByName = target.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var mappings = new List<RelationalTargetMapping>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (configured.Count == 0)
        {
            foreach (var item in source.Columns.Select((column, index) => (column, index)))
            {
                if (targetByName.TryGetValue(item.column.Name, out var targetColumn) &&
                    targetColumn.CanWrite)
                {
                    mappings.Add(new(
                        targetColumn,
                        (row, _) => row.Values[item.index]));
                    used.Add(targetColumn.Name);
                }
            }
        }
        else
        {
            foreach (var binding in configured)
            {
                if (!targetByName.TryGetValue(binding.TargetColumn, out var targetColumn) ||
                    !targetColumn.CanWrite || !used.Add(targetColumn.Name))
                {
                    throw ExecutionFailure.Configuration(
                        "execution.database.target.mapping",
                        "A configured database target mapping is missing, generated, or duplicated.");
                }
                mappings.Add(new(
                    targetColumn,
                    CreateValueFactory(
                        binding,
                        source,
                        targetColumn,
                        context,
                        expressionSession)));
            }
        }
        return mappings.Count > 0
            ? mappings
            : throw ExecutionFailure.Configuration(
                "execution.database.target.mappings",
                "No writable target columns match the input schema.");
    }

    private static Func<TabularRow, CancellationToken, TabularValue> CreateValueFactory(
        WorkflowValueBinding binding,
        TabularSchema sourceSchema,
        RelationalColumnMetadata target,
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
                    "execution.database.target.variable",
                    $"Workflow variable '{binding.Value}' was not resolved."),
            WorkflowValueBindingKind.JavaScript when expressionSession is not null =>
                (row, cancellationToken) => expressionSession.Evaluate(
                    binding.Value,
                    sourceSchema,
                    row,
                    target.DataType,
                    cancellationToken),
            _ => throw ExecutionFailure.Configuration(
                "execution.database.target.binding",
                "A database target value binding is invalid.")
        };

    private static Func<TabularRow, CancellationToken, TabularValue> CreateColumnFactory(
        TabularSchema sourceSchema,
        string sourceColumn)
    {
        var sourceIndex = TabularExecutionSupport.FindColumn(sourceSchema, sourceColumn);
        return (row, _) => row.Values[sourceIndex];
    }

    private static async Task InsertAsync(
        DbConnection connection,
        RelationalDatabaseConnectionConfiguration configuration,
        SqlTargetNodeSettings settings,
        ITabularDataSet input,
        IReadOnlyList<RelationalTargetMapping> mappings,
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = configuration.CommandTimeoutSeconds;
            var placeholders = mappings.Select((_, index) =>
                configuration.Kind == ConnectionKind.Oracle ? $":p{index}" : $"@p{index}").ToArray();
            command.CommandText =
                $"INSERT INTO {RelationalExecutionSupport.QualifiedName(configuration.Kind, settings.Schema, settings.Object)} ({string.Join(", ", mappings.Select(mapping => RelationalExecutionSupport.Quote(configuration.Kind, mapping.Target.Name)))}) VALUES ({string.Join(", ", placeholders)})";
            for (var index = 0; index < mappings.Count; index++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"p{index}";
                command.Parameters.Add(parameter);
            }
            await foreach (var batch in input.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
            {
                await using (batch.ConfigureAwait(false))
                {
                    foreach (var row in batch.Rows)
                    {
                        for (var index = 0; index < mappings.Count; index++)
                        {
                            command.Parameters[index].Value = TabularExecutionSupport.ToProviderValue(
                                mappings[index].ValueFactory(row, cancellationToken));
                        }
                        _ = await command.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await context.Progress.ReportAsync(
                        new WorkflowNodeProgress(
                            RowsRead: batch.Rows.Count,
                            RowsWritten: batch.Rows.Count,
                            BatchesProcessed: 1,
                            BytesRead: batch.EstimatedByteCount),
                        cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private sealed record RelationalTargetMapping(
        RelationalColumnMetadata Target,
        Func<TabularRow, CancellationToken, TabularValue> ValueFactory);
}

public sealed class MySqlTargetNodeExecutor(
    ExecutionConnectionResolver resolver,
    WorkflowNodeSettingsValidator validator,
    RelationalConnectionFactory factory,
    IWorkflowExpressionSessionFactory? expressionSessionFactory = null)
    : RelationalTargetNodeExecutor(
        resolver,
        validator,
        factory,
        expressionSessionFactory,
        WorkflowNodeKind.MySqlTarget,
        ConnectionKind.MySql);

public sealed class PostgreSqlTargetNodeExecutor(
    ExecutionConnectionResolver resolver,
    WorkflowNodeSettingsValidator validator,
    RelationalConnectionFactory factory,
    IWorkflowExpressionSessionFactory? expressionSessionFactory = null)
    : RelationalTargetNodeExecutor(
        resolver,
        validator,
        factory,
        expressionSessionFactory,
        WorkflowNodeKind.PostgreSqlTarget,
        ConnectionKind.PostgreSql);

public sealed class OracleTargetNodeExecutor(
    ExecutionConnectionResolver resolver,
    WorkflowNodeSettingsValidator validator,
    RelationalConnectionFactory factory,
    IWorkflowExpressionSessionFactory? expressionSessionFactory = null)
    : RelationalTargetNodeExecutor(
        resolver,
        validator,
        factory,
        expressionSessionFactory,
        WorkflowNodeKind.OracleTarget,
        ConnectionKind.Oracle);

internal sealed record RelationalColumnMetadata(
    string Name,
    string SourceTypeName,
    TabularDataType DataType,
    bool IsNullable,
    bool CanWrite);

internal static class RelationalExecutionSupport
{
    public static async Task<IReadOnlyList<RelationalColumnMetadata>> LoadMetadataAsync(
        DbConnection connection,
        RelationalDatabaseConnectionConfiguration configuration,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.CommandTimeout = configuration.CommandTimeoutSeconds;
        command.CommandText =
            $"SELECT * FROM {QualifiedName(configuration.Kind, schema, objectName)} WHERE 1 = 0";
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo,
            cancellationToken);
        var schemaColumns = await reader.GetColumnSchemaAsync(cancellationToken);
        var columns = schemaColumns.Select(column => new RelationalColumnMetadata(
            column.ColumnName ?? throw new InvalidOperationException("A database column has no name."),
            column.DataTypeName ?? column.DataType?.Name ?? "unknown",
            ToTabularType(column.DataType),
            column.AllowDBNull ?? true,
            column.IsReadOnly != true && column.IsAutoIncrement != true)).ToArray();
        return columns.Length > 0
            ? columns
            : throw ExecutionFailure.Configuration(
                "execution.database.object.not_found",
                "The configured database object does not exist or exposes no columns.");
    }

    public static IReadOnlyList<RelationalColumnMetadata> SelectColumns(
        IReadOnlyList<RelationalColumnMetadata> metadata,
        IReadOnlyList<string>? configured)
    {
        if (configured is null)
        {
            return metadata;
        }
        var byName = metadata.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        return configured.Select(name => byName.TryGetValue(name, out var column)
            ? column
            : throw ExecutionFailure.Configuration(
                "execution.database.source.column",
                $"Configured source column '{name}' does not exist.")).ToArray();
    }

    public static string QualifiedName(ConnectionKind kind, string schema, string objectName) =>
        $"{Quote(kind, schema)}.{Quote(kind, objectName)}";

    public static string Quote(ConnectionKind kind, string identifier) => kind switch
    {
        ConnectionKind.MySql => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`",
        ConnectionKind.PostgreSql or ConnectionKind.Oracle =>
            $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static TabularDataType ToTabularType(Type? type)
    {
        type = Nullable.GetUnderlyingType(type ?? typeof(string)) ?? type;
        if (type == typeof(bool)) return TabularDataType.Boolean;
        if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long)) return TabularDataType.Int64;
        if (type == typeof(decimal)) return TabularDataType.Decimal;
        if (type == typeof(float) || type == typeof(double)) return TabularDataType.Double;
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return TabularDataType.DateTimeOffset;
        if (type == typeof(Guid)) return TabularDataType.Guid;
        if (type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>)) return TabularDataType.Binary;
        return TabularDataType.String;
    }
}
