using Microsoft.Extensions.Configuration;
using THub.Application.Connections;

namespace THub.Infrastructure.Connections;

public sealed class ConfigurationConnectionCredentialResolver(IConfiguration configuration)
    : IConnectionCredentialResolver
{
    public ValueTask<ConnectionCredential?> ResolveAsync(
        string secretReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new DatabaseAuthenticationConfiguration(
            DatabaseAuthenticationKind.UserPassword,
            secretReference);

        var section = configuration.GetSection($"ConnectionCredentials:{secretReference}");
        var userName = section["Username"];
        var password = section["Password"];
        return ValueTask.FromResult(
            string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)
                ? null
                : new ConnectionCredential(userName, password));
    }
}
