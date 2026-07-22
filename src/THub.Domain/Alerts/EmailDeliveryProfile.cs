using System.Collections.ObjectModel;

namespace THub.Domain.Alerts;

public enum EmailTransportSecurity
{
    StartTlsRequired,
    ImplicitTls
}

public sealed class EmailDeliveryProfile
{
    private IReadOnlyList<string> _allowedRecipientDomains =
        Array.AsReadOnly(Array.Empty<string>());

    private EmailDeliveryProfile() { }

    public EmailDeliveryProfile(
        string name,
        string smtpHost,
        int smtpPort,
        EmailTransportSecurity transportSecurity,
        string senderAddress,
        IEnumerable<string> allowedRecipientDomains,
        string createdBy,
        DateTimeOffset createdAtUtc,
        string? credentialSecretReference = null,
        EmailDeliveryLimits? limits = null)
    {
        var domains = NormalizeDomains(allowedRecipientDomains);
        ValidateTransport(smtpPort, transportSecurity);

        var timestamp = DomainGuard.Utc(createdAtUtc, nameof(createdAtUtc));
        Id = Guid.NewGuid();
        Name = DomainGuard.Require(name, nameof(name), 200);
        SmtpHost = EmailPolicy.SmtpHost(smtpHost, nameof(smtpHost));
        SmtpPort = smtpPort;
        TransportSecurity = transportSecurity;
        SenderAddress = EmailPolicy.Address(senderAddress, nameof(senderAddress));
        CredentialSecretReference = string.IsNullOrWhiteSpace(credentialSecretReference)
            ? null
            : DomainGuard.Require(
                credentialSecretReference,
                nameof(credentialSecretReference),
                500);
        Limits = limits ?? EmailDeliveryLimits.Default;
        CreatedBy = DomainGuard.Require(createdBy, nameof(createdBy), 256);
        CreatedAtUtc = timestamp;
        UpdatedAtUtc = timestamp;
        _allowedRecipientDomains = Array.AsReadOnly(domains);
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string SmtpHost { get; private set; } = string.Empty;

    public int SmtpPort { get; private set; }

    public EmailTransportSecurity TransportSecurity { get; private set; }

    public string SenderAddress { get; private set; } = string.Empty;

    /// <summary>
    /// A lookup key for an external secret provider. This is never the credential value.
    /// </summary>
    public string? CredentialSecretReference { get; private set; }

    public IReadOnlyList<string> AllowedRecipientDomains => _allowedRecipientDomains;

    public EmailDeliveryLimits Limits { get; private set; } = EmailDeliveryLimits.Default;

    public bool IsEnabled { get; private set; } = true;

    public string CreatedBy { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void ValidateMessage(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsEnabled)
        {
            throw new InvalidOperationException("The Email delivery profile is disabled.");
        }

        ValidateMessagePolicy(message);
    }

    public void ValidateMessagePolicy(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Recipients.Count > Limits.MaximumRecipients)
        {
            throw new InvalidOperationException(
                $"The profile permits at most {Limits.MaximumRecipients} recipients.");
        }

        if (message.Subject.Length > Limits.MaximumSubjectLength)
        {
            throw new InvalidOperationException(
                $"The profile permits subjects of at most {Limits.MaximumSubjectLength} characters.");
        }

        if (message.Body.Length > Limits.MaximumBodyLength)
        {
            throw new InvalidOperationException(
                $"The profile permits bodies of at most {Limits.MaximumBodyLength} characters.");
        }

        foreach (var recipient in message.Recipients)
        {
            var domain = EmailPolicy.GetDomain(recipient);
            if (!_allowedRecipientDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Recipient domain '{domain}' is not allowed by this profile.");
            }
        }
    }

    public void Disable(DateTimeOffset changedAtUtc) => SetEnabled(false, changedAtUtc);

    public void Enable(DateTimeOffset changedAtUtc) => SetEnabled(true, changedAtUtc);

    public void Update(
        string name,
        string smtpHost,
        int smtpPort,
        EmailTransportSecurity transportSecurity,
        string senderAddress,
        IEnumerable<string> allowedRecipientDomains,
        string? credentialSecretReference,
        EmailDeliveryLimits limits,
        DateTimeOffset changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var domains = NormalizeDomains(allowedRecipientDomains);
        ValidateTransport(smtpPort, transportSecurity);
        var timestamp = DomainGuard.OnOrAfter(
            changedAtUtc,
            UpdatedAtUtc,
            nameof(changedAtUtc));

        Name = DomainGuard.Require(name, nameof(name), 200);
        SmtpHost = EmailPolicy.SmtpHost(smtpHost, nameof(smtpHost));
        SmtpPort = smtpPort;
        TransportSecurity = transportSecurity;
        SenderAddress = EmailPolicy.Address(senderAddress, nameof(senderAddress));
        CredentialSecretReference = string.IsNullOrWhiteSpace(credentialSecretReference)
            ? null
            : DomainGuard.Require(
                credentialSecretReference,
                nameof(credentialSecretReference),
                500);
        Limits = limits;
        _allowedRecipientDomains = Array.AsReadOnly(domains);
        UpdatedAtUtc = timestamp;
    }

    private void SetEnabled(bool enabled, DateTimeOffset changedAtUtc)
    {
        var timestamp = DomainGuard.OnOrAfter(
            changedAtUtc,
            UpdatedAtUtc,
            nameof(changedAtUtc));
        IsEnabled = enabled;
        UpdatedAtUtc = timestamp;
    }

    private static string[] NormalizeDomains(IEnumerable<string> allowedRecipientDomains)
    {
        ArgumentNullException.ThrowIfNull(allowedRecipientDomains);
        var domains = allowedRecipientDomains
            .Select(domain => EmailPolicy.Domain(domain, nameof(allowedRecipientDomains)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (domains.Length == 0)
        {
            throw new ArgumentException(
                "At least one allowed recipient domain is required.",
                nameof(allowedRecipientDomains));
        }

        return domains;
    }

    private static void ValidateTransport(
        int smtpPort,
        EmailTransportSecurity transportSecurity)
    {
        if (smtpPort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(smtpPort));
        }

        if (!Enum.IsDefined(transportSecurity))
        {
            throw new ArgumentOutOfRangeException(nameof(transportSecurity));
        }
    }
}
