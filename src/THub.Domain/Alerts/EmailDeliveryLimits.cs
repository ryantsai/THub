namespace THub.Domain.Alerts;

public sealed record EmailDeliveryLimits
{
    public const int AbsoluteMaximumRecipients = 100;
    public const int AbsoluteMaximumSubjectLength = 998;
    public const int AbsoluteMaximumBodyLength = 1_000_000;
    public const int AbsoluteMaximumConcurrency = 100;

    public static EmailDeliveryLimits Default { get; } = new(
        maximumRecipients: 20,
        maximumSubjectLength: 200,
        maximumBodyLength: 20_000,
        maximumConcurrentSends: 5);

    public EmailDeliveryLimits(
        int maximumRecipients,
        int maximumSubjectLength,
        int maximumBodyLength,
        int maximumConcurrentSends)
    {
        MaximumRecipients = InRange(
            maximumRecipients,
            1,
            AbsoluteMaximumRecipients,
            nameof(maximumRecipients));
        MaximumSubjectLength = InRange(
            maximumSubjectLength,
            1,
            AbsoluteMaximumSubjectLength,
            nameof(maximumSubjectLength));
        MaximumBodyLength = InRange(
            maximumBodyLength,
            1,
            AbsoluteMaximumBodyLength,
            nameof(maximumBodyLength));
        MaximumConcurrentSends = InRange(
            maximumConcurrentSends,
            1,
            AbsoluteMaximumConcurrency,
            nameof(maximumConcurrentSends));
    }

    public int MaximumRecipients { get; }

    public int MaximumSubjectLength { get; }

    public int MaximumBodyLength { get; }

    public int MaximumConcurrentSends { get; }

    private static int InRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value must be between {minimum} and {maximum}.");
        }

        return value;
    }
}
