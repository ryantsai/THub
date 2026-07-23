using THub.Domain.Connections;

namespace THub.Application.Connections;

public abstract record ConnectionConfiguration(ConnectionKind Kind);

public enum DatabaseAuthenticationKind
{
    Integrated,
    UserPassword
}

public sealed record DatabaseAuthenticationConfiguration
{
    public DatabaseAuthenticationConfiguration(
        DatabaseAuthenticationKind kind,
        string? credentialSecretReference = null)
    {
        if (kind == DatabaseAuthenticationKind.Integrated)
        {
            if (!string.IsNullOrWhiteSpace(credentialSecretReference))
            {
                throw new ArgumentException(
                    "Integrated authentication cannot reference a database credential.",
                    nameof(credentialSecretReference));
            }

            Kind = kind;
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(credentialSecretReference);
        var normalized = credentialSecretReference.Trim();
        if (normalized.Length > 200 ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "Credential references may contain only letters, numbers, dots, hyphens, and underscores.",
                nameof(credentialSecretReference));
        }

        Kind = kind;
        CredentialSecretReference = normalized;
    }

    public DatabaseAuthenticationKind Kind { get; }

    public string? CredentialSecretReference { get; }

    public static DatabaseAuthenticationConfiguration Integrated { get; } =
        new(DatabaseAuthenticationKind.Integrated);
}

public sealed class ConnectionCredential
{
    public ConnectionCredential(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);
        if (userName.Length > 256 || password.Length is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                userName.Length > 256 ? nameof(userName) : nameof(password));
        }

        UserName = userName;
        Password = password;
    }

    public string UserName { get; }

    /// <summary>Secret material. Consumers must never persist, return, or log it.</summary>
    public string Password { get; }
}

public interface IConnectionCredentialResolver
{
    ValueTask<ConnectionCredential?> ResolveAsync(
        string secretReference,
        CancellationToken cancellationToken);
}

public sealed record RelationalDatabaseConnectionConfiguration : ConnectionConfiguration
{
    public RelationalDatabaseConnectionConfiguration(
        ConnectionKind kind,
        string host,
        int port,
        string database,
        bool encrypt,
        bool trustServerCertificate,
        int connectTimeoutSeconds = 15,
        int commandTimeoutSeconds = 30,
        int maximumBatchRows = 1_000,
        DatabaseAuthenticationConfiguration? authentication = null)
        : base(kind)
    {
        if (kind is not (ConnectionKind.MySql or ConnectionKind.PostgreSql or ConnectionKind.Oracle))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Host = ConnectionConfigurationValidation.RequireEndpoint(host, nameof(host), 253);
        Port = ConnectionConfigurationValidation.InRange(port, 1, 65_535, nameof(port));
        Database = ConnectionConfigurationValidation.RequireEndpoint(database, nameof(database), 128);
        Encrypt = encrypt;
        TrustServerCertificate = trustServerCertificate;
        ConnectTimeoutSeconds = ConnectionConfigurationValidation.InRange(connectTimeoutSeconds, 1, 60, nameof(connectTimeoutSeconds));
        CommandTimeoutSeconds = ConnectionConfigurationValidation.InRange(commandTimeoutSeconds, 1, 300, nameof(commandTimeoutSeconds));
        MaximumBatchRows = ConnectionConfigurationValidation.InRange(maximumBatchRows, 1, 10_000, nameof(maximumBatchRows));
        Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        if (Authentication.Kind != DatabaseAuthenticationKind.UserPassword)
        {
            throw new ArgumentException(
                $"{kind} connections require referenced username/password authentication.",
                nameof(authentication));
        }
    }

    public string Host { get; }
    public int Port { get; }
    public string Database { get; }
    public bool Encrypt { get; }
    public bool TrustServerCertificate { get; }
    public int ConnectTimeoutSeconds { get; }
    public int CommandTimeoutSeconds { get; }
    public int MaximumBatchRows { get; }
    public DatabaseAuthenticationConfiguration Authentication { get; }
}

public enum FtpEncryptionMode
{
    None,
    Explicit,
    Implicit
}

public sealed record FtpConnectionConfiguration : ConnectionConfiguration
{
    public FtpConnectionConfiguration(
        string host,
        int port,
        FtpEncryptionMode encryptionMode,
        bool trustServerCertificate,
        string credentialSecretReference,
        int connectTimeoutSeconds = 15,
        long maximumFileBytes = 256L * 1_024 * 1_024,
        int maximumRows = 1_000_000,
        int maximumColumns = 1_024)
        : base(ConnectionKind.Ftp)
    {
        Host = ConnectionConfigurationValidation.RequireEndpoint(host, nameof(host), 253);
        Port = ConnectionConfigurationValidation.InRange(port, 1, 65_535, nameof(port));
        if (!Enum.IsDefined(encryptionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(encryptionMode));
        }

        EncryptionMode = encryptionMode;
        TrustServerCertificate = trustServerCertificate;
        CredentialSecretReference = new DatabaseAuthenticationConfiguration(
            DatabaseAuthenticationKind.UserPassword,
            credentialSecretReference).CredentialSecretReference!;
        ConnectTimeoutSeconds = ConnectionConfigurationValidation.InRange(connectTimeoutSeconds, 1, 60, nameof(connectTimeoutSeconds));
        if (maximumFileBytes is < 1_024 or > 4L * 1_024 * 1_024 * 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }
        MaximumFileBytes = maximumFileBytes;
        MaximumRows = ConnectionConfigurationValidation.InRange(maximumRows, 1, 10_000_000, nameof(maximumRows));
        MaximumColumns = ConnectionConfigurationValidation.InRange(maximumColumns, 1, 16_384, nameof(maximumColumns));
    }

    public string Host { get; }
    public int Port { get; }
    public FtpEncryptionMode EncryptionMode { get; }
    public bool TrustServerCertificate { get; }
    public string CredentialSecretReference { get; }
    public int ConnectTimeoutSeconds { get; }
    public long MaximumFileBytes { get; }
    public int MaximumRows { get; }
    public int MaximumColumns { get; }
}

internal static class ConnectionConfigurationValidation
{
    public static string RequireEndpoint(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength ||
            normalized.Any(character => char.IsControl(character) || character is ';' or '='))
        {
            throw new ArgumentException("Connection value contains unsupported characters.", parameterName);
        }
        return normalized;
    }

    public static int InRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return value;
    }
}

public sealed record SqlServerConnectionConfiguration : ConnectionConfiguration
{
    public SqlServerConnectionConfiguration(
        string server,
        string database,
        bool encrypt = true,
        bool trustServerCertificate = false,
        int connectTimeoutSeconds = 15,
        int commandTimeoutSeconds = 30,
        int maximumBatchRows = 1_000,
        DatabaseAuthenticationConfiguration? authentication = null)
        : base(ConnectionKind.SqlServer)
    {
        Server = Require(server, nameof(server), 253);
        Database = Require(database, nameof(database), 128);
        if (Server.Any(character => char.IsControl(character) || character is ';' or '='))
        {
            throw new ArgumentException("SQL Server name contains unsupported characters.", nameof(server));
        }
        if (Database.Any(character => char.IsControl(character) || character is ';' or '='))
        {
            throw new ArgumentException("Database name contains unsupported characters.", nameof(database));
        }
        if (!encrypt && !Server.StartsWith("(localdb)\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "SQL Server connections require transport encryption outside LocalDB.",
                nameof(encrypt));
        }

        Encrypt = encrypt;
        TrustServerCertificate = trustServerCertificate;
        ConnectTimeoutSeconds = InRange(connectTimeoutSeconds, 1, 60, nameof(connectTimeoutSeconds));
        CommandTimeoutSeconds = InRange(commandTimeoutSeconds, 1, 300, nameof(commandTimeoutSeconds));
        MaximumBatchRows = InRange(maximumBatchRows, 1, 10_000, nameof(maximumBatchRows));
        Authentication = authentication ?? DatabaseAuthenticationConfiguration.Integrated;
    }

    public string Server { get; }

    public string Database { get; }

    public bool Encrypt { get; }

    public bool TrustServerCertificate { get; }

    public int ConnectTimeoutSeconds { get; }

    public int CommandTimeoutSeconds { get; }

    public int MaximumBatchRows { get; }

    public DatabaseAuthenticationConfiguration Authentication { get; }

    private static string Require(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return normalized;
    }

    private static int InRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public sealed record FileConnectionConfiguration : ConnectionConfiguration
{
    public FileConnectionConfiguration(
        ConnectionKind kind,
        string approvedRoot,
        bool allowUncRoot = false,
        long maximumFileBytes = 256L * 1_024 * 1_024,
        int maximumRows = 1_000_000,
        int maximumColumns = 1_024)
        : base(kind)
    {
        if (kind is not (ConnectionKind.CsvFile or ConnectionKind.ExcelFile))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(approvedRoot);
        if (approvedRoot.Length > 1_024 || approvedRoot.Any(char.IsControl))
        {
            throw new ArgumentException("Approved root is invalid or too long.", nameof(approvedRoot));
        }

        if (maximumFileBytes is < 1_024 or > 4L * 1_024 * 1_024 * 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }
        if (maximumRows is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        }
        if (maximumColumns is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumColumns));
        }

        ApprovedRoot = approvedRoot.Trim();
        AllowUncRoot = allowUncRoot;
        MaximumFileBytes = maximumFileBytes;
        MaximumRows = maximumRows;
        MaximumColumns = maximumColumns;
    }

    public string ApprovedRoot { get; }

    public bool AllowUncRoot { get; }

    public long MaximumFileBytes { get; }

    public int MaximumRows { get; }

    public int MaximumColumns { get; }
}
