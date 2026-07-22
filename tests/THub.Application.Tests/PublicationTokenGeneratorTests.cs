using THub.Application.Publications;

namespace THub.Application.Tests;

public sealed class PublicationTokenGeneratorTests
{
    private readonly PublicationTokenGenerator generator = new();

    [Fact]
    public void GeneratedTokensAreUniqueAndSelfVerifying()
    {
        var first = generator.Generate();
        var second = generator.Generate();

        Assert.NotEqual(first.PlaintextToken, second.PlaintextToken);
        Assert.NotEqual(first.Selector, second.Selector);
        Assert.Equal(PublicationTokenMaterial.CurrentAlgorithm, first.Algorithm);
        Assert.True(generator.TryReadSelector(first.PlaintextToken, out var selector));
        Assert.Equal(first.Selector, selector);
        Assert.True(generator.Verify(first.PlaintextToken, first.Verifier));
        Assert.False(generator.Verify(second.PlaintextToken, first.Verifier));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("secret-in-query-string")]
    [InlineData("thub_short.short")]
    [InlineData("thub_abcdefghijklmnop.abcdefghijklmnopqrstuvwxyz0123456789_-ABCDE.extra")]
    public void MalformedTokensAreRejected(string? token)
    {
        Assert.False(generator.TryReadSelector(token, out _));
        Assert.False(generator.Verify(token, new byte[32]));
    }

    [Fact]
    public void VerifierDoesNotContainPlaintext()
    {
        var material = generator.Generate();

        Assert.Equal(32, material.Verifier.Length);
        Assert.DoesNotContain(
            material.PlaintextToken,
            Convert.ToHexString(material.Verifier),
            StringComparison.OrdinalIgnoreCase);
    }
}
