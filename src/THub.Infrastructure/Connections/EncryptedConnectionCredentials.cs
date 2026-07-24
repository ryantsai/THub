using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using THub.Application.Connections;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Connections;

internal sealed class EncryptedConnectionCredential
{
    private EncryptedConnectionCredential()
    {
    }

    public EncryptedConnectionCredential(
        string secretReference,
        byte[] nonce,
        byte[] ciphertext,
        byte[] authenticationTag,
        DateTimeOffset updatedAtUtc)
    {
        SecretReference = secretReference;
        Nonce = nonce;
        Ciphertext = ciphertext;
        AuthenticationTag = authenticationTag;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string SecretReference { get; private set; } = string.Empty;

    public byte[] Nonce { get; private set; } = [];

    public byte[] Ciphertext { get; private set; } = [];

    public byte[] AuthenticationTag { get; private set; } = [];

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void ReplaceWith(EncryptedConnectionCredential replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(
                SecretReference,
                replacement.SecretReference,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A credential replacement must retain its reference.",
                nameof(replacement));
        }

        Nonce = replacement.Nonce;
        Ciphertext = replacement.Ciphertext;
        AuthenticationTag = replacement.AuthenticationTag;
        UpdatedAtUtc = replacement.UpdatedAtUtc;
    }
}

internal sealed class ConnectionCredentialEncryptionKey
{
    public const string SectionName = "CredentialEncryption";

    private readonly byte[]? key;

    private ConnectionCredentialEncryptionKey(byte[]? key)
    {
        this.key = key;
    }

    public static ConnectionCredentialEncryptionKey FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configuredKey = configuration[$"{SectionName}:Key"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return new ConnectionCredentialEncryptionKey(null);
        }

        byte[] parsedKey;
        try
        {
            parsedKey = Convert.FromBase64String(configuredKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "CredentialEncryption:Key must be Base64 encoded.",
                exception);
        }

        if (parsedKey.Length != 32)
        {
            CryptographicOperations.ZeroMemory(parsedKey);
            throw new InvalidOperationException(
                "CredentialEncryption:Key must decode to exactly 32 bytes.");
        }

        return new ConnectionCredentialEncryptionKey(parsedKey);
    }

    public byte[] GetKey() =>
        key ?? throw new ConnectionCredentialProtectionException(
            "Credential encryption key is unavailable.");
}

internal sealed class ConnectionCredentialProtector(
    ConnectionCredentialEncryptionKey encryptionKey)
{
    private const int NonceSize = 12;
    private const int AuthenticationTagSize = 16;
    internal const int MaximumCiphertextBytes = 32 * 1_024;

    public EncryptedConnectionCredential Protect(
        ConnectionCredentialWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new CredentialPayload(
                write.Credential.UserName,
                write.Credential.Password));
        if (plaintext.Length > MaximumCiphertextBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new ConnectionCredentialProtectionException(
                "Connection credential payload exceeds the encrypted storage limit.");
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AuthenticationTagSize];
        var associatedData = CreateAssociatedData(write.SecretReference);
        try
        {
            using var aes = new AesGcm(
                encryptionKey.GetKey(),
                AuthenticationTagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }

        return new EncryptedConnectionCredential(
            write.SecretReference,
            nonce,
            ciphertext,
            tag,
            write.ChangedAtUtc);
    }

    public ConnectionCredential Unprotect(EncryptedConnectionCredential encrypted)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        if (encrypted.Nonce.Length != NonceSize ||
            encrypted.AuthenticationTag.Length != AuthenticationTagSize ||
            encrypted.Ciphertext.Length is < 1 or > MaximumCiphertextBytes)
        {
            throw new ConnectionCredentialProtectionException(
                "Stored connection credential envelope is invalid.");
        }

        var plaintext = new byte[encrypted.Ciphertext.Length];
        var associatedData = CreateAssociatedData(encrypted.SecretReference);
        try
        {
            using var aes = new AesGcm(
                encryptionKey.GetKey(),
                AuthenticationTagSize);
            aes.Decrypt(
                encrypted.Nonce,
                encrypted.Ciphertext,
                encrypted.AuthenticationTag,
                plaintext,
                associatedData);
            var payload = JsonSerializer.Deserialize<CredentialPayload>(plaintext)
                ?? throw new InvalidOperationException(
                    "Stored connection credential payload is empty.");
            return new ConnectionCredential(payload.UserName, payload.Password);
        }
        catch (CryptographicException exception)
        {
            throw new ConnectionCredentialProtectionException(
                "Stored connection credential could not be decrypted.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new ConnectionCredentialProtectionException(
                "Stored connection credential payload is invalid.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private static byte[] CreateAssociatedData(string secretReference) =>
        Encoding.UTF8.GetBytes(
            $"THub.ConnectionCredential:{secretReference}");

    private sealed record CredentialPayload(string UserName, string Password);
}

internal interface IEncryptedConnectionCredentialReader
{
    Task<EncryptedConnectionCredential?> FindAsync(
        string secretReference,
        CancellationToken cancellationToken);
}

internal sealed class SqlEncryptedConnectionCredentialReader(
    IDbContextFactory<THubDbContext> contextFactory)
    : IEncryptedConnectionCredentialReader
{
    public async Task<EncryptedConnectionCredential?> FindAsync(
        string secretReference,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.EncryptedConnectionCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(
                credential => credential.SecretReference == secretReference,
                cancellationToken);
    }
}

internal sealed class EncryptedConnectionCredentialResolver(
    IEncryptedConnectionCredentialReader reader,
    ConnectionCredentialProtector protector)
    : IConnectionCredentialResolver
{
    public async ValueTask<ConnectionCredential?> ResolveAsync(
        string secretReference,
        CancellationToken cancellationToken)
    {
        var normalizedReference = new DatabaseAuthenticationConfiguration(
            DatabaseAuthenticationKind.UserPassword,
            secretReference).CredentialSecretReference!;
        var encrypted = await reader.FindAsync(
            normalizedReference,
            cancellationToken);
        return encrypted is null ? null : protector.Unprotect(encrypted);
    }
}
