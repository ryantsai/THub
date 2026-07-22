using Cronos;

namespace THub.Application.Scheduling;

public sealed class ScheduleCalculator
{
    public DateTimeOffset? GetNextOccurrence(string cronExpression, string timeZoneId, DateTimeOffset fromUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        var expression = CronExpression.Parse(cronExpression, CronFormat.Standard);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return expression.GetNextOccurrence(fromUtc, timeZone);
    }
}

