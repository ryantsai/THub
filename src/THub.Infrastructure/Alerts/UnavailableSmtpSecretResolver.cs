using THub.Application.Alerts;

namespace THub.Infrastructure.Alerts;

/// <summary>
/// A fail-closed resolver for deployments that use only an anonymous approved relay. Any profile
/// containing a credential reference is rejected until the host replaces this registration with
/// an organization-approved secret provider.
/// </summary>
public sealed class UnavailableSmtpSecretResolver : ISecretResolver
{
    public ValueTask<SmtpCredential?> ResolveSmtpCredentialAsync(
        string secretReference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<SmtpCredential?>(null);
    }
}
