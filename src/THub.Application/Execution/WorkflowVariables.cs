using System.Globalization;
using THub.Domain.Workflows;

namespace THub.Application.Execution;

public interface IWorkflowDatabaseVariableProvider
{
    Task<TabularValue> ResolveAsync(
        WorkflowVariable variable,
        CancellationToken cancellationToken);
}

public interface IWorkflowVariableResolver
{
    Task<IReadOnlyDictionary<string, TabularValue>> ResolveAsync(
        Guid workflowRunId,
        DateTimeOffset runStartedAtUtc,
        WorkflowGraph graph,
        CancellationToken cancellationToken);
}

public interface IWorkflowExpressionSession : IDisposable
{
    TabularValue Evaluate(
        string expression,
        TabularSchema rowSchema,
        TabularRow row,
        TabularDataType targetType,
        CancellationToken cancellationToken);
}

public interface IWorkflowExpressionSessionFactory
{
    IWorkflowExpressionSession Create(
        IReadOnlyList<WorkflowFunction> functions,
        IReadOnlyDictionary<string, TabularValue> variables,
        CancellationToken cancellationToken);

    void Validate(IReadOnlyList<WorkflowFunction> functions);

    void ValidateExpression(string expression);
}

public sealed class WorkflowVariableResolver(
    IWorkflowDatabaseVariableProvider databaseProvider,
    IWorkflowExpressionSessionFactory? expressionFactory = null) : IWorkflowVariableResolver
{
    public async Task<IReadOnlyDictionary<string, TabularValue>> ResolveAsync(
        Guid workflowRunId,
        DateTimeOffset runStartedAtUtc,
        WorkflowGraph graph,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        expressionFactory?.Validate(graph.Functions);
        var values = new Dictionary<string, TabularValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["runId"] = TabularValue.From(workflowRunId),
            ["runStartedAtUtc"] = TabularValue.From(runStartedAtUtc),
            ["utcToday"] = TabularValue.From(new DateTimeOffset(
                runStartedAtUtc.UtcDateTime.Date,
                TimeSpan.Zero))
        };

        foreach (var variable in graph.Variables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = variable.Kind switch
            {
                WorkflowVariableKind.Literal => ParseLiteral(variable),
                WorkflowVariableKind.Database => await databaseProvider.ResolveAsync(
                    variable,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"Unsupported workflow variable kind '{variable.Kind}'.")
            };
            values.Add(variable.Name, Coerce(value, variable.DataType, variable.Name));
        }

        return values;
    }

    public static TabularValue ParseLiteral(WorkflowVariable variable)
    {
        var value = variable.Value
            ?? throw new InvalidOperationException(
                $"Literal workflow variable '{variable.Name}' has no value.");
        try
        {
            return variable.DataType switch
            {
                WorkflowValueType.Boolean => TabularValue.From(bool.Parse(value)),
                WorkflowValueType.Int64 => TabularValue.From(long.Parse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture)),
                WorkflowValueType.Decimal => TabularValue.From(decimal.Parse(
                    value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture)),
                WorkflowValueType.Double => TabularValue.From(double.Parse(
                    value,
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture)),
                WorkflowValueType.String => TabularValue.From(value),
                WorkflowValueType.DateTimeOffset => TabularValue.From(DateTimeOffset.Parse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)),
                WorkflowValueType.Guid => TabularValue.From(Guid.Parse(value)),
                _ => throw new ArgumentOutOfRangeException(nameof(variable))
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidOperationException(
                $"Workflow variable '{variable.Name}' does not match its configured type.",
                exception);
        }
    }

    public static TabularValue Coerce(
        TabularValue value,
        WorkflowValueType expectedType,
        string variableName)
    {
        if (value.Kind == TabularValueKind.Null)
        {
            return value;
        }

        var matches = expectedType switch
        {
            WorkflowValueType.Boolean => value.Kind == TabularValueKind.Boolean,
            WorkflowValueType.Int64 => value.Kind == TabularValueKind.Int64,
            WorkflowValueType.Decimal => value.Kind == TabularValueKind.Decimal,
            WorkflowValueType.Double => value.Kind == TabularValueKind.Double,
            WorkflowValueType.String => value.Kind == TabularValueKind.String,
            WorkflowValueType.DateTimeOffset => value.Kind == TabularValueKind.DateTimeOffset,
            WorkflowValueType.Guid => value.Kind == TabularValueKind.Guid,
            _ => false
        };
        return matches
            ? value
            : throw new InvalidOperationException(
                $"Database value for workflow variable '{variableName}' does not match its configured type.");
    }
}

internal sealed class UnavailableWorkflowDatabaseVariableProvider
    : IWorkflowDatabaseVariableProvider
{
    public Task<TabularValue> ResolveAsync(
        WorkflowVariable variable,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            $"Database workflow variable '{variable.Name}' cannot be resolved in this host.");
}
