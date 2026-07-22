using THub.Application.Alerts;
using THub.Domain.Alerts;
using THub.Infrastructure.Alerts;

namespace THub.Infrastructure.Tests;

public sealed class MailKitAlertSenderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FailsSafelyWhenCredentialReferenceCannotBeResolved()
    {
        var profile = CreateProfile();
        var delivery = CreateDelivery(profile, "ops@example.com");
        var sender = new MailKitAlertSender(
            new NullSecretResolver(),
            new SmtpAlertSenderOptions());

        var result = await sender.SendAsync(profile, delivery, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("email.secret_unavailable", result.Error!.Code);
        Assert.False(result.Error.IsRetryable);
    }

    [Fact]
    public async Task RejectsMessageOutsideRecipientPolicyBeforeResolvingSecret()
    {
        var profile = CreateProfile();
        var delivery = CreateDelivery(profile, "ops@outside.test");
        var resolver = new CountingSecretResolver();
        var sender = new MailKitAlertSender(resolver, new SmtpAlertSenderOptions());

        var result = await sender.SendAsync(profile, delivery, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("email.message_policy", result.Error!.Code);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public void RejectsUnsafeOperationTimeoutConfiguration()
    {
        var options = new SmtpAlertSenderOptions { OperationTimeoutSeconds = 1 };

        Assert.Throws<InvalidOperationException>(() =>
            new MailKitAlertSender(new NullSecretResolver(), options));
    }

    private static EmailDeliveryProfile CreateProfile() => new(
        "Relay",
        "smtp.example.com",
        587,
        EmailTransportSecurity.StartTlsRequired,
        "thub@example.com",
        ["example.com"],
        "DOMAIN\\admin",
        Now,
        "smtp/production");

    private static AlertDelivery CreateDelivery(
        EmailDeliveryProfile profile,
        string recipient) => AlertDelivery.ForWorkflowRule(
        Guid.NewGuid(),
        Guid.NewGuid(),
        profile.Id,
        AlertDeliveryEvent.RunFailed,
        new EmailMessage([recipient], "Failure", "body"),
        Now);

    private sealed class NullSecretResolver : ISecretResolver
    {
        public ValueTask<SmtpCredential?> ResolveSmtpCredentialAsync(
            string secretReference,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<SmtpCredential?>(null);
    }

    private sealed class CountingSecretResolver : ISecretResolver
    {
        public int CallCount { get; private set; }

        public ValueTask<SmtpCredential?> ResolveSmtpCredentialAsync(
            string secretReference,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<SmtpCredential?>(null);
        }
    }
}
