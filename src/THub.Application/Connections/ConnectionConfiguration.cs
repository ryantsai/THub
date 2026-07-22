using THub.Domain.Connections;

namespace THub.Application.Connections;

public abstract record ConnectionConfiguration(ConnectionKind Kind);

public sealed record SqlServerConnectionConfiguration : ConnectionConfiguration
{
    public SqlServerConnectionConfiguration(
        string server,
        string database,
        bool encrypt = true,
        bool trustServerCertificate = false,
        int connectTimeoutSeconds = 15,
        int commandTimeoutSeconds = 30,
        int maximumBatchRows = 1_000)
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
    }

    public string Server { get; }

    public string Database { get; }

    public bool Encrypt { get; }

    public bool TrustServerCertificate { get; }

    public int ConnectTimeoutSeconds { get; }

    public int CommandTimeoutSeconds { get; }

    public int MaximumBatchRows { get; }

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
