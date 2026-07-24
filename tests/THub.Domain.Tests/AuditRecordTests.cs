using THub.Domain.Auditing;

namespace THub.Domain.Tests;

public sealed class AuditRecordTests
{
    [Fact]
    public void AuditRecordNormalizesMachineNamesAndPreservesSafeIdentifiers()
    {
        var occurredAt = new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.FromHours(8));
        var record = new AuditRecord(
            Guid.NewGuid(),
            occurredAt,
            AuditActorKind.User,
            "DOMAIN\\operator",
            "THub.Web",
            "Workflow.Published",
            AuditOutcome.Succeeded,
            "Workflow",
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"));

        Assert.Equal(occurredAt.ToUniversalTime(), record.OccurredAtUtc);
        Assert.Equal("thub.web", record.Source);
        Assert.Equal("workflow.published", record.Action);
        Assert.Equal("workflow", record.ResourceType);
        Assert.Equal("DOMAIN\\operator", record.ActorIdentifier);
    }

    [Theory]
    [InlineData("workflow published")]
    [InlineData("workflow/published")]
    [InlineData("workflow:published")]
    public void AuditRecordRejectsNonMachineReadableAction(string action)
    {
        Assert.Throws<ArgumentException>(() => new AuditRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            AuditActorKind.User,
            "DOMAIN\\operator",
            "thub.web",
            action,
            AuditOutcome.Succeeded,
            "workflow"));
    }
}
