using System.Globalization;
using System.Runtime.CompilerServices;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Domain.Connections;
using THub.Domain.Workflows;
using THub.Infrastructure.Files;

namespace THub.Infrastructure.Execution;

public sealed class CsvSourceNodeExecutor(
    ExecutionConnectionResolver connectionResolver,
    ApprovedPathResolver pathResolver,
    WorkflowNodeSettingsValidator settingsValidator) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Source(WorkflowNodeKind.CsvSource);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (CsvSourceNodeSettings)settingsValidator.Parse(context.Node);
        var connection = await connectionResolver.ResolveAsync<FileConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.CsvFile,
            cancellationToken);
        var path = pathResolver.ResolveFile(
            connection.ApprovedRoot,
            settings.RelativePath,
            connection.AllowUncRoot);
        var file = RequireBoundedSourceFile(path, connection.MaximumFileBytes);
        var schema = await DiscoverCsvSchemaAsync(
            path,
            settings,
            connection,
            context.Limits,
            cancellationToken);
        return WorkflowNodeExecutionResult.WithOutput(
            schema,
            ReadCsvAsync(
                path,
                file.Length,
                schema,
                settings,
                connection,
                context,
                cancellationToken));
    }

    internal static async Task<TabularSchema> DiscoverCsvSchemaAsync(
        string path,
        CsvSourceNodeSettings settings,
        FileConnectionConfiguration connection,
        TabularExecutionLimits limits,
        CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(path);
        using var textReader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(textReader, CsvConfigurationFor(settings.Delimiter, settings.HasHeader));
        if (!await csv.ReadAsync())
        {
            throw ExecutionFailure.Data("execution.csv.empty", "The configured CSV file is empty.");
        }

        string[]? header = null;
        if (settings.HasHeader)
        {
            csv.ReadHeader();
            header = csv.HeaderRecord;
            if (header is null || header.Length == 0)
            {
                throw ExecutionFailure.Data("execution.csv.header", "The CSV header is empty or invalid.");
            }
        }

        var columns = CreateFileColumns(header, settings.Columns, connection, limits);
        cancellationToken.ThrowIfCancellationRequested();
        return new TabularSchema(columns);
    }

    internal static async IAsyncEnumerable<TabularBatch> ReadCsvAsync(
        string path,
        long fileLength,
        TabularSchema schema,
        CsvSourceNodeSettings settings,
        FileConnectionConfiguration connection,
        WorkflowNodeExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(path);
        using var textReader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(textReader, CsvConfigurationFor(settings.Delimiter, settings.HasHeader));
        if (settings.HasHeader && await csv.ReadAsync())
        {
            csv.ReadHeader();
        }

        var rows = new List<TabularRow>(context.Limits.MaximumRowsPerBatch);
        long totalRows = 0;
        long lastPosition = 0;
        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = csv.Parser.Record
                ?? throw ExecutionFailure.Data("execution.csv.record", "The CSV reader returned an invalid record.");
            if (record.Length != schema.Columns.Count)
            {
                throw ExecutionFailure.Data(
                    "execution.csv.columns",
                    "A CSV row has a different column count from the configured schema.");
            }

            totalRows++;
            if (totalRows > connection.MaximumRows)
            {
                throw new TabularLimitExceededException(
                    "execution.csv.rows.limit",
                    $"The CSV file exceeds its configured {connection.MaximumRows}-row limit.");
            }

            rows.Add(new TabularRow(record.Select((value, index) =>
                TabularExecutionSupport.ParseText(
                    value,
                    schema.Columns[index],
                    context.Limits.MaximumStringCharacters))));
            if (rows.Count == context.Limits.MaximumRowsPerBatch)
            {
                var batch = new TabularBatch(rows);
                var position = Math.Min(fileLength, stream.Position);
                await context.Progress.ReportAsync(
                    new WorkflowNodeProgress(
                        RowsRead: batch.Rows.Count,
                        BatchesProcessed: 1,
                        BytesRead: Math.Max(0, position - lastPosition)),
                    cancellationToken);
                lastPosition = position;
                yield return batch;
                rows = new(context.Limits.MaximumRowsPerBatch);
            }
        }

        if (rows.Count > 0)
        {
            var batch = new TabularBatch(rows);
            await context.Progress.ReportAsync(
                new WorkflowNodeProgress(
                    RowsRead: batch.Rows.Count,
                    BatchesProcessed: 1,
                    BytesRead: Math.Max(0, fileLength - lastPosition)),
                cancellationToken);
            yield return batch;
        }
    }

    internal static IReadOnlyList<TabularColumn> CreateFileColumns(
        IReadOnlyList<string>? header,
        IReadOnlyList<DelimitedColumnSettings>? configured,
        FileConnectionConfiguration connection,
        TabularExecutionLimits limits)
    {
        if (configured is not null)
        {
            if (header is not null
                && (header.Count != configured.Count
                    || header.Where((name, index) => !string.Equals(
                        name?.Trim(),
                        configured[index].Name,
                        StringComparison.OrdinalIgnoreCase)).Any()))
            {
                throw ExecutionFailure.Data(
                    "execution.file.header.mismatch",
                    "The file header does not match the configured typed columns.");
            }

            EnsureColumnBound(configured.Count, connection, limits);
            return configured
                .Select(column => new TabularColumn(column.Name, column.DataType, column.IsNullable))
                .ToArray();
        }

        if (header is null)
        {
            throw ExecutionFailure.Configuration(
                "execution.file.columns.required",
                "Files without a header require explicit typed columns.");
        }

        EnsureColumnBound(header.Count, connection, limits);
        try
        {
            return header
                .Select(name => new TabularColumn(name, TabularDataType.String))
                .ToArray();
        }
        catch (ArgumentException exception)
        {
            throw ExecutionFailure.Data(
                "execution.file.header.invalid",
                "The file header contains an empty, duplicate, or invalid column name.",
                exception);
        }
    }

    internal static FileInfo RequireBoundedSourceFile(string path, long maximumBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The configured source file does not exist.", path);
        }

        if (file.Length > maximumBytes)
        {
            throw new TabularLimitExceededException(
                "execution.file.bytes.limit",
                $"The source file exceeds its configured {maximumBytes}-byte limit.");
        }

        return file;
    }

    private static void EnsureColumnBound(
        int count,
        FileConnectionConfiguration connection,
        TabularExecutionLimits limits)
    {
        var maximum = Math.Min(connection.MaximumColumns, limits.MaximumColumns);
        if (count < 1 || count > maximum)
        {
            throw new TabularLimitExceededException(
                "execution.file.columns.limit",
                $"The file column count must be between 1 and {maximum}.");
        }
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static CsvConfiguration CsvConfigurationFor(char delimiter, bool hasHeader) =>
        new(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter.ToString(),
            HasHeaderRecord = hasHeader,
            DetectColumnCountChanges = true,
            MaxFieldSize = TabularValue.AbsoluteMaximumStringCharacters
        };
}

public sealed class ExcelSourceNodeExecutor(
    ExecutionConnectionResolver connectionResolver,
    ApprovedPathResolver pathResolver,
    WorkflowNodeSettingsValidator settingsValidator) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Source(WorkflowNodeKind.ExcelSource);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (ExcelSourceNodeSettings)settingsValidator.Parse(context.Node);
        var connection = await connectionResolver.ResolveAsync<FileConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.ExcelFile,
            cancellationToken);
        var path = pathResolver.ResolveFile(connection.ApprovedRoot, settings.RelativePath, connection.AllowUncRoot);
        _ = CsvSourceNodeExecutor.RequireBoundedSourceFile(path, connection.MaximumFileBytes);
        var schema = DiscoverSchema(path, settings, connection, context.Limits, cancellationToken);
        return WorkflowNodeExecutionResult.WithOutput(
            schema,
            ReadExcelAsync(path, schema, settings, connection, context, cancellationToken));
    }

    internal static TabularSchema DiscoverSchema(
        string path,
        ExcelSourceNodeSettings settings,
        FileConnectionConfiguration connection,
        TabularExecutionLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var workbook = new XLWorkbook(path);
        var range = ResolveRange(workbook, settings);
        var columnCount = range.ColumnCount();
        var header = settings.HasHeader
            ? Enumerable.Range(1, columnCount).Select(index => range.Cell(1, index).GetString()).ToArray()
            : null;
        return new TabularSchema(CsvSourceNodeExecutor.CreateFileColumns(
            header,
            settings.Columns,
            connection,
            limits));
    }

    internal static async IAsyncEnumerable<TabularBatch> ReadExcelAsync(
        string path,
        TabularSchema schema,
        ExcelSourceNodeSettings settings,
        FileConnectionConfiguration connection,
        WorkflowNodeExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook(path);
        var range = ResolveRange(workbook, settings);
        var firstDataRow = settings.HasHeader ? 2 : 1;
        var rows = new List<TabularRow>(context.Limits.MaximumRowsPerBatch);
        long totalRows = 0;
        for (var rowIndex = firstDataRow; rowIndex <= range.RowCount(); rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalRows++;
            if (totalRows > connection.MaximumRows)
            {
                throw new TabularLimitExceededException(
                    "execution.excel.rows.limit",
                    $"The workbook range exceeds its configured {connection.MaximumRows}-row limit.");
            }

            rows.Add(new TabularRow(Enumerable.Range(1, schema.Columns.Count).Select(columnIndex =>
                TabularExecutionSupport.ParseText(
                    range.Cell(rowIndex, columnIndex).GetString(),
                    schema.Columns[columnIndex - 1],
                    context.Limits.MaximumStringCharacters))));
            if (rows.Count == context.Limits.MaximumRowsPerBatch)
            {
                var batch = new TabularBatch(rows);
                await context.Progress.ReportAsync(
                    new WorkflowNodeProgress(
                        RowsRead: batch.Rows.Count,
                        BatchesProcessed: 1,
                        BytesRead: batch.EstimatedByteCount),
                    cancellationToken);
                yield return batch;
                rows = new(context.Limits.MaximumRowsPerBatch);
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

    private static IXLRange ResolveRange(XLWorkbook workbook, ExcelSourceNodeSettings settings)
    {
        var worksheet = workbook.Worksheets.FirstOrDefault(sheet =>
            string.Equals(sheet.Name, settings.Worksheet, StringComparison.OrdinalIgnoreCase))
            ?? throw ExecutionFailure.Configuration(
                "execution.excel.worksheet",
                "The configured worksheet does not exist.");
        try
        {
            return settings.Range is null
                ? worksheet.RangeUsed() ?? throw ExecutionFailure.Data(
                    "execution.excel.empty",
                    "The configured worksheet is empty.")
                : worksheet.Range(settings.Range);
        }
        catch (ArgumentException exception)
        {
            throw ExecutionFailure.Configuration(
                "execution.excel.range",
                "The configured worksheet range is invalid.",
                exception);
        }
    }
}

public sealed class CsvTargetNodeExecutor(
    ExecutionConnectionResolver connectionResolver,
    ApprovedPathResolver pathResolver,
    WorkflowNodeSettingsValidator settingsValidator) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Target(WorkflowNodeKind.CsvTarget);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (CsvTargetNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context);
        var connection = await connectionResolver.ResolveAsync<FileConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.CsvFile,
            cancellationToken);
        string relativePath;
        try
        {
            relativePath = WorkflowFilePathTemplate.Render(
                settings.RelativePathTemplate,
                context.Variables);
        }
        catch (InvalidOperationException exception)
        {
            throw ExecutionFailure.Configuration(
                "execution.file.path.template",
                "The CSV target file name template could not be resolved.",
                exception);
        }

        var target = pathResolver.ResolveFile(
            connection.ApprovedRoot,
            relativePath,
            connection.AllowUncRoot);
        await WriteCsvAsync(target, input.DataSet, settings, connection, context, cancellationToken);
        return WorkflowNodeExecutionResult.WithoutOutput;
    }

    internal static async Task WriteCsvAsync(
        string target,
        ITabularDataSet input,
        CsvTargetNodeSettings settings,
        FileConnectionConfiguration connection,
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        FileTargetSupport.ValidateFileTarget(target, settings.Mode);
        var temporary = FileTargetSupport.CreateTemporaryPath(target, context.WorkflowRunId, context.Node.Id, ".csv");
        try
        {
            var appendExisting = settings.Mode == "append"
                && File.Exists(target)
                && new FileInfo(target).Length > 0;
            if (appendExisting)
            {
                await FileTargetSupport.CopyFileAsync(
                    target,
                    temporary,
                    cancellationToken);
                FileTargetSupport.EnsureFileLimit(
                    new FileInfo(temporary).Length,
                    connection.MaximumFileBytes);
            }
            var appendNeedsNewLine = appendExisting
                && !FileTargetSupport.EndsWithNewLine(temporary);

            await using (var stream = new FileStream(
                temporary,
                appendExisting ? FileMode.Append : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var textWriter = new StreamWriter(stream))
            await using (var csv = new CsvWriter(textWriter, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = settings.Delimiter.ToString(),
                HasHeaderRecord = settings.IncludeHeader
            }))
            {
                if (appendNeedsNewLine)
                {
                    await textWriter.WriteLineAsync();
                }

                if (settings.IncludeHeader && !appendExisting)
                {
                    foreach (var column in input.Schema.Columns)
                    {
                        csv.WriteField(column.Name);
                    }
                    await csv.NextRecordAsync();
                }

                long rowsWritten = 0;
                var bytesWritten = stream.Length;
                await foreach (var batch in input.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
                {
                    await using (batch.ConfigureAwait(false))
                    {
                        foreach (var row in batch.Rows)
                        {
                            foreach (var value in row.Values)
                            {
                                csv.WriteField(TabularExecutionSupport.ToInvariantText(value));
                            }
                            await csv.NextRecordAsync();
                        }

                        rowsWritten += batch.Rows.Count;
                        FileTargetSupport.EnsureRowLimit(rowsWritten, connection.MaximumRows);
                        await textWriter.FlushAsync(cancellationToken);
                        FileTargetSupport.EnsureFileLimit(stream.Length, connection.MaximumFileBytes);
                        var bytesWrittenDelta = checked(stream.Length - bytesWritten);
                        bytesWritten = stream.Length;
                        await context.Progress.ReportAsync(
                            new WorkflowNodeProgress(
                                RowsRead: batch.Rows.Count,
                                RowsWritten: batch.Rows.Count,
                                BatchesProcessed: 1,
                                BytesRead: batch.EstimatedByteCount,
                                BytesWritten: bytesWrittenDelta),
                            cancellationToken);
                    }
                }
            }

            File.Move(
                temporary,
                target,
                overwrite: settings.Mode is "append" or "replace");
        }
        catch
        {
            FileTargetSupport.DeleteTemporary(temporary);
            throw;
        }
    }
}

public sealed class ExcelTargetNodeExecutor(
    ExecutionConnectionResolver connectionResolver,
    ApprovedPathResolver pathResolver,
    WorkflowNodeSettingsValidator settingsValidator) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Target(WorkflowNodeKind.ExcelTarget);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (ExcelTargetNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context);
        var connection = await connectionResolver.ResolveAsync<FileConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.ExcelFile,
            cancellationToken);
        string relativePath;
        try
        {
            relativePath = WorkflowFilePathTemplate.Render(
                settings.RelativePathTemplate,
                context.Variables);
        }
        catch (InvalidOperationException exception)
        {
            throw ExecutionFailure.Configuration(
                "execution.file.path.template",
                "The Excel target file name template could not be resolved.",
                exception);
        }

        var target = pathResolver.ResolveFile(
            connection.ApprovedRoot,
            relativePath,
            connection.AllowUncRoot);
        await WriteExcelAsync(target, input.DataSet, settings, connection, context, cancellationToken);
        return WorkflowNodeExecutionResult.WithoutOutput;
    }

    internal static async Task WriteExcelAsync(
        string target,
        ITabularDataSet dataSet,
        ExcelTargetNodeSettings settings,
        FileConnectionConfiguration connection,
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        FileTargetSupport.ValidateFileTarget(target, settings.Mode);
        var temporary = FileTargetSupport.CreateTemporaryPath(
            target,
            context.WorkflowRunId,
            context.Node.Id,
            Path.GetExtension(target));
        try
        {
            var appendExisting = settings.Mode == "append"
                && File.Exists(target)
                && new FileInfo(target).Length > 0;
            if (appendExisting)
            {
                await FileTargetSupport.CopyFileAsync(
                    target,
                    temporary,
                    cancellationToken);
                FileTargetSupport.EnsureFileLimit(
                    new FileInfo(temporary).Length,
                    connection.MaximumFileBytes);
            }

            using var workbook = appendExisting
                ? new XLWorkbook(temporary)
                : new XLWorkbook();
            var worksheet = workbook.Worksheets.FirstOrDefault(sheet =>
                string.Equals(
                    sheet.Name,
                    settings.Worksheet,
                    StringComparison.OrdinalIgnoreCase))
                ?? workbook.AddWorksheet(settings.Worksheet);
            var lastUsedRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;
            var existingDataRows = Math.Max(
                0,
                lastUsedRow - (settings.IncludeHeader ? 1 : 0));
            var rowIndex = lastUsedRow + 1;
            if (settings.IncludeHeader && lastUsedRow == 0)
            {
                for (var columnIndex = 0; columnIndex < dataSet.Schema.Columns.Count; columnIndex++)
                {
                    worksheet.Cell(rowIndex, columnIndex + 1).Value = dataSet.Schema.Columns[columnIndex].Name;
                }
                rowIndex++;
            }

            long rowsWritten = 0;
            await foreach (var batch in dataSet.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
            {
                await using (batch.ConfigureAwait(false))
                {
                    foreach (var row in batch.Rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        for (var columnIndex = 0; columnIndex < row.Values.Count; columnIndex++)
                        {
                            SetCell(worksheet.Cell(rowIndex, columnIndex + 1), row.Values[columnIndex]);
                        }
                        rowIndex++;
                    }

                    rowsWritten += batch.Rows.Count;
                    FileTargetSupport.EnsureRowLimit(
                        checked(existingDataRows + rowsWritten),
                        connection.MaximumRows);
                    await context.Progress.ReportAsync(
                        new WorkflowNodeProgress(
                            RowsRead: batch.Rows.Count,
                            RowsWritten: batch.Rows.Count,
                            BatchesProcessed: 1,
                            BytesRead: batch.EstimatedByteCount),
                        cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (appendExisting)
            {
                workbook.Save();
            }
            else
            {
                workbook.SaveAs(temporary);
            }
            FileTargetSupport.EnsureFileLimit(new FileInfo(temporary).Length, connection.MaximumFileBytes);
            File.Move(
                temporary,
                target,
                overwrite: settings.Mode is "append" or "replace");
        }
        catch
        {
            FileTargetSupport.DeleteTemporary(temporary);
            throw;
        }
    }

    private static void SetCell(IXLCell cell, TabularValue value)
    {
        if (value.Kind == TabularValueKind.Null)
        {
            cell.Clear();
            return;
        }

        switch (value.Kind)
        {
            case TabularValueKind.Boolean:
                cell.Value = (bool)value.Value!;
                break;
            case TabularValueKind.Int64:
                cell.Value = (long)value.Value!;
                break;
            case TabularValueKind.Decimal:
                cell.Value = (decimal)value.Value!;
                break;
            case TabularValueKind.Double:
                cell.Value = (double)value.Value!;
                break;
            case TabularValueKind.String:
                cell.Value = (string)value.Value!;
                break;
            case TabularValueKind.DateTimeOffset:
                cell.Value = ((DateTimeOffset)value.Value!).ToString("O", CultureInfo.InvariantCulture);
                break;
            case TabularValueKind.Guid:
                cell.Value = ((Guid)value.Value!).ToString("D");
                break;
            case TabularValueKind.Binary:
                cell.Value = Convert.ToBase64String(((ReadOnlyMemory<byte>)value.Value!).Span);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}

internal static class FileTargetSupport
{
    public static void ValidateFileTarget(string target, string mode)
    {
        EnsureTargetDirectory(target);
        if (mode == "createNew" && File.Exists(target))
        {
            throw ExecutionFailure.ExternalSideEffect(
                "execution.file.target.exists",
                "The target file already exists; createNew mode never overwrites data.");
        }
    }

    public static void EnsureNewTarget(string target)
    {
        ValidateFileTarget(target, "createNew");
    }

    public static void EnsureTargetDirectory(string target)
    {
        var directory = Path.GetDirectoryName(target);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The approved target directory does not exist.");
        }
    }

    public static string CreateTemporaryPath(
        string target,
        Guid workflowRunId,
        string nodeId,
        string extension)
    {
        var safeNode = string.Concat(nodeId.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_'));
        return $"{target}.{workflowRunId:N}.{safeNode}.{Guid.NewGuid():N}.partial{extension}";
    }

    public static void EnsureRowLimit(long rowCount, int maximumRows)
    {
        if (rowCount > maximumRows)
        {
            throw new TabularLimitExceededException(
                "execution.file.rows.limit",
                $"The target exceeds its configured {maximumRows}-row limit.");
        }
    }

    public static void EnsureFileLimit(long fileBytes, long maximumBytes)
    {
        if (fileBytes > maximumBytes)
        {
            throw new TabularLimitExceededException(
                "execution.file.bytes.limit",
                $"The target exceeds its configured {maximumBytes}-byte limit.");
        }
    }

    public static bool EndsWithNewLine(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        stream.Position = stream.Length - 1;
        return stream.ReadByte() == '\n';
    }

    public static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(
            destination,
            64 * 1024,
            cancellationToken);
    }

    public static void DeleteTemporary(string temporary)
    {
        try
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        catch (IOException)
        {
            // The incomplete file retains the explicit .partial marker for operator cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // The incomplete file retains the explicit .partial marker for operator cleanup.
        }
    }
}
