using THub.Domain.Connections;

namespace THub.Domain.Tests;

public sealed class DataConnectionTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 23, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConnectionNormalizesMetadataAndSupportsAuditedLifecycle()
    {
        var connection = new DataConnection(
            "  Warehouse  ",
            ConnectionKind.SqlServer,
            "{\"schemaVersion\":1,\"server\":\"sql01\"}",
            "  DOMAIN\\admin  ",
            CreatedAt);

        Assert.Equal("Warehouse", connection.Name);
        Assert.Equal("DOMAIN\\admin", connection.CreatedBy);
        Assert.True(connection.IsEnabled);

        connection.Disable(CreatedAt.AddMinutes(1));
        connection.UpdateConfiguration(
            "{\"schemaVersion\":1,\"server\":\"sql02\"}",
            CreatedAt.AddMinutes(2));

        Assert.False(connection.IsEnabled);
        Assert.Equal(CreatedAt.AddMinutes(2), connection.UpdatedAtUtc);
    }

    [Fact]
    public void DeleteDisablesConnectionAndPreservesDeletionTimestamp()
    {
        var connection = new DataConnection(
            "Warehouse",
            ConnectionKind.SqlServer,
            "{\"schemaVersion\":1,\"server\":\"sql01\"}",
            "DOMAIN\\admin",
            CreatedAt);

        connection.Delete(CreatedAt.AddMinutes(1));

        Assert.True(connection.IsDeleted);
        Assert.False(connection.IsEnabled);
        Assert.Equal(CreatedAt.AddMinutes(1), connection.DeletedAtUtc);
        Assert.Equal(CreatedAt.AddMinutes(1), connection.UpdatedAtUtc);
        Assert.Throws<InvalidOperationException>(
            () => connection.Delete(CreatedAt.AddMinutes(2)));
    }

    [Theory]
    [InlineData("{\"password\":\"do-not-store\"}")]
    [InlineData("{\"nested\":{\"apiKey\":\"do-not-store\"}}")]
    [InlineData("{\"connectionString\":\"Server=.;Password=x\"}")]
    public void ConnectionRejectsInlineSecrets(string configurationJson)
    {
        Assert.Throws<ArgumentException>(() => new DataConnection(
            "Warehouse",
            ConnectionKind.SqlServer,
            configurationJson,
            "DOMAIN\\admin",
            CreatedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{trailing:}")]
    public void ConnectionRejectsMalformedConfiguration(string configurationJson)
    {
        Assert.ThrowsAny<ArgumentException>(() => new DataConnection(
            "Warehouse",
            ConnectionKind.SqlServer,
            configurationJson,
            "DOMAIN\\admin",
            CreatedAt));
    }
}
