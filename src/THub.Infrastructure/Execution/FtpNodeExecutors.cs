using System.Runtime.CompilerServices;
using FluentFTP;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Domain.Connections;
using THub.Domain.Workflows;
using THub.Infrastructure.Connections;

namespace THub.Infrastructure.Execution;

public sealed class FtpSourceNodeExecutor(
    ExecutionConnectionResolver connectionResolver,
    WorkflowNodeSettingsValidator settingsValidator,
    FtpClientFactory clientFactory) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Source(WorkflowNodeKind.FtpSource);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (FtpSourceNodeSettings)settingsValidator.Parse(context.Node);
        var configuration = await connectionResolver.ResolveAsync<FtpConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.Ftp,
            cancellationToken);
        var temporary = FtpTemporaryFile.Create(settings.Format);
        try
        {
            await using (var client = await clientFactory.CreateConnectedAsync(configuration, cancellationToken))
            {
                var size = await client.GetFileSize(settings.RemotePath, -1, cancellationToken);
                if (size < 0)
                {
                    throw ExecutionFailure.Configuration(
                        "execution.ftp.source.not-found",
                        "The configured FTP source file does not exist or its size is unavailable.");
                }
                if (size > configuration.MaximumFileBytes)
                {
                    throw new TabularLimitExceededException(
                        "execution.ftp.bytes.limit",
                        "The FTP source file exceeds its configured byte limit.");
                }
                var status = await client.DownloadFile(
                    temporary.Path,
                    settings.RemotePath,
                    FtpLocalExists.Overwrite,
                    FtpVerify.None,
                    progress: null,
                    cancellationToken);
                if (status != FtpStatus.Success)
                {
                    throw ExecutionFailure.Connectivity(
                        "execution.ftp.download",
                        "The FTP source file could not be downloaded.",
                        isRetryable: true);
                }
            }

            var localConfiguration = new FileConnectionConfiguration(
                settings.Format == FtpFileFormat.Excel ? ConnectionKind.ExcelFile : ConnectionKind.CsvFile,
                temporary.Directory,
                maximumFileBytes: configuration.MaximumFileBytes,
                maximumRows: configuration.MaximumRows,
                maximumColumns: configuration.MaximumColumns);
            if (settings.Format == FtpFileFormat.Excel)
            {
                var excelSettings = new ExcelSourceNodeSettings(
                    settings.ConnectionId,
                    temporary.FileName,
                    settings.Worksheet!,
                    Range: null,
                    settings.HasHeader,
                    settings.Columns);
                var schema = ExcelSourceNodeExecutor.DiscoverSchema(
                    temporary.Path,
                    excelSettings,
                    localConfiguration,
                    context.Limits,
                    cancellationToken);
                return WorkflowNodeExecutionResult.WithOutput(
                    schema,
                    FtpTemporaryFile.CleanupAfter(
                        ExcelSourceNodeExecutor.ReadExcelAsync(
                            temporary.Path,
                            schema,
                            excelSettings,
                            localConfiguration,
                            context,
                            cancellationToken),
                        temporary,
                        cancellationToken));
            }

            var csvSettings = new CsvSourceNodeSettings(
                settings.ConnectionId,
                temporary.FileName,
                settings.HasHeader,
                settings.Delimiter,
                settings.Columns);
            var csvSchema = await CsvSourceNodeExecutor.DiscoverCsvSchemaAsync(
                temporary.Path,
                csvSettings,
                localConfiguration,
                context.Limits,
                cancellationToken);
            return WorkflowNodeExecutionResult.WithOutput(
                csvSchema,
                FtpTemporaryFile.CleanupAfter(
                    CsvSourceNodeExecutor.ReadCsvAsync(
                        temporary.Path,
                        new FileInfo(temporary.Path).Length,
                        csvSchema,
                        csvSettings,
                        localConfiguration,
                        context,
                        cancellationToken),
                    temporary,
                    cancellationToken));
        }
        catch
        {
            temporary.Delete();
            throw;
        }
    }
}

public sealed class FtpTargetNodeExecutor(
    ExecutionConnectionResolver connectionResolver,
    WorkflowNodeSettingsValidator settingsValidator,
    FtpClientFactory clientFactory) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Target(WorkflowNodeKind.FtpTarget);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (FtpTargetNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context).DataSet;
        var configuration = await connectionResolver.ResolveAsync<FtpConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.Ftp,
            cancellationToken);
        var temporary = FtpTemporaryFile.Create(settings.Format);
        try
        {
            var localConfiguration = new FileConnectionConfiguration(
                settings.Format == FtpFileFormat.Excel ? ConnectionKind.ExcelFile : ConnectionKind.CsvFile,
                temporary.Directory,
                maximumFileBytes: configuration.MaximumFileBytes,
                maximumRows: configuration.MaximumRows,
                maximumColumns: configuration.MaximumColumns);
            if (settings.Format == FtpFileFormat.Excel)
            {
                await ExcelTargetNodeExecutor.WriteExcelAsync(
                    temporary.Path,
                    input,
                    new ExcelTargetNodeSettings(
                        settings.ConnectionId,
                        temporary.FileName,
                        settings.Worksheet!,
                        settings.IncludeHeader,
                        settings.Mode),
                    localConfiguration,
                    context,
                    cancellationToken);
            }
            else
            {
                await CsvTargetNodeExecutor.WriteCsvAsync(
                    temporary.Path,
                    input,
                    new CsvTargetNodeSettings(
                        settings.ConnectionId,
                        temporary.FileName,
                        settings.IncludeHeader,
                        settings.Delimiter,
                        settings.Mode),
                    localConfiguration,
                    context,
                    cancellationToken);
            }

            await using var client = await clientFactory.CreateConnectedAsync(configuration, cancellationToken);
            if (await client.FileExists(settings.RemotePath, cancellationToken))
            {
                throw ExecutionFailure.ExternalSideEffect(
                    "execution.ftp.target.exists",
                    "The FTP target already exists; createNew mode never overwrites remote files.");
            }
            var status = await client.UploadFile(
                temporary.Path,
                settings.RemotePath,
                FtpRemoteExists.Skip,
                createRemoteDir: false,
                FtpVerify.None,
                progress: null,
                cancellationToken);
            if (status != FtpStatus.Success)
            {
                throw ExecutionFailure.ExternalSideEffect(
                    "execution.ftp.upload",
                    "The FTP target could not be uploaded.");
            }
            return WorkflowNodeExecutionResult.WithoutOutput;
        }
        finally
        {
            temporary.Delete();
        }
    }
}

internal sealed class FtpTemporaryFile
{
    private FtpTemporaryFile(string directory, string path)
    {
        Directory = directory;
        Path = path;
    }

    public string Directory { get; }
    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);

    public static FtpTemporaryFile Create(FtpFileFormat format)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "THub",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var extension = format == FtpFileFormat.Excel ? ".xlsx" : ".csv";
        return new FtpTemporaryFile(directory, System.IO.Path.Combine(directory, $"payload{extension}"));
    }

    public static async IAsyncEnumerable<TabularBatch> CleanupAfter(
        IAsyncEnumerable<TabularBatch> source,
        FtpTemporaryFile temporary,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var batch in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            temporary.Delete();
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory);
        }
        catch (IOException)
        {
            // Best-effort cleanup; operational monitoring should detect stale temporary files.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; operational monitoring should detect stale temporary files.
        }
    }
}
