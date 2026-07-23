using FluentFTP;
using THub.Application.Connections;

namespace THub.Infrastructure.Connections;

public sealed class FtpClientFactory(IConnectionCredentialResolver credentialResolver)
{
    public async ValueTask<AsyncFtpClient> CreateConnectedAsync(
        FtpConnectionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var credential = await credentialResolver.ResolveAsync(
                configuration.CredentialSecretReference,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConnectionCredentialUnavailableException();
        var client = new AsyncFtpClient(
            configuration.Host,
            credential.UserName,
            credential.Password,
            configuration.Port);
        client.Config.EncryptionMode = configuration.EncryptionMode switch
        {
            Application.Connections.FtpEncryptionMode.None => FluentFTP.FtpEncryptionMode.None,
            Application.Connections.FtpEncryptionMode.Explicit => FluentFTP.FtpEncryptionMode.Explicit,
            Application.Connections.FtpEncryptionMode.Implicit => FluentFTP.FtpEncryptionMode.Implicit,
            _ => throw new ArgumentOutOfRangeException(nameof(configuration))
        };
        client.Config.ConnectTimeout = configuration.ConnectTimeoutSeconds * 1_000;
        client.Config.ReadTimeout = configuration.ConnectTimeoutSeconds * 1_000;
        client.Config.DataConnectionConnectTimeout = configuration.ConnectTimeoutSeconds * 1_000;
        client.Config.ValidateAnyCertificate = configuration.TrustServerCertificate;
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
