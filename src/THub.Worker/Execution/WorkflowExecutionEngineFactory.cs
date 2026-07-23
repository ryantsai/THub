using THub.Application.Execution;
using THub.Application.Workflows;

namespace THub.Worker.Execution;

internal sealed class WorkflowExecutionEngineFactory(
    WorkflowExecutionPlanner planner,
    IEnumerable<IWorkflowNodeExecutor> executors,
    IWorkflowExecutionPreflightValidator preflightValidator,
    ITabularDataSetStore dataSetStore,
    INodeRetryPolicyProvider retryPolicies,
    IWorkflowRetryScheduler retryScheduler,
    IExecutionErrorClassifier errorClassifier,
    IWorkflowExecutionEventSinkFactory eventSinkFactory,
    WorkflowGraphSerializer graphSerializer,
    TimeProvider timeProvider,
    ILogger<WorkflowOperationTraceSink> traceLogger)
{
    public BoundedWorkflowExecutionEngine Create(
        WorkflowRunExecutionClaim claim,
        string leaseOwner,
        WorkflowExecutionWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var timeouts = options.CreateTimeouts();
        var eventSink = new WorkflowOperationTraceSink(
            eventSinkFactory.Create(claim.WorkflowRunId, leaseOwner),
            traceLogger,
            claim.WorkflowId,
            claim.WorkflowVersionId,
            claim.WorkflowVersion);
        return new BoundedWorkflowExecutionEngine(
            planner,
            new WorkflowNodeExecutorRegistry(executors),
            preflightValidator,
            dataSetStore,
            retryPolicies,
            retryScheduler,
            errorClassifier,
            eventSink,
            options.CreateLimits(),
            timeProvider,
            graphSerializer,
            new DefaultNodeExecutionTimeoutProvider(timeouts),
            timeouts);
    }
}
