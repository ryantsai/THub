using System.ComponentModel.DataAnnotations;
using THub.Worker.Alerts;
using THub.Worker.Publications;

namespace THub.Worker.Tests;

public sealed class BackgroundWorkerOptionsTests
{
    [Fact]
    public void EmailDefaults_CreateBoundedDispatchOptions()
    {
        var configured = new EmailAlertDispatchWorkerOptions();

        var options = configured.CreateDispatchOptions();

        Assert.Equal(25, options.MaximumDeliveriesPerBatch);
        Assert.Equal(TimeSpan.FromMinutes(2), options.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(15), options.TransitionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RetryPolicy.InitialDelay);
        Assert.Equal(TimeSpan.FromMinutes(30), options.RetryPolicy.MaximumDelay);
    }

    [Fact]
    public void EmailOptions_RejectOutOfRangeBatch()
    {
        var configured = new EmailAlertDispatchWorkerOptions
        {
            MaximumDeliveriesPerBatch = 101,
        };

        Assert.False(IsDataAnnotationValid(configured));
    }

    [Fact]
    public void EmailOptions_RequireLeaseLongerThanSendAndTransitionBounds()
    {
        var configured = new EmailAlertDispatchWorkerOptions
        {
            LeaseDurationSeconds = 46,
            TransitionTimeoutSeconds = 15,
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            configured.ValidateCrossFieldBounds(smtpOperationTimeoutSeconds: 30));

        Assert.Contains("lease duration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicationOptions_RejectBusyLoopPolling()
    {
        var configured = new PublicationChangeSetWorkerOptions
        {
            PollIntervalMilliseconds = 0,
        };

        Assert.False(IsDataAnnotationValid(configured));
    }

    private static bool IsDataAnnotationValid(object options)
    {
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(options, new ValidationContext(options), results, true);
    }
}
