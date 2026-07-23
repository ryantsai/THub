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
        int keyVersion,
        byte[] nonce,
        byte[] ciphertext,
        byte[] authenticationTag,
        DateTimeOffset updatedAtUtc)
    {
        SecretReference = secretReference;
        KeyVersion = keyVersion;
        Nonce = nonce;
        Ciphertext = ciphertext;
        AuthenticationTag = authenticationTag;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string SecretReference { get; private set; } = string.Empty;

    public int KeyVersion { get; private set; }

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

        KeyVersion = replacement.KeyVersion;
        Nonce = replacement.Nonce;
        Ciphertext = replacement.Ciphertext;
        AuthenticationTag = replacement.AuthenticationTag;
        UpdatedAtUtc = replacement.UpdatedAtUtc;
    }
}

internal sealed class ConnectionCredentialKeyRing
{
    public const string SectionName = "CredentialEncryption";

    private readonly IReadOnlyDictionary<int, byte[]> keys;

    private ConnectionCredentialKeyRing(
        int currentKeyVersion,
        IReadOnlyDictionary<int, byte[]> keys)
    {
        CurrentKeyVersion = currentKeyVersion;
        this.keys = keys;
    }

    public int CurrentKeyVersion { get; }

    public static ConnectionCredentialKeyRing FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var currentVersionValue = section["CurrentKeyVersion"];
        var keyEntries = section.GetSection("Keys").GetChildren().ToArray();

        if (string.IsNullOrWhiteSpace(currentVersionValue) && keyEntries.Length == 0)
        {
            return new ConnectionCredentialKeyRing(0, new Dictionary<int, byte[]>());
        }

        if (!int.TryParse(
                currentVersionValue,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var currentVersion) ||
            currentVersion < 1)
        {
            throw new InvalidOperationException(
                "CredentialEncryption:CurrentKeyVersion must be a positive integer.");
        }

        var parsedKeys = new Dictionary<int, byte[]>();
        foreach (var entry in keyEntries)
        {
            if (!int.TryParse(
                    entry.Key,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var version) ||
                version < 1)
            {
                throw new InvalidOperationException(
                    "CredentialEncryption key versions must be positive integers.");
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(entry.Value ?? string.Empty);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException(
                    $"CredentialEncryption:Keys:{version} must be Base64 encoded.",
                    exception);
            }

            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidOperationException(
                    $"CredentialEncryption:Keys:{version} must decode to exactly 32 bytes.");
            }

            parsedKeys.Add(version, key);
        }

        if (!parsedKeys.ContainsKey(currentVersion))
        {
            throw new InvalidOperationException(
                "CredentialEncryption:Keys must contain the configured current key version.");
        }

        return new ConnectionCredentialKeyRing(currentVersion, parsedKeys);
    }

    public byte[] GetCurrentKey() => GetKey(CurrentKeyVersion);

    public byte[] GetKey(int version)
    {
        if (!keys.TryGetValue(version, out var key))
        {
            throw new ConnectionCredentialProtectionException(
                $"Credential encryption key version {version} is unavailable.");
        }

        return key;
    }
}

internal sealed class ConnectionCredentialProtector(ConnectionCredentialKeyRing keyRing)
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
                keyRing.GetCurrentKey(),
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
            keyRing.CurrentKeyVersion,
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
                keyRing.GetKey(encrypted.KeyVersion),
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
            $"THub.ConnectionCredential.v1:{secretReference}");

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
