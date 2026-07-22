namespace THub.Infrastructure.Alerts;

public sealed class SmtpAlertSenderOptions
{
    public const int MinimumOperationTimeoutSeconds = 5;
    public const int MaximumOperationTimeoutSeconds = 300;

    public int OperationTimeoutSeconds { get; set; } = 30;

    public TimeSpan GetValidatedOperationTimeout()
    {
        if (OperationTimeoutSeconds is < MinimumOperationTimeoutSeconds
            or > MaximumOperationTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"SMTP operation timeout must be between {MinimumOperationTimeoutSeconds} "
                + $"and {MaximumOperationTimeoutSeconds} seconds.");
        }

        return TimeSpan.FromSeconds(OperationTimeoutSeconds);
    }
}
