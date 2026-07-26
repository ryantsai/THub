using System.Runtime.CompilerServices;
using THub.Application.Execution;
using THub.Domain.Workflows;

namespace THub.Infrastructure.Execution;

public sealed class DeriveColumnsNodeExecutor(
    WorkflowNodeSettingsValidator settingsValidator,
    IWorkflowExpressionSessionFactory expressionSessionFactory) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Transform(WorkflowNodeKind.DeriveColumns);

    public ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = (DeriveColumnsNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context);
        var names = input.DataSet.Schema.Columns
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in settings.Columns)
        {
            if (!names.Add(column.Name))
            {
                throw ExecutionFailure.Configuration(
                    "execution.derive.column.duplicate",
                    $"Derived column '{column.Name}' duplicates an input column.");
            }
        }

        var schema = new TabularSchema(
            input.DataSet.Schema.Columns.Concat(settings.Columns.Select(column =>
                new TabularColumn(column.Name, column.DataType, column.IsNullable))));
        return ValueTask.FromResult(WorkflowNodeExecutionResult.WithOutput(
            schema,
            DeriveAsync(
                input.DataSet,
                settings.Columns,
                expressionSessionFactory,
                context,
                cancellationToken)));
    }

    private static async IAsyncEnumerable<TabularBatch> DeriveAsync(
        ITabularDataSet input,
        IReadOnlyList<DerivedColumnSettings> columns,
        IWorkflowExpressionSessionFactory expressionSessionFactory,
        WorkflowNodeExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var session = expressionSessionFactory.Create(
            context.Functions,
            context.Variables,
            cancellationToken);
        await foreach (var batch in input.ReadBatchesAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            await using (batch.ConfigureAwait(false))
            {
                var rows = new TabularRow[batch.Rows.Count];
                for (var rowIndex = 0; rowIndex < batch.Rows.Count; rowIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = batch.Rows[rowIndex];
                    var values = new List<TabularValue>(row.Values);
                    foreach (var column in columns)
                    {
                        var value = session.Evaluate(
                            column.Expression,
                            input.Schema,
                            row,
                            column.DataType,
                            cancellationToken);
                        if (value.Kind == TabularValueKind.Null && !column.IsNullable)
                        {
                            throw ExecutionFailure.Data(
                                "execution.derive.value.null",
                                $"Derived column '{column.Name}' does not allow null values.");
                        }

                        values.Add(value);
                    }

                    rows[rowIndex] = new TabularRow(values);
                }

                await context.Progress.ReportAsync(
                    new WorkflowNodeProgress(
                        RowsRead: batch.Rows.Count,
                        BatchesProcessed: 1,
                        BytesRead: batch.EstimatedByteCount),
                    cancellationToken);
                if (rows.Length > 0)
                {
                    yield return new TabularBatch(rows);
                }
            }
        }
    }
}
