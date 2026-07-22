using System.Collections.ObjectModel;

namespace THub.Domain.Alerts;

public sealed class EmailMessage
{
    private readonly ReadOnlyCollection<string> _recipients;

    public EmailMessage(IEnumerable<string> recipients, string subject, string body)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        var normalizedRecipients = recipients
            .Select(recipient => EmailPolicy.Address(recipient, nameof(recipients)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedRecipients.Length == 0)
        {
            throw new ArgumentException("At least one recipient is required.", nameof(recipients));
        }

        if (normalizedRecipients.Length > EmailDeliveryLimits.AbsoluteMaximumRecipients)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recipients),
                $"A message cannot exceed {EmailDeliveryLimits.AbsoluteMaximumRecipients} recipients.");
        }

        Subject = DomainGuard.Require(
            subject,
            nameof(subject),
            EmailDeliveryLimits.AbsoluteMaximumSubjectLength);
        if (Subject.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An Email subject cannot contain control characters.",
                nameof(subject));
        }

        ArgumentNullException.ThrowIfNull(body);
        if (body.Length > EmailDeliveryLimits.AbsoluteMaximumBodyLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(body),
                $"An Email body cannot exceed {EmailDeliveryLimits.AbsoluteMaximumBodyLength} characters.");
        }

        Body = body;
        _recipients = Array.AsReadOnly(normalizedRecipients);
    }

    public IReadOnlyList<string> Recipients => _recipients;

    public string Subject { get; }

    public string Body { get; }
}
