using System.Security.Cryptography;
using System.Text;

namespace THub.Application.Publications;

public sealed class PublicationTokenGenerator
{
    public const string TokenPrefix = "thub_";
    public const int MaximumTokenCharacters = 128;

    public PublicationTokenMaterial Generate()
    {
        var selector = Base64UrlEncode(RandomNumberGenerator.GetBytes(12));
        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var token = $"{TokenPrefix}{selector}.{secret}";
        return new PublicationTokenMaterial(
            selector,
            token[..Math.Min(16, token.Length)],
            ComputeVerifier(token),
            token,
            PublicationTokenMaterial.CurrentAlgorithm);
    }

    public bool TryReadSelector(string? token, out string selector)
    {
        selector = string.Empty;
        if (string.IsNullOrWhiteSpace(token)
            || token.Length > MaximumTokenCharacters
            || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = token.IndexOf('.', TokenPrefix.Length);
        if (separatorIndex <= TokenPrefix.Length
            || separatorIndex == token.Length - 1
            || token.IndexOf('.', separatorIndex + 1) >= 0)
        {
            return false;
        }

        var selectorValue = token.AsSpan(TokenPrefix.Length, separatorIndex - TokenPrefix.Length);
        var secretValue = token.AsSpan(separatorIndex + 1);
        if (selectorValue.Length != 16
            || secretValue.Length != 43
            || !IsBase64Url(selectorValue)
            || !IsBase64Url(secretValue))
        {
            return false;
        }

        selector = selectorValue.ToString();
        return true;
    }

    public bool Verify(string? token, ReadOnlySpan<byte> expectedVerifier)
    {
        if (!TryReadSelector(token, out _)
            || expectedVerifier.Length != SHA256.HashSizeInBytes)
        {
            return false;
        }

        var actualVerifier = ComputeVerifier(token!);
        return CryptographicOperations.FixedTimeEquals(actualVerifier, expectedVerifier);
    }

    public byte[] ComputeVerifier(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length > MaximumTokenCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(token),
                $"Token cannot exceed {MaximumTokenCharacters} characters.");
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool IsBase64Url(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            {
                return false;
            }
        }

        return true;
    }
}

