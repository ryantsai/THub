using Cronos;
using THub.Application.Execution;
using THub.Application.Scheduling;
using THub.Domain.Workflows;

namespace THub.Application.Workflows.Management;

internal sealed class WorkflowInputValidator(
    WorkflowGraphSerializer graphSerializer,
    WorkflowGraphValidator graphValidator,
    ScheduleCalculator scheduleCalculator,
    WorkflowNodeSettingsValidator? nodeSettingsValidator = null)
{
    private readonly WorkflowNodeSettingsValidator _nodeSettingsValidator =
        nodeSettingsValidator ?? new WorkflowNodeSettingsValidator();

    public GraphInputResult ValidateGraph(
        string? graphJson,
        bool requirePublishableGraph = true)
    {
        if (string.IsNullOrWhiteSpace(graphJson))
        {
            return GraphInputResult.Invalid(
                new WorkflowIssue(
                    "graph.required",
                    "A workflow graph is required.",
                    nameof(graphJson)));
        }

        WorkflowGraph graph;
        try
        {
            graph = graphSerializer.Deserialize(graphJson);
        }
        catch (WorkflowGraphSerializationException exception)
        {
            return GraphInputResult.Invalid(
                new WorkflowIssue("graph.invalid-json", exception.Message, nameof(graphJson)));
        }
        catch (ArgumentException exception)
        {
            return GraphInputResult.Invalid(
                new WorkflowIssue("graph.invalid-json", exception.Message, nameof(graphJson)));
        }

        var graphIssues = graphValidator.Validate(graph);
        if (requirePublishableGraph && graphIssues.Count > 0)
        {
            return GraphInputResult.Invalid(
                graphIssues
                    .Select(issue => new WorkflowIssue(
                        issue.Code,
                        issue.Message,
                        nameof(graphJson),
                        issue.NodeId))
                    .ToArray());
        }

        if (requirePublishableGraph)
        {
            var settingsIssues = _nodeSettingsValidator.Validate(graph);
            if (settingsIssues.Count > 0)
            {
                return GraphInputResult.Invalid(
                    settingsIssues
                        .Select(issue => new WorkflowIssue(
                            issue.Code,
                            issue.Message,
                            nameof(graphJson),
                            issue.NodeId))
                        .ToArray());
            }
        }

        return GraphInputResult.Valid(graphSerializer.Serialize(graph), graph);
    }

    public ScheduleInputResult ValidateSchedule(
        string? cronExpression,
        string? timeZoneId,
        DateTimeOffset fromUtc)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return ScheduleInputResult.Invalid(
                new WorkflowIssue(
                    "schedule.time-zone.required",
                    "A schedule time-zone id is required.",
                    nameof(timeZoneId)));
        }

        var normalizedTimeZone = timeZoneId.Trim();
        if (normalizedTimeZone.Length > WorkflowDefinition.MaximumTimeZoneIdLength)
        {
            return ScheduleInputResult.Invalid(
                new WorkflowIssue(
                    "schedule.time-zone.length",
                    $"A time-zone id cannot exceed {WorkflowDefinition.MaximumTimeZoneIdLength} characters.",
                    nameof(timeZoneId)));
        }

        var normalizedCron = string.IsNullOrWhiteSpace(cronExpression)
            ? null
            : cronExpression.Trim();
        if (normalizedCron is not null
            && normalizedCron.Length > WorkflowDefinition.MaximumCronExpressionLength)
        {
            return ScheduleInputResult.Invalid(
                new WorkflowIssue(
                    "schedule.cron.length",
                    $"A cron expression cannot exceed {WorkflowDefinition.MaximumCronExpressionLength} characters.",
                    nameof(cronExpression)));
        }

        try
        {
            if (normalizedCron is null)
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(normalizedTimeZone);
                return ScheduleInputResult.Valid(null, normalizedTimeZone, null);
            }

            var nextOccurrence = scheduleCalculator.GetNextOccurrence(
                normalizedCron,
                normalizedTimeZone,
                fromUtc);
            if (nextOccurrence is null)
            {
                return ScheduleInputResult.Invalid(
                    new WorkflowIssue(
                        "schedule.unreachable",
                        "The cron expression has no reachable future occurrence.",
                        nameof(cronExpression)));
            }

            return ScheduleInputResult.Valid(
                normalizedCron,
                normalizedTimeZone,
                nextOccurrence.Value.ToUniversalTime());
        }
        catch (CronFormatException exception)
        {
            return ScheduleInputResult.Invalid(
                new WorkflowIssue(
                    "schedule.cron.invalid",
                    exception.Message,
                    nameof(cronExpression)));
        }
        catch (TimeZoneNotFoundException)
        {
            return ScheduleInputResult.Invalid(
                new WorkflowIssue(
                    "schedule.time-zone.unknown",
                    $"Time zone '{normalizedTimeZone}' was not found on this host.",
                    nameof(timeZoneId)));
        }
        catch (InvalidTimeZoneException)
        {
            return ScheduleInputResult.Invalid(
                new WorkflowIssue(
                    "schedule.time-zone.invalid",
                    $"Time zone '{normalizedTimeZone}' is invalid on this host.",
                    nameof(timeZoneId)));
        }
    }

    internal sealed record GraphInputResult(
        bool IsValid,
        string? CanonicalJson,
        WorkflowGraph? Graph,
        IReadOnlyList<WorkflowIssue> Issues)
    {
        public static GraphInputResult Valid(string canonicalJson, WorkflowGraph graph) =>
            new(true, canonicalJson, graph, []);

        public static GraphInputResult Invalid(params WorkflowIssue[] issues) =>
            new(false, null, null, Array.AsReadOnly(issues));
    }

    internal sealed record ScheduleInputResult(
        bool IsValid,
        string? CronExpression,
        string? TimeZoneId,
        DateTimeOffset? NextOccurrenceUtc,
        IReadOnlyList<WorkflowIssue> Issues)
    {
        public static ScheduleInputResult Valid(
            string? cronExpression,
            string timeZoneId,
            DateTimeOffset? nextOccurrenceUtc) =>
            new(true, cronExpression, timeZoneId, nextOccurrenceUtc, []);

        public static ScheduleInputResult Invalid(params WorkflowIssue[] issues) =>
            new(false, null, null, null, Array.AsReadOnly(issues));
    }
}
