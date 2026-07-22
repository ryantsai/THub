namespace THub.Domain.Connections;

public enum ConnectionKind { SqlServer, CsvFile, ExcelFile }

public sealed class DataConnection
{
    private DataConnection() { }

    public DataConnection(string name, ConnectionKind kind, string configurationJson, string createdBy)
    {
        Id = Guid.NewGuid();
        Name = name;
        Kind = kind;
        ConfigurationJson = configurationJson;
        CreatedBy = createdBy;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public ConnectionKind Kind { get; private set; }
    public string ConfigurationJson { get; private set; } = "{}";
    public string CreatedBy { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
}

