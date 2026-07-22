namespace THub.Infrastructure.Files;

public sealed class ApprovedPathResolver
{
    public string ResolveFile(
        string configuredRoot,
        string relativePath,
        bool allowUncRoot = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        if (IsDevicePath(root) || (!allowUncRoot && IsUncPath(root)))
        {
            throw new InvalidOperationException("The configured file root is not an approved path type.");
        }
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The configured file root does not exist.");
        }

        if (Path.IsPathRooted(relativePath)
            || IsDevicePath(relativePath)
            || relativePath.Contains(':'))
        {
            throw new InvalidOperationException("Workflow file paths must be relative to an approved root.");
        }

        var resolved = Path.GetFullPath(relativePath, root);
        var relativeToRoot = Path.GetRelativePath(root, resolved);
        if (relativeToRoot.Equals("..", StringComparison.Ordinal)
            || relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relativeToRoot))
        {
            throw new InvalidOperationException("The workflow file path escapes its approved root.");
        }

        RejectExistingReparsePoints(root, relativeToRoot);
        return resolved;
    }

    private static void RejectExistingReparsePoints(string root, string relativePath)
    {
        var current = root;
        RejectReparsePoint(current);

        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                break;
            }

            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Approved file paths cannot traverse symbolic links or other reparse points.");
        }
    }

    private static bool IsDevicePath(string path) =>
        path.StartsWith("\\\\?\\", StringComparison.Ordinal)
        || path.StartsWith("\\\\.\\", StringComparison.Ordinal)
        || path.StartsWith("\\??\\", StringComparison.Ordinal);

    private static bool IsUncPath(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal);
}
