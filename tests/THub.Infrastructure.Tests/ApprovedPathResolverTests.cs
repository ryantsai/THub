using THub.Infrastructure.Files;

namespace THub.Infrastructure.Tests;

public sealed class ApprovedPathResolverTests
{
    private readonly ApprovedPathResolver resolver = new();

    [Fact]
    public void ResolvesRelativeFileInsideApprovedRoot()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var result = resolver.ResolveFile(root, Path.Combine("inbound", "data.csv"));

            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "inbound", "data.csv")),
                result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("..\\secret.txt")]
    [InlineData("sub\\..\\..\\secret.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\file.csv")]
    [InlineData("\\\\?\\C:\\file.csv")]
    public void RejectsPathsOutsideApprovedRoot(string untrustedPath)
    {
        var root = CreateTemporaryRoot();
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                resolver.ResolveFile(root, untrustedPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"thub-path-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
