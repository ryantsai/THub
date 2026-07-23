using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Jint;
using Jint.Native;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Domain.Connections;
using THub.Domain.Workflows;
using THub.Infrastructure.Connections;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Execution;

public sealed class InfrastructureWorkflowDatabaseVariableProvider(
    IDbContextFactory<THubDbContext> contextFactory,
    ConnectionConfigurationSerializer serializer,
    SqlServerConnectionStringFactory sqlServerConnectionFactory,
    RelationalConnectionFactory relationalConnectionFactory)
    : IWorkflowDatabaseVariableProvider
{
    public async Task<TabularValue> ResolveAsync(
        WorkflowVariable variable,
        CancellationToken cancellationToken)
    {
        if (variable.ConnectionId is null)
        {
            throw ExecutionFailure.Configuration(
                "execution.variable.connection.required",
                "A database workflow variable has no approved connection.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await db.Connections
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == variable.ConnectionId.Value,
                cancellationToken);
        if (connection is null || !connection.IsEnabled || !IsRelational(connection.Kind))
        {
            throw ExecutionFailure.Configuration(
                "execution.variable.connection.invalid",
                "A database workflow variable references a missing, disabled, or non-relational connection.");
        }

        return connection.Kind == ConnectionKind.SqlServer
            ? await ResolveSqlServerAsync(connection, variable, cancellationToken)
                .ConfigureAwait(false)
            : await ResolveRelationalAsync(connection, variable, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<TabularValue> ResolveSqlServerAsync(
        DataConnection connection,
        WorkflowVariable variable,
        CancellationToken cancellationToken)
    {
        var configuration = (SqlServerConnectionConfiguration)serializer.Deserialize(connection);
        var connectionString = (await sqlServerConnectionFactory.CreateAsync(
            configuration,
            "THub workflow variable lookup",
            ApplicationIntent.ReadOnly,
            enlist: false,
            cancellationToken).ConfigureAwait(false)).ConnectionString;
        var metadata = await SqlExecutionSupport.LoadObjectMetadataAsync(
            connectionString,
            configuration.CommandTimeoutSeconds,
            variable.Schema!,
            variable.Object!,
            allowView: true,
            cancellationToken).ConfigureAwait(false);
        EnsureColumns(metadata.Select(column => column.Name), variable);

        await using var database = new SqlConnection(connectionString);
        if (database.State != ConnectionState.Open)
        {
            await database.OpenAsync(cancellationToken);
        }
        await using var command = database.CreateCommand();
        command.CommandTimeout = configuration.CommandTimeoutSeconds;
        command.CommandText =
            $"SELECT TOP (2) {SqlExecutionSupport.Quote(variable.ValueColumn!)} " +
            $"FROM {SqlExecutionSupport.Quote(variable.Schema!)}.{SqlExecutionSupport.Quote(variable.Object!)} " +
            $"WHERE {SqlExecutionSupport.Quote(variable.FilterColumn!)} = @filter;";
        _ = command.Parameters.Add(new SqlParameter("@filter", variable.FilterValue));
        return await ReadSingleAsync(command, variable, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TabularValue> ResolveRelationalAsync(
        DataConnection connection,
        WorkflowVariable variable,
        CancellationToken cancellationToken)
    {
        var configuration =
            (RelationalDatabaseConnectionConfiguration)serializer.Deserialize(connection);
        await using var database = await relationalConnectionFactory.CreateAsync(
            configuration,
            cancellationToken).ConfigureAwait(false);
        var metadata = await RelationalExecutionSupport.LoadMetadataAsync(
            database,
            configuration,
            variable.Schema!,
            variable.Object!,
            cancellationToken).ConfigureAwait(false);
        EnsureColumns(metadata.Select(column => column.Name), variable);

        if (database.State != ConnectionState.Open)
        {
            await database.OpenAsync(cancellationToken);
        }
        await using var command = database.CreateCommand();
        command.CommandTimeout = configuration.CommandTimeoutSeconds;
        var parameterMarker = configuration.Kind == ConnectionKind.Oracle ? ":filter" : "@filter";
        var limit = configuration.Kind switch
        {
            ConnectionKind.MySql or ConnectionKind.PostgreSql => " LIMIT 2",
            ConnectionKind.Oracle => " FETCH FIRST 2 ROWS ONLY",
            _ => string.Empty
        };
        command.CommandText =
            $"SELECT {RelationalExecutionSupport.Quote(configuration.Kind, variable.ValueColumn!)} " +
            $"FROM {RelationalExecutionSupport.QualifiedName(configuration.Kind, variable.Schema!, variable.Object!)} " +
            $"WHERE {RelationalExecutionSupport.Quote(configuration.Kind, variable.FilterColumn!)} = {parameterMarker}{limit}";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "filter";
        parameter.Value = variable.FilterValue!;
        _ = command.Parameters.Add(parameter);
        return await ReadSingleAsync(command, variable, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TabularValue> ReadSingleAsync(
        DbCommand command,
        WorkflowVariable variable,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleResult,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw ExecutionFailure.Data(
                "execution.variable.database.not_found",
                $"Database workflow variable '{variable.Name}' returned no row.");
        }

        var value = await reader.IsDBNullAsync(0, cancellationToken)
            ? TabularValue.Null
            : ConvertValue(reader.GetValue(0), variable);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw ExecutionFailure.Data(
                "execution.variable.database.multiple",
                $"Database workflow variable '{variable.Name}' returned more than one row.");
        }

        return value;
    }

    private static TabularValue ConvertValue(object value, WorkflowVariable variable)
    {
        try
        {
            return variable.DataType switch
            {
                WorkflowValueType.Boolean => TabularValue.From(
                    Convert.ToBoolean(value, CultureInfo.InvariantCulture)),
                WorkflowValueType.Int64 => TabularValue.From(
                    Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                WorkflowValueType.Decimal => TabularValue.From(
                    Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
                WorkflowValueType.Double => TabularValue.From(
                    Convert.ToDouble(value, CultureInfo.InvariantCulture)),
                WorkflowValueType.String => TabularValue.From(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
                WorkflowValueType.DateTimeOffset => TabularValue.From(value switch
                {
                    DateTimeOffset typed => typed,
                    DateTime typed => new DateTimeOffset(
                        DateTime.SpecifyKind(typed, DateTimeKind.Utc)),
                    _ => DateTimeOffset.Parse(
                        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)
                }),
                WorkflowValueType.Guid => TabularValue.From(value is Guid guid
                    ? guid
                    : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)
                        ?? string.Empty)),
                _ => throw new ArgumentOutOfRangeException(nameof(variable))
            };
        }
        catch (Exception exception) when (exception is FormatException
            or InvalidCastException or OverflowException)
        {
            throw ExecutionFailure.Data(
                "execution.variable.database.type",
                $"Database workflow variable '{variable.Name}' does not match its configured type.",
                exception);
        }
    }

    private static void EnsureColumns(
        IEnumerable<string> columns,
        WorkflowVariable variable)
    {
        var names = columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(variable.ValueColumn!)
            || !names.Contains(variable.FilterColumn!))
        {
            throw ExecutionFailure.Configuration(
                "execution.variable.database.column",
                $"Database workflow variable '{variable.Name}' references a missing column.");
        }
    }

    private static bool IsRelational(ConnectionKind kind) => kind is
        ConnectionKind.SqlServer or ConnectionKind.MySql
            or ConnectionKind.PostgreSql or ConnectionKind.Oracle;
}

public sealed class JintWorkflowExpressionSessionFactory : IWorkflowExpressionSessionFactory
{
    public IWorkflowExpressionSession Create(
        IReadOnlyList<WorkflowFunction> functions,
        IReadOnlyDictionary<string, TabularValue> variables,
        CancellationToken cancellationToken) =>
        new JintWorkflowExpressionSession(functions, variables, cancellationToken);

    public void Validate(IReadOnlyList<WorkflowFunction> functions)
    {
        using var session = new JintWorkflowExpressionSession(
            functions,
            new Dictionary<string, TabularValue>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
    }

    public void ValidateExpression(string expression)
    {
        _ = Engine.PrepareScript($"({expression});");
    }
}

internal sealed class JintWorkflowExpressionSession : IWorkflowExpressionSession
{
    private readonly Engine engine;

    public JintWorkflowExpressionSession(
        IReadOnlyList<WorkflowFunction> functions,
        IReadOnlyDictionary<string, TabularValue> variables,
        CancellationToken cancellationToken)
    {
        engine = new Engine(options => options
            .Strict()
            .LimitMemory(2_000_000)
            .TimeoutInterval(TimeSpan.FromMilliseconds(50))
            .MaxStatements(1_000)
            .LimitRecursion(32)
            .DisableStringCompilation()
            .CancellationToken(cancellationToken));
        engine.SetValue("vars", ParseFrozenObject(ToJson(variables)));
        foreach (var function in functions)
        {
            var parameters = string.Join(",", function.Parameters);
            _ = engine.Execute(
                $"function {function.Name}({parameters}){{return ({function.Expression});}}");
        }
    }

    public TabularValue Evaluate(
        string expression,
        TabularSchema rowSchema,
        TabularRow row,
        TabularDataType targetType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var rowValues = rowSchema.Columns
                .Select((column, index) => new KeyValuePair<string, TabularValue>(
                    column.Name,
                    row.Values[index]))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            engine.SetValue("row", ParseFrozenObject(ToJson(rowValues)));
            return ConvertResult(engine.Evaluate($"({expression})"), targetType);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not OutOfMemoryException)
        {
            throw ExecutionFailure.Data(
                "execution.javascript.failed",
                "A bounded JavaScript value expression failed.",
                exception);
        }
    }

    public void Dispose()
    {
    }

    private JsValue ParseFrozenObject(string json)
    {
        var encoded = JsonSerializer.Serialize(json);
        return engine.Evaluate($"Object.freeze(JSON.parse({encoded}))");
    }

    private static string ToJson(IReadOnlyDictionary<string, TabularValue> values)
    {
        var serializable = values.ToDictionary(
            pair => pair.Key,
            pair => ToObject(pair.Value),
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(serializable);
    }

    private static object? ToObject(TabularValue value) => value.Kind switch
    {
        TabularValueKind.Null => null,
        TabularValueKind.Binary => Convert.ToBase64String(
            ((ReadOnlyMemory<byte>)value.Value!).Span),
        TabularValueKind.DateTimeOffset => ((DateTimeOffset)value.Value!).ToString("O"),
        TabularValueKind.Guid => ((Guid)value.Value!).ToString("D"),
        _ => value.Value
    };

    private static TabularValue ConvertResult(JsValue value, TabularDataType targetType)
    {
        if (value.IsNull() || value.IsUndefined())
        {
            return TabularValue.Null;
        }

        return targetType switch
        {
            TabularDataType.Boolean => TabularValue.From(value.AsBoolean()),
            TabularDataType.Int64 => TabularValue.From(checked((long)value.AsNumber())),
            TabularDataType.Decimal => TabularValue.From(decimal.Parse(
                value.IsString() ? value.AsString() : value.AsNumber().ToString("R", CultureInfo.InvariantCulture),
                NumberStyles.Number | NumberStyles.Float,
                CultureInfo.InvariantCulture)),
            TabularDataType.Double => TabularValue.From(value.AsNumber()),
            TabularDataType.String => TabularValue.From(value.IsString()
                ? value.AsString()
                : value.ToString()),
            TabularDataType.DateTimeOffset => TabularValue.From(DateTimeOffset.Parse(
                value.AsString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind)),
            TabularDataType.Guid => TabularValue.From(Guid.Parse(value.AsString())),
            TabularDataType.Binary => TabularValue.From(Convert.FromBase64String(value.AsString())),
            _ => throw new ArgumentOutOfRangeException(nameof(targetType))
        };
    }
}
