using THub.Application.Connections;
using THub.Domain.Connections;
using THub.Infrastructure.Publications;

namespace THub.Infrastructure.Tests;

public sealed class PublicationConnectionPolicyTests
{
    public static TheoryData<ConnectionConfiguration, ConnectionConfiguration> MatchingTargets => new()
    {
        {
            Sql("sql.example", "orders", "orders-read"),
            Sql("SQL.EXAMPLE", "ORDERS", "orders-apply")
        },
        {
            Relational(ConnectionKind.MySql, "mysql.example", 3306, "orders", "mysql-read"),
            Relational(ConnectionKind.MySql, "MYSQL.EXAMPLE", 3306, "ORDERS", "mysql-apply")
        },
        {
            Relational(ConnectionKind.PostgreSql, "pg.example", 5432, "orders", "pg-read"),
            Relational(ConnectionKind.PostgreSql, "PG.EXAMPLE", 5432, "ORDERS", "pg-apply")
        },
        {
            Relational(ConnectionKind.Oracle, "oracle.example", 1521, "orders", "oracle-read"),
            Relational(ConnectionKind.Oracle, "ORACLE.EXAMPLE", 1521, "ORDERS", "oracle-apply")
        },
    };

    [Theory]
    [MemberData(nameof(MatchingTargets))]
    public async Task ValidateAsync_AcceptsSeparateConnectionsToSameRelationalTarget(
        ConnectionConfiguration readConfiguration,
        ConnectionConfiguration applyConfiguration)
    {
        var (policy, read, apply) = CreatePolicy(readConfiguration, applyConfiguration);

        var result = await policy.ValidateAsync(
            read.Id,
            apply.Id,
            requiresApplyConnection: true,
            CancellationToken.None);

        Assert.True(result.IsValid, result.Message);
    }

    [Fact]
    public async Task ValidateAsync_RejectsDifferentDatabaseTarget()
    {
        var (policy, read, apply) = CreatePolicy(
            Sql("sql.example", "orders", "orders-read"),
            Sql("sql.example", "finance", "orders-apply"));

        var result = await policy.ValidateAsync(
            read.Id,
            apply.Id,
            requiresApplyConnection: true,
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("publication.connection_target_mismatch", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_RejectsReusedStoredCredential()
    {
        var (policy, read, apply) = CreatePolicy(
            Relational(ConnectionKind.PostgreSql, "pg.example", 5432, "orders", "shared"),
            Relational(ConnectionKind.PostgreSql, "pg.example", 5432, "orders", "shared"));

        var result = await policy.ValidateAsync(
            read.Id,
            apply.Id,
            requiresApplyConnection: true,
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("publication.apply_credential_must_be_separate", result.ErrorCode);
    }

    private static (
        PublicationConnectionPolicy Policy,
        DataConnection Read,
        DataConnection Apply) CreatePolicy(
        ConnectionConfiguration readConfiguration,
        ConnectionConfiguration applyConfiguration)
    {
        var serializer = new ConnectionConfigurationSerializer();
        var read = new DataConnection(
            "Publication read",
            readConfiguration.Kind,
            serializer.Serialize(readConfiguration),
            "tester");
        var apply = new DataConnection(
            "Publication apply",
            applyConfiguration.Kind,
            serializer.Serialize(applyConfiguration),
            "tester");
        return (
            new PublicationConnectionPolicy(
                new ConnectionStore([read, apply]),
                serializer),
            read,
            apply);
    }

    private static SqlServerConnectionConfiguration Sql(
        string server,
        string database,
        string credentialReference) =>
        new(
            server,
            database,
            authentication: Authentication(credentialReference));

    private static RelationalDatabaseConnectionConfiguration Relational(
        ConnectionKind kind,
        string host,
        int port,
        string database,
        string credentialReference) =>
        new(
            kind,
            host,
            port,
            database,
            encrypt: true,
            trustServerCertificate: false,
            authentication: Authentication(credentialReference));

    private static DatabaseAuthenticationConfiguration Authentication(string reference) =>
        new(DatabaseAuthenticationKind.UserPassword, reference);

    private sealed class ConnectionStore(IReadOnlyList<DataConnection> connections)
        : IDataConnectionStore
    {
        public Task<IReadOnlyList<DataConnection>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(connections);

        public Task<DataConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(connections.SingleOrDefault(connection => connection.Id == id));

        public Task<ConnectionSaveStatus> AddAsync(
            DataConnection connection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ConnectionSaveStatus> SaveAsync(
            DataConnection connection,
            DateTimeOffset expectedUpdatedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
