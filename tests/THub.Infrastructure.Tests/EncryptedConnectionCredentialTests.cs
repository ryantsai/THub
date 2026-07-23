using System.Text;
using Microsoft.Extensions.Configuration;
using THub.Application.Connections;
using THub.Infrastructure.Connections;

namespace THub.Infrastructure.Tests;

public sealed class EncryptedConnectionCredentialTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProtectorEncryptsAndAuthenticatesCredentialPayload()
    {
        var protector = CreateProtector(currentVersion: 1, versions: [1]);
        var encrypted = protector.Protect(new ConnectionCredentialWrite(
            "warehouse_writer",
            new ConnectionCredential("db_writer", "secret-value"),
            Now));

        var storedBytes = encrypted.Ciphertext
            .Concat(encrypted.Nonce)
            .Concat(encrypted.AuthenticationTag)
            .ToArray();
        var storedText = Encoding.UTF8.GetString(storedBytes);
        var roundTrip = protector.Unprotect(encrypted);

        Assert.DoesNotContain("db_writer", storedText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", storedText, StringComparison.Ordinal);
        Assert.Equal("db_writer", roundTrip.UserName);
        Assert.Equal("secret-value", roundTrip.Password);
        Assert.Equal(1, encrypted.KeyVersion);
        Assert.Equal(12, encrypted.Nonce.Length);
        Assert.Equal(16, encrypted.AuthenticationTag.Length);
    }

    [Fact]
    public void ProtectorRejectsCiphertextMovedToAnotherReference()
    {
        var protector = CreateProtector(currentVersion: 1, versions: [1]);
        var encrypted = protector.Protect(new ConnectionCredentialWrite(
            "warehouse_writer",
            new ConnectionCredential("db_writer", "secret-value"),
            Now));
        var moved = new EncryptedConnectionCredential(
            "another_reference",
            encrypted.KeyVersion,
            encrypted.Nonce,
            encrypted.Ciphertext,
            encrypted.AuthenticationTag,
            encrypted.UpdatedAtUtc);

        var exception = Assert.Throws<ConnectionCredentialProtectionException>(
            () => protector.Unprotect(moved));

        Assert.DoesNotContain("secret-value", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void KeyRingRetainsPreviousVersionForRotation()
    {
        var oldProtector = CreateProtector(currentVersion: 1, versions: [1]);
        var encrypted = oldProtector.Protect(new ConnectionCredentialWrite(
            "warehouse_writer",
            new ConnectionCredential("db_writer", "secret-value"),
            Now));
        var rotatedProtector = CreateProtector(currentVersion: 2, versions: [1, 2]);

        var credential = rotatedProtector.Unprotect(encrypted);

        Assert.Equal("db_writer", credential.UserName);
        Assert.Equal("secret-value", credential.Password);
    }

    [Fact]
    public async Task ResolverReturnsNullForUnknownReference()
    {
        var resolver = new EncryptedConnectionCredentialResolver(
            new StubReader(null),
            CreateProtector(currentVersion: 1, versions: [1]));

        var credential = await resolver.ResolveAsync(
            "missing",
            CancellationToken.None);

        Assert.Null(credential);
    }

    [Fact]
    public void KeyRingRejectsNon256BitKeys()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CredentialEncryption:CurrentKeyVersion"] = "1",
                ["CredentialEncryption:Keys:1"] =
                    Convert.ToBase64String(new byte[16])
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => ConnectionCredentialKeyRing.FromConfiguration(configuration));

        Assert.Contains("exactly 32 bytes", exception.Message, StringComparison.Ordinal);
    }

    private static ConnectionCredentialProtector CreateProtector(
        int currentVersion,
        int[] versions)
    {
        var values = new Dictionary<string, string?>
        {
            ["CredentialEncryption:CurrentKeyVersion"] =
                currentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        foreach (var version in versions)
        {
            values[$"CredentialEncryption:Keys:{version}"] =
                Convert.ToBase64String(
                    Enumerable.Repeat((byte)version, 32).ToArray());
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return new ConnectionCredentialProtector(
            ConnectionCredentialKeyRing.FromConfiguration(configuration));
    }

    private sealed class StubReader(EncryptedConnectionCredential? credential)
        : IEncryptedConnectionCredentialReader
    {
        public Task<EncryptedConnectionCredential?> FindAsync(
            string secretReference,
            CancellationToken cancellationToken) =>
            Task.FromResult(credential);
    }
}
