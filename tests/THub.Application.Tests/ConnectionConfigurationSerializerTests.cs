using THub.Application.Connections;
using THub.Domain.Connections;

namespace THub.Application.Tests;

public sealed class ConnectionConfigurationSerializerTests
{
    private readonly ConnectionConfigurationSerializer serializer = new();

    [Fact]
    public void SqlConfigurationRoundTripsWithCredentialReferenceButNoSecret()
    {
        var input = new SqlServerConnectionConfiguration(
            "sql01.contoso.test",
            "Warehouse",
            encrypt: true,
            trustServerCertificate: false,
            maximumBatchRows: 500,
            authentication: new DatabaseAuthenticationConfiguration(
                DatabaseAuthenticationKind.UserPassword,
                "warehouse_reader"));

        var json = serializer.Serialize(input);
        var output = Assert.IsType<SqlServerConnectionConfiguration>(
            serializer.Deserialize(ConnectionKind.SqlServer, json));

        Assert.Equal(input, output);
        Assert.Contains("\"authenticationKind\":\"userPassword\"", json, StringComparison.Ordinal);
        Assert.Contains("\"credentialSecretReference\":\"warehouse_reader\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\":", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileConfigurationRoundTripsWithBounds()
    {
        var input = new FileConnectionConfiguration(
            ConnectionKind.ExcelFile,
            "D:\\THub\\Inbound",
            maximumRows: 25_000);

        var json = serializer.Serialize(input);
        var output = Assert.IsType<FileConnectionConfiguration>(
            serializer.Deserialize(ConnectionKind.ExcelFile, json));

        Assert.Equal(input, output);
    }

    [Theory]
    [InlineData(ConnectionKind.MySql, 3306)]
    [InlineData(ConnectionKind.PostgreSql, 5432)]
    [InlineData(ConnectionKind.Oracle, 1521)]
    public void RelationalConfigurationRoundTripsWithoutSecretValues(
        ConnectionKind kind,
        int port)
    {
        var input = new RelationalDatabaseConnectionConfiguration(
            kind,
            "db.contoso.test",
            port,
            "Warehouse",
            encrypt: true,
            trustServerCertificate: false,
            authentication: new DatabaseAuthenticationConfiguration(
                DatabaseAuthenticationKind.UserPassword,
                "warehouse_reader"));

        var json = serializer.Serialize(input);
        var output = Assert.IsType<RelationalDatabaseConnectionConfiguration>(
            serializer.Deserialize(kind, json));

        Assert.Equal(input, output);
        Assert.DoesNotContain("\"password\":", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(FtpEncryptionMode.None)]
    [InlineData(FtpEncryptionMode.Explicit)]
    [InlineData(FtpEncryptionMode.Implicit)]
    public void FtpConfigurationRoundTrips(FtpEncryptionMode encryptionMode)
    {
        var input = new FtpConnectionConfiguration(
            "ftp.contoso.test",
            21,
            encryptionMode,
            trustServerCertificate: false,
            "partner_ftp");

        var json = serializer.Serialize(input);
        var output = Assert.IsType<FtpConnectionConfiguration>(
            serializer.Deserialize(ConnectionKind.Ftp, json));

        Assert.Equal(input, output);
        Assert.DoesNotContain("\"password\":", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1,\"server\":\"sql01\",\"database\":\"db\",\"integratedSecurity\":false,\"encrypt\":true,\"trustServerCertificate\":false,\"connectTimeoutSeconds\":15,\"commandTimeoutSeconds\":30,\"maximumBatchRows\":1000}")]
    [InlineData("{\"schemaVersion\":1,\"server\":\"sql01\",\"database\":\"db\",\"authenticationKind\":\"userPassword\",\"credentialSecretReference\":\"safe\",\"encrypt\":true,\"trustServerCertificate\":false,\"connectTimeoutSeconds\":15,\"commandTimeoutSeconds\":30,\"maximumBatchRows\":1000,\"password\":\"x\"}")]
    public void SqlConfigurationRejectsUnsupportedOrUnsafeDocuments(string json)
    {
        Assert.Throws<ConnectionConfigurationException>(() =>
            serializer.Deserialize(ConnectionKind.SqlServer, json));
    }

}
