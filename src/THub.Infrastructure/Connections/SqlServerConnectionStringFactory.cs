using Microsoft.Data.SqlClient;
using THub.Application.Connections;

namespace THub.Infrastructure.Connections;

public sealed class SqlServerConnectionStringFactory(
    IDatabaseCredentialResolver credentialResolver)
{
    public async ValueTask<SqlConnectionStringBuilder> CreateAsync(
        SqlServerConnectionConfiguration configuration,
        string applicationName,
        ApplicationIntent applicationIntent,
        bool enlist,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        DatabaseCredential? credential = null;
        if (configuration.Authentication.Kind == DatabaseAuthenticationKind.UserPassword)
        {
            credential = await credentialResolver.ResolveAsync(
                    configuration.Authentication.CredentialSecretReference!,
                    cancellationToken)
                .ConfigureAwait(false);
            if (credential is null)
            {
                throw new DatabaseCredentialUnavailableException();
            }
        }

        return Build(
            configuration,
            credential,
            applicationName,
            applicationIntent,
            enlist);
    }

    internal static SqlConnectionStringBuilder Build(
        SqlServerConnectionConfiguration configuration,
        DatabaseCredential? credential,
        string applicationName,
        ApplicationIntent applicationIntent,
        bool enlist)
    {
        var integrated = configuration.Authentication.Kind == DatabaseAuthenticationKind.Integrated;
        if (integrated == (credential is not null))
        {
            throw new InvalidOperationException(
                "Resolved credentials do not match the configured database authentication kind.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = configuration.Server,
            InitialCatalog = configuration.Database,
            IntegratedSecurity = integrated,
            Encrypt = configuration.Encrypt,
            TrustServerCertificate = configuration.TrustServerCertificate,
            ConnectTimeout = configuration.ConnectTimeoutSeconds,
            ApplicationName = applicationName,
            ApplicationIntent = applicationIntent,
            MultipleActiveResultSets = false,
            PersistSecurityInfo = false,
            Enlist = enlist,
            ConnectRetryCount = 1,
            ConnectRetryInterval = 1,
        };
        if (credential is not null)
        {
            builder.UserID = credential.UserName;
            builder.Password = credential.Password;
        }

        return builder;
    }
}

public sealed class DatabaseCredentialUnavailableException : Exception
{
    public DatabaseCredentialUnavailableException()
        : base("The referenced database credential is unavailable.")
    {
    }
}
