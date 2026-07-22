using System.Runtime.CompilerServices;
using THub.Application.Execution;
using THub.Domain.Workflows;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Tests;

public sealed class TransformNodeExecutorTests
{
    [Fact]
    public async Task SelectColumnsProjectsSchemaAndValuesInConfiguredOrder()
    {
        var input = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("Name", TabularDataType.String)
            ]),
            [new([TabularValue.From(7L), TabularValue.From("Seven")])]);
        var executor = new SelectColumnsNodeExecutor(new WorkflowNodeSettingsValidator());
        var context = Context(
            new("select", WorkflowNodeKind.SelectColumns, "Select", 0, 0, """{"columns":["Name","Id"]}"""),
            [new("source", input)]);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal(["Name", "Id"], result.Output!.Schema.Columns.Select(column => column.Name));
        var row = Assert.Single(rows);
        Assert.Equal("Seven", row.Values[0].Value);
        Assert.Equal(7L, row.Values[1].Value);
    }

    [Fact]
    public async Task FilterRowsAppliesTypedConditionsWithAndSemantics()
    {
        var schema = new TabularSchema(
        [
            new("Id", TabularDataType.Int64, false),
            new("Name", TabularDataType.String, false)
        ]);
        var input = DataSet(
            schema,
            [
                new([TabularValue.From(1L), TabularValue.From("Alpha")]),
                new([TabularValue.From(2L), TabularValue.From("Beta")]),
                new([TabularValue.From(3L), TabularValue.From("Alpine")])
            ]);
        var executor = new FilterRowsNodeExecutor(new WorkflowNodeSettingsValidator());
        var context = Context(
            new(
                "filter",
                WorkflowNodeKind.FilterRows,
                "Filter",
                0,
                0,
                """{"conditions":[{"column":"Id","operator":"greaterThan","value":1},{"column":"Name","operator":"startsWith","value":"Al"}]}"""),
            [new("source", input)]);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        var row = Assert.Single(rows);
        Assert.Equal(3L, row.Values[0].Value);
    }

    [Fact]
    public async Task LeftJoinUsesConfiguredSourceIdentitiesAndProducesNullsForMissingMatch()
    {
        var left = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("Name", TabularDataType.String, false)
            ]),
            [
                new([TabularValue.From(1L), TabularValue.From("One")]),
                new([TabularValue.From(2L), TabularValue.From("Two")])
            ]);
        var right = DataSet(
            new TabularSchema(
            [
                new("Id", TabularDataType.Int64, false),
                new("Code", TabularDataType.String, false)
            ]),
            [new([TabularValue.From(1L), TabularValue.From("A")])]);
        var executor = new JoinNodeExecutor(new WorkflowNodeSettingsValidator());
        var context = Context(
            new(
                "join",
                WorkflowNodeKind.Join,
                "Join",
                0,
                0,
                """{"leftNodeId":"left","rightNodeId":"right","leftKeys":["Id"],"rightKeys":["Id"],"type":"left","maximumBufferedRows":100}"""),
            [new("left", left), new("right", right)]);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);
        var rows = await ReadRowsAsync(result.Output!);

        Assert.Equal(["Id", "Name", "right.Id", "Code"], result.Output!.Schema.Columns.Select(column => column.Name));
        Assert.Equal(2, rows.Count);
        Assert.Equal("A", rows[0].Values[3].Value);
        Assert.Equal(TabularValueKind.Null, rows[1].Values[2].Kind);
        Assert.Equal(TabularValueKind.Null, rows[1].Values[3].Kind);
    }

    private static WorkflowNodeExecutionContext Context(
        WorkflowNode node,
        IReadOnlyList<WorkflowNodeInput> inputs) => new(
            Guid.NewGuid(),
            node,
            1,
            inputs,
            new TabularExecutionLimits(),
            new RecordingProgress());

    private static ITabularDataSet DataSet(TabularSchema schema, IReadOnlyList<TabularRow> rows) =>
        new TestDataSet(schema, rows);

    private static async Task<IReadOnlyList<TabularRow>> ReadRowsAsync(WorkflowNodeOutput output)
    {
        var rows = new List<TabularRow>();
        await foreach (var batch in output.Batches)
        {
            await using (batch)
            {
                rows.AddRange(batch.Rows);
            }
        }

        return rows;
    }

    private sealed class RecordingProgress : IWorkflowNodeProgressReporter
    {
        public ValueTask ReportAsync(
            WorkflowNodeProgress delta,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delta.Validate();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDataSet(TabularSchema schema, IReadOnlyList<TabularRow> rows)
        : ITabularDataSet
    {
        public TabularSchema Schema { get; } = schema;

        public long RowCount => rows.Count;

        public long ByteCount => 0;

        public async IAsyncEnumerable<TabularBatch> ReadBatchesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TabularBatch(rows);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
