using System.Globalization;
using Microsoft.EntityFrameworkCore;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Domain.Connections;
using THub.Domain.Runs;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Execution;

public sealed class ExecutionConnectionResolver(
    IDbContextFactory<THubDbContext> contextFactory,
    ConnectionConfigurationSerializer configurationSerializer)
{
    public async Task<TConfiguration> ResolveAsync<TConfiguration>(
        Guid connectionId,
        ConnectionKind expectedKind,
        CancellationToken cancellationToken)
        where TConfiguration : ConnectionConfiguration
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await db.Connections
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            throw ExecutionFailure.Configuration(
                "execution.connection.not_found",
                "The configured data connection was not found.");
        }

        if (!connection.IsEnabled)
        {
            throw ExecutionFailure.Configuration(
                "execution.connection.disabled",
                "The configured data connection is disabled.");
        }

        if (connection.Kind != expectedKind)
        {
            throw ExecutionFailure.Configuration(
                "execution.connection.kind",
                "The configured data connection has the wrong connector kind.");
        }

        try
        {
            return configurationSerializer.Deserialize(connection) is TConfiguration typed
                ? typed
                : throw new InvalidOperationException("The connection configuration type is invalid.");
        }
        catch (Exception exception) when (exception is ConnectionConfigurationException
            or InvalidOperationException)
        {
            throw ExecutionFailure.Configuration(
                "execution.connection.configuration",
                "The configured data connection is invalid.",
                exception);
        }
    }
}

internal static class ExecutionFailure
{
    public static WorkflowNodeExecutionException Configuration(
        string code,
        string summary,
        Exception? exception = null) => Create(
            code,
            ExecutionErrorCategory.Configuration,
            summary,
            isRetryable: false,
            exception);

    public static WorkflowNodeExecutionException Data(
        string code,
        string summary,
        Exception? exception = null) => Create(
            code,
            ExecutionErrorCategory.Data,
            summary,
            isRetryable: false,
            exception);

    public static WorkflowNodeExecutionException Connectivity(
        string code,
        string summary,
        bool isRetryable,
        Exception? exception = null) => Create(
            code,
            ExecutionErrorCategory.Connectivity,
            summary,
            isRetryable,
            exception);

    public static WorkflowNodeExecutionException ExternalSideEffect(
        string code,
        string summary,
        Exception? exception = null) => Create(
            code,
            ExecutionErrorCategory.ExternalSideEffect,
            summary,
            isRetryable: false,
            exception);

    private static WorkflowNodeExecutionException Create(
        string code,
        ExecutionErrorCategory category,
        string summary,
        bool isRetryable,
        Exception? exception)
    {
        var error = new ExecutionError(code, category, summary, isRetryable);
        return exception is null
            ? new WorkflowNodeExecutionException(error)
            : new WorkflowNodeExecutionException(error, exception);
    }
}

internal static class TabularExecutionSupport
{
    public static WorkflowNodeInput RequireSingleInput(WorkflowNodeExecutionContext context)
    {
        if (context.Inputs.Count != 1)
        {
            throw ExecutionFailure.Configuration(
                "execution.input.count",
                "This node requires exactly one tabular input.");
        }

        return context.Inputs[0];
    }

    public static int FindColumn(TabularSchema schema, string name)
    {
        for (var index = 0; index < schema.Columns.Count; index++)
        {
            if (string.Equals(schema.Columns[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw ExecutionFailure.Configuration(
            "execution.column.not_found",
            $"Configured column '{name}' does not exist in the input schema.");
    }

    public static TabularValue ParseText(
        string? text,
        TabularColumn column,
        int maximumStringCharacters)
    {
        if (text is null || (text.Length == 0 && column.DataType != TabularDataType.String))
        {
            if (!column.IsNullable)
            {
                throw ExecutionFailure.Data(
                    "execution.value.null",
                    $"Column '{column.Name}' does not allow an empty value.");
            }

            return TabularValue.Null;
        }

        try
        {
            return column.DataType switch
            {
                TabularDataType.Boolean => TabularValue.From(bool.Parse(text)),
                TabularDataType.Int64 => TabularValue.From(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                TabularDataType.Decimal => TabularValue.From(decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)),
                TabularDataType.Double => TabularValue.From(double.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture)),
                TabularDataType.String when text.Length <= maximumStringCharacters => TabularValue.From(text),
                TabularDataType.String => throw new FormatException("The string exceeds its configured bound."),
                TabularDataType.DateTimeOffset => TabularValue.From(DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
                TabularDataType.Guid => TabularValue.From(Guid.Parse(text)),
                TabularDataType.Binary => TabularValue.From(Convert.FromBase64String(text)),
                _ => throw new ArgumentOutOfRangeException(nameof(column))
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw ExecutionFailure.Data(
                "execution.value.format",
                $"A value in column '{column.Name}' does not match its configured type.",
                exception);
        }
    }

    public static object? ToProviderValue(TabularValue value) => value.Kind switch
    {
        TabularValueKind.Null => DBNull.Value,
        TabularValueKind.Binary => ((ReadOnlyMemory<byte>)value.Value!).ToArray(),
        TabularValueKind.DateTimeOffset => value.Value,
        _ => value.Value
    };

    public static string ToInvariantText(TabularValue value) => value.Kind switch
    {
        TabularValueKind.Null => string.Empty,
        TabularValueKind.Boolean => ((bool)value.Value!).ToString(CultureInfo.InvariantCulture),
        TabularValueKind.Int64 => ((long)value.Value!).ToString(CultureInfo.InvariantCulture),
        TabularValueKind.Decimal => ((decimal)value.Value!).ToString(CultureInfo.InvariantCulture),
        TabularValueKind.Double => ((double)value.Value!).ToString("R", CultureInfo.InvariantCulture),
        TabularValueKind.String => (string)value.Value!,
        TabularValueKind.DateTimeOffset => ((DateTimeOffset)value.Value!).ToString("O", CultureInfo.InvariantCulture),
        TabularValueKind.Guid => ((Guid)value.Value!).ToString("D"),
        TabularValueKind.Binary => Convert.ToBase64String(((ReadOnlyMemory<byte>)value.Value!).Span),
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
