using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using THub.Application.Connections;
using THub.Domain.Connections;
using THub.Infrastructure.Connections;

namespace THub.Infrastructure.Tests;

public sealed class RelationalConnectionFactoryTests
{
    [Theory]
    [InlineData(ConnectionKind.MySql)]
    [InlineData(ConnectionKind.PostgreSql)]
    [InlineData(ConnectionKind.Oracle)]
    public async Task CreateAsync_UsesProviderSpecificConnectionAndReferencedCredential(
        ConnectionKind kind)
    {
        var factory = new RelationalConnectionFactory(
            new StubCredentialResolver(new ConnectionCredential("reader", "not-logged")));
        var configuration = new RelationalDatabaseConnectionConfiguration(
            kind,
            "db.example.test",
            kind switch
            {
                ConnectionKind.MySql => 3306,
                ConnectionKind.PostgreSql => 5432,
                ConnectionKind.Oracle => 1521,
                _ => throw new InvalidOperationException()
            },
            "Warehouse",
            encrypt: false,
            trustServerCertificate: false,
            authentication: new DatabaseAuthenticationConfiguration(
                DatabaseAuthenticationKind.UserPassword,
                "warehouse_reader"));

        await using var connection = await factory.CreateAsync(configuration, CancellationToken.None);

        switch (kind)
        {
            case ConnectionKind.MySql:
                Assert.IsType<MySqlConnection>(connection);
                var mysql = new MySqlConnectionStringBuilder(connection.ConnectionString);
                Assert.Equal("reader", mysql.UserID);
                Assert.Equal("Warehouse", mysql.Database);
                break;
            case ConnectionKind.PostgreSql:
                Assert.IsType<NpgsqlConnection>(connection);
                var postgres = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
                Assert.Equal("reader", postgres.Username);
                Assert.Equal("Warehouse", postgres.Database);
                break;
            case ConnectionKind.Oracle:
                Assert.IsType<OracleConnection>(connection);
                var oracle = new OracleConnectionStringBuilder(connection.ConnectionString);
                Assert.Equal("reader", oracle.UserID);
                Assert.Contains("SERVICE_NAME=Warehouse", oracle.DataSource, StringComparison.Ordinal);
                break;
        }
    }

    [Fact]
    public async Task CreateAsync_FailsClosedWhenCredentialIsUnavailable()
    {
        var factory = new RelationalConnectionFactory(new StubCredentialResolver(null));
        var configuration = new RelationalDatabaseConnectionConfiguration(
            ConnectionKind.PostgreSql,
            "db.example.test",
            5432,
            "Warehouse",
            encrypt: true,
            trustServerCertificate: false,
            authentication: new DatabaseAuthenticationConfiguration(
                DatabaseAuthenticationKind.UserPassword,
                "missing"));

        await Assert.ThrowsAsync<ConnectionCredentialUnavailableException>(
            async () => await factory.CreateAsync(configuration, CancellationToken.None));
    }

    private sealed class StubCredentialResolver(ConnectionCredential? credential)
        : IConnectionCredentialResolver
    {
        public ValueTask<ConnectionCredential?> ResolveAsync(
            string secretReference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(string.IsNullOrWhiteSpace(secretReference));
            return ValueTask.FromResult(credential);
        }
    }
}
