using THub.Application.Alerts;
using THub.Application.Execution;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Infrastructure.Execution;

/// <summary>
/// Persists Email delivery intent. SMTP delivery remains an independent durable outbox process.
/// The run plus stable graph-node identity deduplicates recovered or retried step attempts.
/// </summary>
public sealed class EmailAlertNodeExecutor(
    WorkflowNodeSettingsValidator settingsValidator,
    IWorkflowStepRunLocator stepRunLocator,
    EmailActionOutboxService outboxService) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Action(
            WorkflowNodeKind.EmailAlert,
            consumesInput: true,
            explicitlyIdempotent: true);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (EmailAlertNodeSettings)settingsValidator.Parse(context.Node);
        var stepRunId = await stepRunLocator.FindRunningStepIdAsync(
            context.WorkflowRunId,
            context.Node.Id,
            cancellationToken);
        if (stepRunId is null)
        {
            throw ExecutionFailure.ExternalSideEffect(
                "execution.email.step_missing",
                "The durable Email action step attempt was not found.");
        }

        var inputRows = context.Inputs.Sum(input => input.DataSet.RowCount);
        var result = await outboxService.QueueAsync(
            new QueueEmailActionCommand(
                context.WorkflowRunId,
                stepRunId.Value,
                context.Node.Id,
                settings.ProfileId,
                settings.Recipients,
                settings.Subject,
                settings.Body,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["run.id"] = context.WorkflowRunId.ToString("D")
                },
                settings.MaximumAttempts),
            cancellationToken);
        if (!result.IsSuccess)
        {
            var problem = result.Problem;
            var category = result.Status switch
            {
                AlertResultStatus.ValidationFailed or AlertResultStatus.NotFound =>
                    ExecutionErrorCategory.Configuration,
                AlertResultStatus.Unavailable => ExecutionErrorCategory.Connectivity,
                _ => ExecutionErrorCategory.ExternalSideEffect
            };
            throw new WorkflowNodeExecutionException(new ExecutionError(
                problem?.Code ?? "execution.email.enqueue",
                category,
                problem?.Message ?? "The Email delivery intent could not be persisted.",
                isRetryable: false));
        }

        await context.Progress.ReportAsync(
            new WorkflowNodeProgress(RowsRead: inputRows, BatchesProcessed: 1),
            cancellationToken);
        return WorkflowNodeExecutionResult.WithoutOutput;
    }
}
