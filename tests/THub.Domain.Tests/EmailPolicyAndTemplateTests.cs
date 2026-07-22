using THub.Domain.Alerts;

namespace THub.Domain.Tests;

public sealed class EmailPolicyAndTemplateTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 23, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(".ops@example.com")]
    [InlineData("ops..team@example.com")]
    [InlineData("ops,admin@example.com")]
    [InlineData("ops@example..com")]
    [InlineData("ops@-example.com")]
    public void RejectsNonDeterministicOrUnsafeRecipientSyntax(string address)
    {
        Assert.Throws<ArgumentException>(() =>
            new EmailMessage([address], "subject", "body"));
    }

    [Theory]
    [InlineData("https://smtp.example.com")]
    [InlineData("smtp example.com")]
    [InlineData("-smtp.example.com")]
    public void RejectsInvalidSmtpHosts(string host)
    {
        Assert.Throws<ArgumentException>(() => new EmailDeliveryProfile(
            "Relay",
            host,
            587,
            EmailTransportSecurity.StartTlsRequired,
            "thub@example.com",
            ["example.com"],
            "DOMAIN\\admin",
            CreatedAt));
    }

    [Fact]
    public void RendersAllowedVariablesWithArbitraryDelimiterWhitespace()
    {
        var template = new EmailTemplate(
            "{{   workflow.name }} failed",
            "Run {{run.id}} failed");

        var message = template.Render(
            ["ops@example.com"],
            new Dictionary<string, string?>
            {
                ["workflow.name"] = "Import",
                ["run.id"] = "42"
            });

        Assert.Equal("Import failed", message.Subject);
        Assert.Equal("Run 42 failed", message.Body);
    }

    [Fact]
    public void ProfileAndRuleUpdatesRemainBoundedAndRevisioned()
    {
        var profile = new EmailDeliveryProfile(
            "Relay",
            "smtp.example.com",
            587,
            EmailTransportSecurity.StartTlsRequired,
            "thub@example.com",
            ["example.com"],
            "DOMAIN\\admin",
            CreatedAt,
            "smtp/old");
        var rule = new WorkflowAlertRule(
            Guid.NewGuid(),
            profile.Id,
            "Failures",
            WorkflowAlertTriggers.RunFailed,
            ["ops@example.com"],
            new EmailTemplate("Failed", "body"),
            "DOMAIN\\admin",
            CreatedAt);

        profile.Update(
            "New relay",
            "mail.example.com",
            465,
            EmailTransportSecurity.ImplicitTls,
            "alerts@example.com",
            ["example.com"],
            "smtp/new",
            new EmailDeliveryLimits(10, 200, 5_000, 2),
            CreatedAt.AddMinutes(1));
        rule.Update(
            profile.Id,
            "All terminal events",
            WorkflowAlertTriggers.RunFailed | WorkflowAlertTriggers.RunSucceeded,
            ["operations@example.com"],
            new EmailTemplate("{{run.status}}", "{{run.id}}"),
            CreatedAt.AddMinutes(1));

        Assert.Equal("smtp/new", profile.CredentialSecretReference);
        Assert.Equal(EmailTransportSecurity.ImplicitTls, profile.TransportSecurity);
        Assert.Equal(CreatedAt.AddMinutes(1), profile.UpdatedAtUtc);
        Assert.Equal(WorkflowAlertTriggers.RunFailed | WorkflowAlertTriggers.RunSucceeded, rule.Triggers);
        Assert.Equal(["operations@example.com"], rule.Recipients);
        Assert.Equal(CreatedAt.AddMinutes(1), rule.UpdatedAtUtc);
    }
}
