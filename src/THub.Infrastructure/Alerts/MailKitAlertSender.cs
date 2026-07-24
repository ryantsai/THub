using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using THub.Application.Alerts;
using THub.Domain.Alerts;
using THub.Domain.Runs;

namespace THub.Infrastructure.Alerts;

public sealed class MailKitAlertSender(
    ISecretResolver secretResolver,
    SmtpAlertSenderOptions options,
    ILogger<MailKitAlertSender>? logger = null) : IAlertSender
{
    private readonly ISecretResolver _secretResolver =
        secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
    private readonly TimeSpan _operationTimeout =
        (options ?? throw new ArgumentNullException(nameof(options)))
        .GetValidatedOperationTimeout();

    public async ValueTask<AlertSendResult> SendAsync(
        EmailDeliveryProfile profile,
        AlertDelivery delivery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(delivery);
        cancellationToken.ThrowIfCancellationRequested();

        if (delivery.EmailDeliveryProfileId != profile.Id)
        {
            return ConfigurationFailure(
                "email.profile_mismatch",
                "The delivery does not belong to the selected Email profile.");
        }

        try
        {
            profile.ValidateMessage(delivery.Message);
        }
        catch (InvalidOperationException)
        {
            return ConfigurationFailure(
                profile.IsEnabled ? "email.message_policy" : "email.profile_disabled",
                profile.IsEnabled
                    ? "The Email message does not satisfy its delivery profile policy."
                    : "The Email delivery profile is disabled.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        SmtpCredential? credential = null;
        try
        {
            if (profile.CredentialSecretReference is not null)
            {
                credential = await _secretResolver.ResolveSmtpCredentialAsync(
                        profile.CredentialSecretReference,
                        timeout.Token)
                    .ConfigureAwait(false);
                if (credential is null)
                {
                    return ConfigurationFailure(
                        "email.secret_unavailable",
                        "The SMTP credential reference could not be resolved.");
                }
            }

            var message = CreateMimeMessage(profile, delivery);
            using var client = new SmtpClient
            {
                CheckCertificateRevocation = true,
                Timeout = checked((int)_operationTimeout.TotalMilliseconds)
            };
            try
            {
                await client.ConnectAsync(
                        profile.SmtpHost,
                        profile.SmtpPort,
                        ToSecureSocketOptions(profile.TransportSecurity),
                        timeout.Token)
                    .ConfigureAwait(false);
                if (credential is not null)
                {
                    await client.AuthenticateAsync(
                            credential.UserName,
                            credential.Password,
                            timeout.Token)
                        .ConfigureAwait(false);
                }

                await client.SendAsync(message, timeout.Token).ConfigureAwait(false);

                // SMTP accepted the message. Disconnect failure must not convert this into a
                // retry because doing so would knowingly increase duplicate delivery risk.
                try
                {
                    await client.DisconnectAsync(quit: true, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Disposing the client closes the connection. No secret or response is logged.
                }

                return AlertSendResult.Success();
            }
            finally
            {
                if (client.IsConnected)
                {
                    try
                    {
                        await client.DisconnectAsync(
                                quit: false,
                                CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Best-effort cleanup only; disposal follows immediately.
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AlertSendResult.Failure(new ExecutionError(
                "smtp.timeout",
                ExecutionErrorCategory.Timeout,
                "The SMTP operation timed out.",
                isRetryable: true));
        }
        catch (AuthenticationException)
        {
            return AlertSendResult.Failure(new ExecutionError(
                "smtp.authentication_failed",
                ExecutionErrorCategory.Authentication,
                "The SMTP relay rejected authentication.",
                isRetryable: false));
        }
        catch (SslHandshakeException)
        {
            return AlertSendResult.Failure(new ExecutionError(
                "smtp.tls_failed",
                ExecutionErrorCategory.Configuration,
                "The required SMTP TLS handshake failed.",
                isRetryable: false));
        }
        catch (NotSupportedException)
        {
            return AlertSendResult.Failure(new ExecutionError(
                "smtp.tls_unavailable",
                ExecutionErrorCategory.Configuration,
                "The SMTP relay does not support the required transport security.",
                isRetryable: false));
        }
        catch (SmtpCommandException exception)
        {
            var result = ClassifyCommandFailure(
                (int)exception.StatusCode,
                exception.ErrorCode);
            logger?.LogError(
                "SMTP relay rejected Email delivery {EmailDeliveryId} for workflow run {WorkflowRunId} and node {NodeId} with status {SmtpStatusCode}, command error {SmtpErrorCode}, normalized error {ErrorCode}, category {ErrorCategory}, and retryable {IsRetryable}.",
                delivery.Id,
                delivery.WorkflowRunId,
                delivery.WorkflowNodeId,
                (int)exception.StatusCode,
                exception.ErrorCode,
                result.Error!.Code,
                result.Error.Category,
                result.Error.IsRetryable);
            return result;
        }
        catch (Exception exception) when (
            exception is SmtpProtocolException or IOException or SocketException)
        {
            return AlertSendResult.Failure(new ExecutionError(
                "smtp.connectivity",
                ExecutionErrorCategory.Connectivity,
                "The SMTP relay connection failed.",
                isRetryable: true));
        }
        catch (Exception)
        {
            return AlertSendResult.Failure(new ExecutionError(
                "smtp.unexpected",
                ExecutionErrorCategory.Unknown,
                "The SMTP adapter failed unexpectedly.",
                isRetryable: true));
        }
    }

    private static MimeMessage CreateMimeMessage(
        EmailDeliveryProfile profile,
        AlertDelivery delivery)
    {
        var message = new MimeMessage
        {
            MessageId = delivery.StableMessageId,
            Subject = delivery.Message.Subject
        };
        var body = new BodyBuilder();
        if (delivery.Message.IsBodyHtml)
        {
            body.HtmlBody = delivery.Message.Body;
        }
        else
        {
            body.TextBody = delivery.Message.Body;
        }
        if (delivery.Message.Attachment is { } attachment)
        {
            body.Attachments.Add(
                attachment.FileName,
                attachment.Content.ToArray(),
                ContentType.Parse(attachment.MediaType));
        }
        message.Body = body.ToMessageBody();
        message.From.Add(MailboxAddress.Parse(profile.SenderAddress));
        foreach (var recipient in delivery.Message.Recipients)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        return message;
    }

    private static SecureSocketOptions ToSecureSocketOptions(
        EmailTransportSecurity security) => security switch
        {
            EmailTransportSecurity.StartTlsRequired => SecureSocketOptions.StartTls,
            EmailTransportSecurity.ImplicitTls => SecureSocketOptions.SslOnConnect,
            _ => throw new ArgumentOutOfRangeException(nameof(security))
        };

    internal static AlertSendResult ClassifyCommandFailure(
        int statusCode,
        SmtpErrorCode errorCode)
    {
        var isTransient = statusCode is >= 400 and < 500;
        var isMessageTooLarge = statusCode == 552;
        return AlertSendResult.Failure(new ExecutionError(
            isMessageTooLarge
                ? "smtp.message_too_large"
                : isTransient
                    ? "smtp.temporary_rejection"
                    : "smtp.rejected",
            isMessageTooLarge
                ? ExecutionErrorCategory.ResourceLimit
                : errorCode == SmtpErrorCode.UnexpectedStatusCode
                    ? ExecutionErrorCategory.Connectivity
                    : ExecutionErrorCategory.ExternalSideEffect,
            isMessageTooLarge
                ? "The SMTP relay rejected the message because it is too large."
                : isTransient
                ? "The SMTP relay temporarily rejected the message."
                : "The SMTP relay permanently rejected the message.",
            isTransient && !isMessageTooLarge));
    }

    private static AlertSendResult ConfigurationFailure(string code, string summary) =>
        AlertSendResult.Failure(new ExecutionError(
            code,
            ExecutionErrorCategory.Configuration,
            summary,
            isRetryable: false));
}
