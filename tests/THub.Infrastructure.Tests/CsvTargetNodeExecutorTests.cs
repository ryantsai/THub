using System.Runtime.CompilerServices;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Domain.Connections;
using THub.Domain.Workflows;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Tests;

public sealed class CsvTargetNodeExecutorTests
{
    [Fact]
    public async Task AppendPreservesExistingRowsAndDoesNotRepeatHeader()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var target = Path.Combine(root, "export.csv");
            await File.WriteAllTextAsync(target, "Id,Name\r\n1,One\r\n");

            await CsvTargetNodeExecutor.WriteCsvAsync(
                target,
                DataSet(2, "Two"),
                Settings("append"),
                Connection(root),
                Context(),
                CancellationToken.None);

            Assert.Equal(
                ["Id,Name", "1,One", "2,Two"],
                await File.ReadAllLinesAsync(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacePublishesOnlyNewRowsAfterStaging()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var target = Path.Combine(root, "export.csv");
            await File.WriteAllTextAsync(target, "Id,Name\r\n1,One\r\n");

            await CsvTargetNodeExecutor.WriteCsvAsync(
                target,
                DataSet(2, "Two"),
                Settings("replace"),
                Connection(root),
                Context(),
                CancellationToken.None);

            Assert.Equal(
                ["Id,Name", "2,Two"],
                await File.ReadAllLinesAsync(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CsvTargetNodeSettings Settings(string mode) => new(
        Guid.NewGuid(),
        "export.csv",
        IncludeHeader: true,
        Delimiter: ',',
        Mode: mode);

    private static FileConnectionConfiguration Connection(string root) => new(
        ConnectionKind.CsvFile,
        root,
        maximumFileBytes: 1_048_576,
        maximumRows: 100,
        maximumColumns: 10);

    private static WorkflowNodeExecutionContext Context() => new(
        Guid.NewGuid(),
        new WorkflowNode(
            "csv-target",
            WorkflowNodeKind.CsvTarget,
            "CSV target",
            0,
            0,
            """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"export.csv","includeHeader":true}"""),
        1,
        [],
        new TabularExecutionLimits(),
        new RecordingProgress());

    private static ITabularDataSet DataSet(long id, string name) => new TestDataSet(
        new TabularSchema(
        [
            new("Id", TabularDataType.Int64, false),
            new("Name", TabularDataType.String, false)
        ]),
        [new([TabularValue.From(id), TabularValue.From(name)])]);

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"thub-csv-target-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
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
