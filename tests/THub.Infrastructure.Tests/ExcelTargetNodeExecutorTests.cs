using System.Runtime.CompilerServices;
using ClosedXML.Excel;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Domain.Connections;
using THub.Domain.Workflows;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Tests;

public sealed class ExcelTargetNodeExecutorTests
{
    [Fact]
    public async Task AppendAddsRowsToExistingWorksheetWithoutRepeatingHeader()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var target = Path.Combine(root, "export.xlsx");
            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.AddWorksheet("Results");
                worksheet.Cell(1, 1).Value = "Id";
                worksheet.Cell(1, 2).Value = "Name";
                worksheet.Cell(2, 1).Value = 1;
                worksheet.Cell(2, 2).Value = "One";
                workbook.SaveAs(target);
            }

            await ExcelTargetNodeExecutor.WriteExcelAsync(
                target,
                DataSet(2, "Two"),
                Settings("append"),
                Connection(root),
                Context(),
                CancellationToken.None);

            using var result = new XLWorkbook(target);
            var sheet = result.Worksheet("Results");
            Assert.Equal("Id", sheet.Cell(1, 1).GetString());
            Assert.Equal("Name", sheet.Cell(1, 2).GetString());
            Assert.Equal("1", sheet.Cell(2, 1).GetString());
            Assert.Equal("One", sheet.Cell(2, 2).GetString());
            Assert.Equal("2", sheet.Cell(3, 1).GetString());
            Assert.Equal("Two", sheet.Cell(3, 2).GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacePublishesOnlyNewWorkbookRows()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var target = Path.Combine(root, "export.xlsx");
            using (var workbook = new XLWorkbook())
            {
                workbook.AddWorksheet("Results").Cell(1, 1).Value = "Old";
                workbook.SaveAs(target);
            }

            await ExcelTargetNodeExecutor.WriteExcelAsync(
                target,
                DataSet(2, "Two"),
                Settings("replace"),
                Connection(root),
                Context(),
                CancellationToken.None);

            using var result = new XLWorkbook(target);
            var sheet = result.Worksheet("Results");
            Assert.Equal("Id", sheet.Cell(1, 1).GetString());
            Assert.Equal("Name", sheet.Cell(1, 2).GetString());
            Assert.Equal("2", sheet.Cell(2, 1).GetString());
            Assert.Equal("Two", sheet.Cell(2, 2).GetString());
            Assert.True(sheet.Cell(3, 1).IsEmpty());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ExcelTargetNodeSettings Settings(string mode) => new(
        Guid.NewGuid(),
        "export.xlsx",
        "Results",
        IncludeHeader: true,
        Mode: mode);

    private static FileConnectionConfiguration Connection(string root) => new(
        ConnectionKind.ExcelFile,
        root,
        maximumFileBytes: 4_194_304,
        maximumRows: 100,
        maximumColumns: 10);

    private static WorkflowNodeExecutionContext Context() => new(
        Guid.NewGuid(),
        new WorkflowNode(
            "excel-target",
            WorkflowNodeKind.ExcelTarget,
            "Excel target",
            0,
            0,
            """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"export.xlsx","worksheet":"Results","includeHeader":true}"""),
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
        var root = Path.Combine(Path.GetTempPath(), $"thub-excel-target-tests-{Guid.NewGuid():N}");
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
