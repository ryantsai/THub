using System.Diagnostics;
using System.Runtime.Versioning;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using THub.Application.Actions;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Domain.Actions;
using THub.Domain.Runs;
using THub.Domain.Workflows;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Actions;

public sealed record ResolvedTrustedAction(
    TrustedAction Action,
    TrustedActionDefinition Definition);

public sealed class TrustedActionExecutionResolver(
    ITrustedActionStore store,
    TrustedActionDefinitionSerializer serializer)
{
    public async Task<ResolvedTrustedAction> ResolveAsync(
        Guid id,
        TrustedActionKind expectedKind,
        CancellationToken cancellationToken)
    {
        var action = await store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (action is null)
        {
            throw ExecutionFailure.Configuration(
                "execution.trusted-action.not-found",
                "The configured trusted action was not found.");
        }

        if (!action.IsEnabled)
        {
            throw ExecutionFailure.Configuration(
                "execution.trusted-action.disabled",
                "The configured trusted action is disabled.");
        }

        if (action.Kind != expectedKind)
        {
            throw ExecutionFailure.Configuration(
                "execution.trusted-action.kind",
                "The configured trusted action has the wrong kind.");
        }

        TrustedActionDefinition definition;
        try
        {
            definition = serializer.Deserialize(action);
        }
        catch (TrustedActionDefinitionException exception)
        {
            throw ExecutionFailure.Configuration(
                "execution.trusted-action.definition",
                "The configured trusted action definition is invalid.",
                exception);
        }

        if (definition is ExecutableActionDefinition executable)
        {
            ValidateExecutableFiles(executable);
        }

        return new(action, definition);
    }

    public static void ValidateExecutableFiles(ExecutableActionDefinition definition)
    {
        if (!File.Exists(definition.ExecutablePath) ||
            !Directory.Exists(definition.WorkingDirectory))
        {
            throw ExecutionFailure.Configuration(
                "execution.executable.path-unavailable",
                "The trusted executable or working directory is unavailable.");
        }

        if (HasReparsePointInPath(new FileInfo(definition.ExecutablePath)) ||
            HasReparsePointInPath(new DirectoryInfo(definition.WorkingDirectory)))
        {
            throw ExecutionFailure.Configuration(
                "execution.executable.reparse-point",
                "Trusted executable paths and working directories cannot contain reparse points.");
        }
    }

    private static bool HasReparsePointInPath(FileSystemInfo item)
    {
        FileSystemInfo? current = item;
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }

        return false;
    }
}

public static class WebhookNetworkGuard
{
    private static readonly HttpRequestOptionsKey<bool> AllowPrivateAddressesKey =
        new("THub.Webhook.AllowPrivateAddresses");

    public static void ApplyPolicy(
        HttpRequestMessage request,
        bool allowPrivateAddresses) =>
        request.Options.Set(AllowPrivateAddressesKey, allowPrivateAddresses);

    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var allowPrivateAddresses =
            context.InitialRequestMessage.Options.TryGetValue(
                AllowPrivateAddressesKey,
                out var configured) &&
            configured;
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken).ConfigureAwait(false);
        var permitted = addresses
            .Where(address => IsPermitted(address, allowPrivateAddresses))
            .ToArray();
        if (permitted.Length == 0)
        {
            throw new HttpRequestException(
                "The webhook destination resolved only to prohibited network addresses.");
        }

        Exception? lastError = null;
        foreach (var address in permitted)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastError = exception;
                socket.Dispose();
            }
        }

        throw new HttpRequestException(
            "The webhook destination could not be reached.",
            lastError);
    }

    private static bool IsPermitted(IPAddress address, bool allowPrivateAddresses)
    {
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6Multicast ||
            address.IsIPv6LinkLocal)
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var ipv6 = address.GetAddressBytes();
            if ((ipv6[0] & 0xfe) == 0xfc)
            {
                return allowPrivateAddresses;
            }

            if (!address.IsIPv4MappedToIPv6)
            {
                return true;
            }
        }

        var bytes = address.MapToIPv4().GetAddressBytes();
        if (bytes[0] is 0 or 127 ||
            bytes[0] == 169 && bytes[1] == 254 ||
            bytes[0] >= 224)
        {
            return false;
        }

        return allowPrivateAddresses ||
               !(bytes[0] == 10 ||
                 bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                 bytes[0] == 192 && bytes[1] == 168);
    }
}

public sealed class WebhookNodeExecutor(
    WorkflowNodeSettingsValidator settingsValidator,
    TrustedActionExecutionResolver actionResolver,
    IConnectionCredentialResolver credentialResolver,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookNodeExecutor> logger) : IWorkflowNodeExecutor
{
    public const string ClientName = "THub.TrustedWebhooks";

    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Action(WorkflowNodeKind.Webhook, consumesInput: true);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (WebhookNodeSettings)settingsValidator.Parse(context.Node);
        var resolved = await actionResolver.ResolveAsync(
            settings.TrustedActionId,
            TrustedActionKind.Webhook,
            cancellationToken).ConfigureAwait(false);
        var definition = (WebhookActionDefinition)resolved.Definition;
        var body = Encoding.UTF8.GetBytes(settings.Body);
        if (body.Length > definition.MaximumRequestBytes)
        {
            throw ExecutionFailure.Configuration(
                "execution.webhook.request-limit",
                "The webhook request body exceeds its trusted action policy.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
        using var request = new HttpRequestMessage(
            new HttpMethod(definition.Method),
            definition.Destination);
        WebhookNetworkGuard.ApplyPolicy(request, definition.AllowPrivateAddresses);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(definition.ContentType);
        foreach (var header in definition.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw ExecutionFailure.Configuration(
                    "execution.webhook.header",
                    "A trusted webhook header is invalid.");
            }
        }

        await ApplyAuthenticationAsync(
            request,
            resolved.Action,
            definition,
            credentialResolver,
            timeout.Token).ConfigureAwait(false);
        logger.LogInformation(
            "Trusted webhook invocation starting for run {WorkflowRunId}, node {NodeId}, action {TrustedActionId}.",
            context.WorkflowRunId,
            context.Node.Id,
            resolved.Action.Id);

        try
        {
            using var response = await httpClientFactory.CreateClient(ClientName).SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            var responseBytes = await DrainBoundedAsync(
                response.Content,
                definition.MaximumResponseBytes,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw ExecutionFailure.ExternalSideEffect(
                    "execution.webhook.rejected",
                    $"The trusted webhook returned HTTP {(int)response.StatusCode}.");
            }

            await context.Progress.ReportAsync(
                new WorkflowNodeProgress(
                    RowsRead: context.Inputs.Sum(input => input.DataSet.RowCount),
                    BatchesProcessed: 1,
                    BytesRead: responseBytes,
                    BytesWritten: body.Length),
                cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Trusted webhook invocation completed for run {WorkflowRunId}, node {NodeId}, action {TrustedActionId}, status {StatusCode}.",
                context.WorkflowRunId,
                context.Node.Id,
                resolved.Action.Id,
                (int)response.StatusCode);
            return WorkflowNodeExecutionResult.WithoutOutput;
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested &&
            timeout.IsCancellationRequested)
        {
            throw new WorkflowNodeExecutionException(
                new(
                    "execution.webhook.timeout",
                    ExecutionErrorCategory.Timeout,
                    "The trusted webhook exceeded its configured timeout.",
                    isRetryable: false),
                exception);
        }
    }

    private static async Task ApplyAuthenticationAsync(
        HttpRequestMessage request,
        TrustedAction action,
        WebhookActionDefinition definition,
        IConnectionCredentialResolver credentialResolver,
        CancellationToken cancellationToken)
    {
        if (definition.Authentication == WebhookAuthenticationKind.None)
        {
            return;
        }

        if (action.CredentialReference is null)
        {
            throw ExecutionFailure.Configuration(
                "execution.webhook.credential-reference",
                "The trusted webhook has no credential reference.");
        }

        var credential = await credentialResolver.ResolveAsync(
            TrustedActionCredentialReferences.ToStorageReference(action.CredentialReference),
            cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            throw ExecutionFailure.Configuration(
                "execution.webhook.credential-missing",
                "The trusted webhook credential was not found.");
        }

        request.Headers.Authorization = definition.Authentication switch
        {
            WebhookAuthenticationKind.Bearer =>
                new AuthenticationHeaderValue("Bearer", credential.Password),
            WebhookAuthenticationKind.Basic =>
                new AuthenticationHeaderValue(
                    "Basic",
                    CreateBasicParameter(credential)),
            _ => throw new ArgumentOutOfRangeException(nameof(definition)),
        };
    }

    private static string CreateBasicParameter(ConnectionCredential credential)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"{credential.UserName}:{credential.Password}");
        try
        {
            return Convert.ToBase64String(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<long> DrainBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 &&
            content.Headers.ContentLength > maximumBytes)
        {
            throw ExecutionFailure.ExternalSideEffect(
                "execution.webhook.response-limit",
                "The trusted webhook response exceeds its configured limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[8_192];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw ExecutionFailure.ExternalSideEffect(
                    "execution.webhook.response-limit",
                    "The trusted webhook response exceeds its configured limit.");
            }
        }
    }
}

public sealed class ExecutableNodeExecutor(
    WorkflowNodeSettingsValidator settingsValidator,
    TrustedActionExecutionResolver actionResolver,
    IConnectionCredentialResolver credentialResolver,
    ILogger<ExecutableNodeExecutor> logger) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Action(WorkflowNodeKind.Executable, consumesInput: true);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (ExecutableNodeSettings)settingsValidator.Parse(context.Node);
        var resolved = await actionResolver.ResolveAsync(
            settings.TrustedActionId,
            TrustedActionKind.Executable,
            cancellationToken).ConfigureAwait(false);
        var definition = (ExecutableActionDefinition)resolved.Definition;
        if (!OperatingSystem.IsWindows())
        {
            throw ExecutionFailure.Configuration(
                "execution.executable.platform",
                "Trusted executable execution is supported only on Windows.");
        }

        var inputRows = context.Inputs.Sum(input => input.DataSet.RowCount);
        var startInfo = new ProcessStartInfo
        {
            FileName = definition.ExecutablePath,
            WorkingDirectory = definition.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            LoadUserProfile = definition.LoadUserProfile,
        };
        startInfo.Environment.Clear();
        startInfo.Environment["SystemRoot"] =
            Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var item in definition.Environment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        foreach (var argument in definition.Arguments)
        {
            startInfo.ArgumentList.Add(ExpandArgument(argument, context, inputRows));
        }

        if (resolved.Action.CredentialReference is not null)
        {
            var credential = await credentialResolver.ResolveAsync(
                TrustedActionCredentialReferences.ToStorageReference(
                    resolved.Action.CredentialReference),
                cancellationToken).ConfigureAwait(false);
            if (credential is null)
            {
                throw ExecutionFailure.Configuration(
                    "execution.executable.credential-missing",
                    "The trusted executable run-as credential was not found.");
            }

            ApplyWindowsCredential(startInfo, credential);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
        logger.LogInformation(
            "Trusted executable invocation starting for run {WorkflowRunId}, node {NodeId}, action {TrustedActionId}.",
            context.WorkflowRunId,
            context.Node.Id,
            resolved.Action.Id);
        try
        {
            if (!process.Start())
            {
                throw ExecutionFailure.ExternalSideEffect(
                    "execution.executable.start",
                    "The trusted executable could not be started.");
            }

            var outputBudget = new OutputBudget(definition.MaximumOutputCharacters);
            var stdout = DrainOutputAsync(
                process.StandardOutput,
                outputBudget,
                () => KillProcessTree(process),
                timeout.Token);
            var stderr = DrainOutputAsync(
                process.StandardError,
                outputBudget,
                () => KillProcessTree(process),
                timeout.Token);
            await Task.WhenAll(
                process.WaitForExitAsync(timeout.Token),
                stdout,
                stderr).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw ExecutionFailure.ExternalSideEffect(
                    "execution.executable.exit-code",
                    $"The trusted executable returned exit code {process.ExitCode}.");
            }

            await context.Progress.ReportAsync(
                new WorkflowNodeProgress(
                    RowsRead: inputRows,
                    BatchesProcessed: 1),
                cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Trusted executable invocation completed for run {WorkflowRunId}, node {NodeId}, action {TrustedActionId}, exit code {ExitCode}.",
                context.WorkflowRunId,
                context.Node.Id,
                resolved.Action.Id,
                process.ExitCode);
            return WorkflowNodeExecutionResult.WithoutOutput;
        }
        catch (OperationCanceledException exception)
        {
            KillProcessTree(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new WorkflowNodeExecutionException(
                new(
                    "execution.executable.timeout",
                    ExecutionErrorCategory.Timeout,
                    "The trusted executable exceeded its configured timeout.",
                    isRetryable: false),
                exception);
        }
        finally
        {
            KillProcessTree(process);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsCredential(
        ProcessStartInfo startInfo,
        ConnectionCredential credential)
    {
        var separator = credential.UserName.IndexOf('\\');
        if (separator > 0 && separator < credential.UserName.Length - 1)
        {
            startInfo.Domain = credential.UserName[..separator];
            startInfo.UserName = credential.UserName[(separator + 1)..];
        }
        else
        {
            startInfo.UserName = credential.UserName;
        }

        startInfo.PasswordInClearText = credential.Password;
    }

    private static string ExpandArgument(
        string template,
        WorkflowNodeExecutionContext context,
        long inputRows) => template
        .Replace("{runId}", context.WorkflowRunId.ToString("D"), StringComparison.Ordinal)
        .Replace("{nodeId}", context.Node.Id, StringComparison.Ordinal)
        .Replace("{attempt}", context.Attempt.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{inputRowCount}", inputRows.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static async Task DrainOutputAsync(
        StreamReader reader,
        OutputBudget outputBudget,
        System.Action limitExceeded,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4_096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            if (!outputBudget.TryAdd(read))
            {
                limitExceeded();
                throw ExecutionFailure.ExternalSideEffect(
                    "execution.executable.output-limit",
                    "The trusted executable exceeded its stdout/stderr limit.");
            }
        }
    }

    private sealed class OutputBudget(long maximumCharacters)
    {
        private long consumed;

        public bool TryAdd(int characters) =>
            Interlocked.Add(ref consumed, characters) <= maximumCharacters;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
