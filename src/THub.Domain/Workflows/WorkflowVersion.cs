using System.Security.Cryptography;
using System.Text;

namespace THub.Domain.Workflows;

/// <summary>
/// An immutable, published workflow graph. A run always points to one of these snapshots.
/// </summary>
public sealed class WorkflowVersion
{
    public const int MaximumGraphLength = 2_000_000;

    private WorkflowVersion() { }

    public WorkflowVersion(
        Guid workflowId,
        int version,
        int schemaVersion,
        string graphJson,
        string checksum,
        string publishedBy,
        DateTimeOffset publishedAtUtc)
    {
        WorkflowId = DomainGuard.RequireId(workflowId, nameof(workflowId));
        Version = DomainGuard.RequirePositive(version, nameof(version));
        SchemaVersion = DomainGuard.RequirePositive(schemaVersion, nameof(schemaVersion));
        GraphJson = DomainGuard.Require(graphJson, nameof(graphJson), MaximumGraphLength);
        PublishedBy = DomainGuard.Require(publishedBy, nameof(publishedBy), 256);
        PublishedAtUtc = DomainGuard.Utc(publishedAtUtc, nameof(publishedAtUtc));

        var expectedChecksum = ComputeChecksum(GraphJson);
        var suppliedChecksum = DomainGuard.Require(checksum, nameof(checksum), expectedChecksum.Length);
        if (!string.Equals(suppliedChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The checksum does not match the workflow graph snapshot.",
                nameof(checksum));
        }

        Checksum = expectedChecksum;
        Id = CreateId(WorkflowId, Version);
    }

    public Guid Id { get; private set; }

    public Guid WorkflowId { get; private set; }

    public int Version { get; private set; }

    public int SchemaVersion { get; private set; }

    public string GraphJson { get; private set; } = string.Empty;

    public string Checksum { get; private set; } = string.Empty;

    public string PublishedBy { get; private set; } = string.Empty;

    public DateTimeOffset PublishedAtUtc { get; private set; }

    public static string ComputeChecksum(string graphJson)
    {
        var value = DomainGuard.Require(graphJson, nameof(graphJson), MaximumGraphLength);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>
    /// Produces the stable identity used by the immutable version and legacy enqueue callers.
    /// </summary>
    public static Guid CreateId(Guid workflowId, int version)
    {
        DomainGuard.RequireId(workflowId, nameof(workflowId));
        DomainGuard.RequirePositive(version, nameof(version));

        var input = Encoding.UTF8.GetBytes($"thub-workflow-version:{workflowId:N}:{version}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }
}
