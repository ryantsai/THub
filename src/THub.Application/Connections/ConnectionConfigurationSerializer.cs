using System.Text;
using System.Text.Json;
using THub.Domain.Connections;

namespace THub.Application.Connections;

public sealed class ConnectionConfigurationSerializer
{
    public const int CurrentSchemaVersion = 1;

    private static readonly HashSet<string> SqlProperties =
    [
        "schemaVersion",
        "server",
        "database",
        "integratedSecurity",
        "encrypt",
        "trustServerCertificate",
        "connectTimeoutSeconds",
        "commandTimeoutSeconds",
        "maximumBatchRows"
    ];

    private static readonly HashSet<string> FileProperties =
    [
        "schemaVersion",
        "approvedRoot",
        "allowUncRoot",
        "maximumFileBytes",
        "maximumRows",
        "maximumColumns"
    ];

    public string Serialize(ConnectionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            switch (configuration)
            {
                case SqlServerConnectionConfiguration sql:
                    writer.WriteString("server", sql.Server);
                    writer.WriteString("database", sql.Database);
                    writer.WriteBoolean("integratedSecurity", true);
                    writer.WriteBoolean("encrypt", sql.Encrypt);
                    writer.WriteBoolean("trustServerCertificate", sql.TrustServerCertificate);
                    writer.WriteNumber("connectTimeoutSeconds", sql.ConnectTimeoutSeconds);
                    writer.WriteNumber("commandTimeoutSeconds", sql.CommandTimeoutSeconds);
                    writer.WriteNumber("maximumBatchRows", sql.MaximumBatchRows);
                    break;

                case FileConnectionConfiguration file:
                    writer.WriteString("approvedRoot", file.ApprovedRoot);
                    writer.WriteBoolean("allowUncRoot", file.AllowUncRoot);
                    writer.WriteNumber("maximumFileBytes", file.MaximumFileBytes);
                    writer.WriteNumber("maximumRows", file.MaximumRows);
                    writer.WriteNumber("maximumColumns", file.MaximumColumns);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(configuration),
                        configuration.GetType().Name,
                        "Connection configuration type is not supported.");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public ConnectionConfiguration Deserialize(DataConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Deserialize(connection.Kind, connection.ConfigurationJson);
    }

    public ConnectionConfiguration Deserialize(ConnectionKind kind, string configurationJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationJson);
        try
        {
            using var document = JsonDocument.Parse(configurationJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConnectionConfigurationException("Connection configuration must be an object.");
            }

            var schemaVersion = Require(root, "schemaVersion").GetInt32();
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new ConnectionConfigurationException(
                    $"Connection configuration schema version {schemaVersion} is not supported.");
            }

            return kind switch
            {
                ConnectionKind.SqlServer => ReadSql(root),
                ConnectionKind.CsvFile or ConnectionKind.ExcelFile => ReadFile(kind, root),
                _ => throw new ConnectionConfigurationException(
                    $"Connection kind '{kind}' is not supported.")
            };
        }
        catch (ConnectionConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or FormatException
            or OverflowException
            or ArgumentException)
        {
            throw new ConnectionConfigurationException(
                "Connection configuration is malformed or contains an invalid value.",
                exception);
        }
    }

    private static SqlServerConnectionConfiguration ReadSql(JsonElement root)
    {
        EnsureOnlyProperties(root, SqlProperties);
        if (!Require(root, "integratedSecurity").GetBoolean())
        {
            throw new ConnectionConfigurationException(
                "SQL Server v1 connections require Windows integrated security.");
        }

        return new SqlServerConnectionConfiguration(
            Require(root, "server").GetString() ?? string.Empty,
            Require(root, "database").GetString() ?? string.Empty,
            Require(root, "encrypt").GetBoolean(),
            Require(root, "trustServerCertificate").GetBoolean(),
            Require(root, "connectTimeoutSeconds").GetInt32(),
            Require(root, "commandTimeoutSeconds").GetInt32(),
            Require(root, "maximumBatchRows").GetInt32());
    }

    private static FileConnectionConfiguration ReadFile(ConnectionKind kind, JsonElement root)
    {
        EnsureOnlyProperties(root, FileProperties);
        return new FileConnectionConfiguration(
            kind,
            Require(root, "approvedRoot").GetString() ?? string.Empty,
            Require(root, "allowUncRoot").GetBoolean(),
            Require(root, "maximumFileBytes").GetInt64(),
            Require(root, "maximumRows").GetInt32(),
            Require(root, "maximumColumns").GetInt32());
    }

    private static JsonElement Require(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw new ConnectionConfigurationException(
                $"Required connection property '{propertyName}' is missing.");
        }

        return value;
    }

    private static void EnsureOnlyProperties(
        JsonElement root,
        IReadOnlySet<string> allowedProperties)
    {
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!encountered.Add(property.Name))
            {
                throw new ConnectionConfigurationException(
                    $"Connection property '{property.Name}' is duplicated.");
            }
            if (!allowedProperties.Contains(property.Name))
            {
                throw new ConnectionConfigurationException(
                    $"Connection property '{property.Name}' is not supported.");
            }
        }
    }
}

public sealed class ConnectionConfigurationException : Exception
{
    public ConnectionConfigurationException(string message)
        : base(message)
    {
    }

    public ConnectionConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
