using System.ComponentModel.DataAnnotations;
using THub.Application.Alerts;

namespace THub.Worker.Alerts;

public sealed class EmailAlertDispatchWorkerOptions
{
    public const string SectionName = "EmailDelivery:Dispatcher";
    public const int MinimumLeaseSafetyMarginSeconds = 2;

    [Range(1, 100)]
    public int MaximumDeliveriesPerBatch { get; set; } = 25;

    [Range(100, 60_000)]
    public int PollIntervalMilliseconds { get; set; } = 2_000;

    [Range(30, 3_600)]
    public int LeaseDurationSeconds { get; set; } = 120;

    [Range(5, 60)]
    public int TransitionTimeoutSeconds { get; set; } = 15;

    [Range(1, 86_400)]
    public int InitialRetryDelaySeconds { get; set; } = 30;

    [Range(1, 86_400)]
    public int MaximumRetryDelaySeconds { get; set; } = 1_800;

    [Range(0, 0.5)]
    public double RetryJitterRatio { get; set; } = 0.2;

    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMilliseconds);

    public AlertDispatchOptions CreateDispatchOptions()
    {
        var initial = TimeSpan.FromSeconds(InitialRetryDelaySeconds);
        var maximum = TimeSpan.FromSeconds(MaximumRetryDelaySeconds);
        return new AlertDispatchOptions(
            MaximumDeliveriesPerBatch,
            TimeSpan.FromSeconds(LeaseDurationSeconds),
            new AlertRetryPolicy(initial, maximum, RetryJitterRatio),
            TimeSpan.FromSeconds(TransitionTimeoutSeconds));
    }

    public void ValidateCrossFieldBounds(int smtpOperationTimeoutSeconds)
    {
        if (MaximumRetryDelaySeconds < InitialRetryDelaySeconds)
        {
            throw new InvalidOperationException(
                "Maximum Email retry delay must be greater than or equal to the initial delay.");
        }

        var minimumLease = checked(
            smtpOperationTimeoutSeconds
            + TransitionTimeoutSeconds
            + MinimumLeaseSafetyMarginSeconds);
        if (LeaseDurationSeconds <= minimumLease)
        {
            throw new InvalidOperationException(
                $"Email delivery lease duration must exceed the SMTP operation timeout, "
                + $"transition timeout, and {MinimumLeaseSafetyMarginSeconds}-second cleanup margin "
                + $"({minimumLease} seconds total)." );
        }
    }
}
