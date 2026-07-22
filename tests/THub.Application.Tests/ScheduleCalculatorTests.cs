using THub.Application.Scheduling;

namespace THub.Application.Tests;

public sealed class ScheduleCalculatorTests
{
    [Fact]
    public void FindsNextUtcOccurrence()
    {
        var calculator = new ScheduleCalculator();

        var next = calculator.GetNextOccurrence(
            "*/15 * * * *",
            TimeZoneInfo.Utc.Id,
            new DateTimeOffset(2026, 7, 22, 2, 7, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 7, 22, 2, 15, 0, TimeSpan.Zero), next);
    }
}
