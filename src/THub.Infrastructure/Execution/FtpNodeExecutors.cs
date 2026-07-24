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
        string remotePath;
        try
        {
            remotePath = WorkflowFilePathTemplate.Render(
                settings.RemotePathTemplate,
                context.Variables);
        }
        catch (InvalidOperationException exception)
        {
            throw ExecutionFailure.Configuration(
                "execution.ftp.path.template",
                "The FTP target file name template could not be resolved.",
                exception);
        }

        var temporary = FtpTemporaryFile.Create(settings.Format);
        try
        {
            var appendExisting = false;
            await using (var client = await clientFactory.CreateConnectedAsync(
                             configuration,
                             cancellationToken))
            {
                var remoteExists = await client.FileExists(remotePath, cancellationToken);
                if (settings.Mode == "createNew" && remoteExists)
                {
                    throw ExecutionFailure.ExternalSideEffect(
                        "execution.ftp.target.exists",
                        "The FTP target already exists; createNew mode never overwrites remote files.");
                }

                appendExisting = settings.Mode == "append" && remoteExists;
                if (appendExisting)
                {
                    var size = await client.GetFileSize(remotePath, -1, cancellationToken);
                    if (size < 0 || size > configuration.MaximumFileBytes)
                    {
                        throw new TabularLimitExceededException(
                            "execution.ftp.bytes.limit",
                            "The FTP target file is unavailable or exceeds its configured byte limit.");
                    }

                    var downloadStatus = await client.DownloadFile(
                        temporary.Path,
                        remotePath,
                        FtpLocalExists.Overwrite,
                        FtpVerify.None,
                        progress: null,
                        cancellationToken);
                    if (downloadStatus != FtpStatus.Success)
                    {
                        throw ExecutionFailure.Connectivity(
                            "execution.ftp.download",
                            "The existing FTP target could not be downloaded for append.",
                            isRetryable: false);
                    }
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
                await ExcelTargetNodeExecutor.WriteExcelAsync(
                    temporary.Path,
                    input,
                    new ExcelTargetNodeSettings(
                        settings.ConnectionId,
                        temporary.FileName,
                        settings.Worksheet!,
                        settings.IncludeHeader,
                        appendExisting ? "append" : "replace"),
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
                        appendExisting ? "append" : "replace"),
                    localConfiguration,
                    context,
                    cancellationToken);
            }

            await using var publishClient = await clientFactory.CreateConnectedAsync(
                configuration,
                cancellationToken);
            var remoteStagingPath = CreateRemoteStagingPath(remotePath, context);
            var published = false;
            try
            {
                var status = await publishClient.UploadFile(
                    temporary.Path,
                    remoteStagingPath,
                    FtpRemoteExists.Skip,
                    createRemoteDir: false,
                    FtpVerify.None,
                    progress: null,
                    cancellationToken);
                if (status != FtpStatus.Success)
                {
                    throw ExecutionFailure.ExternalSideEffect(
                        "execution.ftp.upload",
                        "The staged FTP target could not be uploaded.");
                }

                published = await publishClient.MoveFile(
                    remoteStagingPath,
                    remotePath,
                    settings.Mode == "createNew"
                        ? FtpRemoteExists.Skip
                        : FtpRemoteExists.Overwrite,
                    cancellationToken);
                if (!published)
                {
                    throw ExecutionFailure.ExternalSideEffect(
                        "execution.ftp.publish",
                        "The staged FTP target could not be moved into place.");
                }
            }
            finally
            {
                if (!published)
                {
                    await TryDeleteRemoteAsync(
                        publishClient,
                        remoteStagingPath,
                        cancellationToken);
                }
            }
            return WorkflowNodeExecutionResult.WithoutOutput;
        }
        finally
        {
            temporary.Delete();
        }
    }

    private static string CreateRemoteStagingPath(
        string remotePath,
        WorkflowNodeExecutionContext context)
    {
        var separator = remotePath.LastIndexOf('/');
        var directory = remotePath[..(separator + 1)];
        var safeNode = string.Concat(context.Node.Id.Select(character =>
            char.IsAsciiLetterOrDigit(character) ? character : '_'));
        var fileName = $".thub-{context.WorkflowRunId:N}-{safeNode}-{Guid.NewGuid():N}.partial";
        if (directory.Length + fileName.Length > WorkflowFilePathTemplate.MaximumLength)
        {
            throw ExecutionFailure.Configuration(
                "execution.ftp.path.length",
                "The FTP target directory leaves no room for a safe staged file name.");
        }

        return directory + fileName;
    }

    private static async Task TryDeleteRemoteAsync(
        AsyncFtpClient client,
        string remotePath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await client.FileExists(remotePath, cancellationToken))
            {
                await client.DeleteFile(remotePath, cancellationToken);
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && exception is not OutOfMemoryException)
        {
            // Best-effort cleanup; the explicit .partial name identifies crash remnants.
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
