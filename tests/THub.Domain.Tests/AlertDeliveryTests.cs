using THub.Domain.Alerts;
using THub.Domain.Runs;

namespace THub.Domain.Tests;

public sealed class AlertDeliveryTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 23, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProfileContainsOnlySecretReferenceAndEnforcesRecipientPolicy()
    {
        var profile = CreateProfile();
        var allowed = new EmailMessage(["ops@example.com"], "Failure", "Safe summary");
        var denied = new EmailMessage(["ops@outside.test"], "Failure", "Safe summary");

        profile.ValidateMessage(allowed);
        Assert.Equal("smtp/production", profile.CredentialSecretReference);
        Assert.Throws<InvalidOperationException>(() => profile.ValidateMessage(denied));

        profile.Disable(CreatedAt.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => profile.ValidateMessage(allowed));
    }

    [Fact]
    public void TemplatesAllowOnlyBoundedRunVariables()
    {
        var template = new EmailTemplate(
            "{{workflow.name}} failed",
            "Run {{run.id}}: {{error.summary}}");
        var message = template.Render(
            ["ops@example.com"],
            new Dictionary<string, string?>
            {
                ["workflow.name"] = "Import",
                ["run.id"] = "42",
                ["error.summary"] = "Timed out"
            });

        Assert.Equal("Import failed", message.Subject);
        Assert.Contains("Timed out", message.Body, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            new EmailTemplate("{{row.password}}", "body"));
        Assert.Throws<ArgumentException>(() =>
            new EmailMessage(["ops@example.com"], "Injected\r\nBcc: x@y.test", "body"));
    }

    [Fact]
    public void WorkflowRuleMatchesOnlyConfiguredTerminalEvents()
    {
        var rule = new WorkflowAlertRule(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Failures and cancellations",
            WorkflowAlertTriggers.RunFailed | WorkflowAlertTriggers.RunCancelled,
            ["ops@example.com"],
            new EmailTemplate("Run failed", "{{error.summary}}"),
            "DOMAIN\\admin",
            CreatedAt);

        Assert.True(rule.Matches(WorkflowRunStatus.Failed));
        Assert.True(rule.Matches(WorkflowRunStatus.Cancelled));
        Assert.False(rule.Matches(WorkflowRunStatus.Succeeded));
        Assert.False(rule.Matches(WorkflowRunStatus.Running));
        rule.Disable(CreatedAt.AddMinutes(1));
        Assert.False(rule.Matches(WorkflowRunStatus.Failed));
    }

    [Fact]
    public void TransientDeliveryFailureSchedulesRetryThenSucceeds()
    {
        var delivery = CreateDelivery(maximumAttempts: 3);
        var transient = new ExecutionError(
            "smtp.unavailable",
            ExecutionErrorCategory.Connectivity,
            "The relay is unavailable.",
            isRetryable: true);

        Assert.True(delivery.TryClaim("worker-a", CreatedAt, TimeSpan.FromMinutes(1)));
        delivery.RecordFailure(
            "worker-a",
            transient,
            CreatedAt.AddSeconds(10),
            CreatedAt.AddMinutes(1));

        Assert.Equal(AlertDeliveryStatus.RetryScheduled, delivery.Status);
        Assert.False(delivery.TryClaim(
            "worker-b",
            CreatedAt.AddSeconds(30),
            TimeSpan.FromMinutes(1)));
        Assert.True(delivery.TryClaim(
            "worker-b",
            CreatedAt.AddMinutes(1),
            TimeSpan.FromMinutes(1)));
        delivery.RecordDelivered(
            "worker-b",
            CreatedAt.AddMinutes(1).AddSeconds(10),
            "relay-42");

        Assert.Equal(AlertDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.Equal("relay-42", delivery.ProviderMessageId);
        Assert.Null(delivery.LeaseOwner);
    }

    [Fact]
    public void PermanentOrExhaustedFailuresAreDeadLettered()
    {
        var permanent = CreateDelivery(maximumAttempts: 3);
        permanent.TryClaim("worker", CreatedAt, TimeSpan.FromMinutes(1));
        permanent.RecordFailure(
            "worker",
            new ExecutionError(
                "smtp.rejected",
                ExecutionErrorCategory.Authorization,
                "The relay rejected the message.",
                false),
            CreatedAt.AddSeconds(1));
        Assert.Equal(AlertDeliveryStatus.DeadLettered, permanent.Status);

        var exhausted = CreateDelivery(maximumAttempts: 1);
        exhausted.TryClaim("worker", CreatedAt, TimeSpan.FromSeconds(10));
        Assert.False(exhausted.TryClaim(
            "recovery-worker",
            CreatedAt.AddSeconds(10),
            TimeSpan.FromSeconds(10)));
        Assert.Equal(AlertDeliveryStatus.DeadLettered, exhausted.Status);
        Assert.Equal("email.attempt_limit", exhausted.LastError!.Code);
    }

    [Fact]
    public void DeduplicationKeysAreStableForSameRuleRunAndEvent()
    {
        var ruleId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var message = new EmailMessage(["ops@example.com"], "Failure", "body");

        var first = AlertDelivery.ForWorkflowRule(
            ruleId,
            runId,
            profileId,
            AlertDeliveryEvent.RunFailed,
            message,
            CreatedAt);
        var second = AlertDelivery.ForWorkflowRule(
            ruleId,
            runId,
            profileId,
            AlertDeliveryEvent.RunFailed,
            message,
            CreatedAt.AddSeconds(1));

        Assert.Equal(first.DeduplicationKey, second.DeduplicationKey);
        Assert.NotEqual(first.StableMessageId, second.StableMessageId);
    }

    [Fact]
    public void EmailActionDeduplicatesAcrossRecoveredStepAttempts()
    {
        var runId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var message = new EmailMessage(["ops@example.com"], "Action", "body");

        var first = AlertDelivery.ForEmailAction(
            runId,
            Guid.NewGuid(),
            "notify-operations",
            profileId,
            message,
            CreatedAt);
        var recovered = AlertDelivery.ForEmailAction(
            runId,
            Guid.NewGuid(),
            "notify-operations",
            profileId,
            message,
            CreatedAt.AddSeconds(1));

        Assert.Equal(first.DeduplicationKey, recovered.DeduplicationKey);
        Assert.NotEqual(first.WorkflowStepRunId, recovered.WorkflowStepRunId);
        Assert.Equal("notify-operations", recovered.WorkflowNodeId);
    }

    private static EmailDeliveryProfile CreateProfile() => new(
        "Production relay",
        "smtp.example.com",
        587,
        EmailTransportSecurity.StartTlsRequired,
        "thub@example.com",
        ["example.com"],
        "DOMAIN\\admin",
        CreatedAt,
        "smtp/production",
        new EmailDeliveryLimits(5, 100, 1_000, 2));

    private static AlertDelivery CreateDelivery(int maximumAttempts) =>
        AlertDelivery.ForWorkflowRule(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AlertDeliveryEvent.RunFailed,
            new EmailMessage(["ops@example.com"], "Failure", "body"),
            CreatedAt,
            maximumAttempts);
}
