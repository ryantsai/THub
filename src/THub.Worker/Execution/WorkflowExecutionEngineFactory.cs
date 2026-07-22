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
    TimeProvider timeProvider)
{
    public BoundedWorkflowExecutionEngine Create(
        Guid workflowRunId,
        string leaseOwner,
        WorkflowExecutionWorkerOptions options)
    {
        var timeouts = options.CreateTimeouts();
        return new BoundedWorkflowExecutionEngine(
            planner,
            new WorkflowNodeExecutorRegistry(executors),
            preflightValidator,
            dataSetStore,
            retryPolicies,
            retryScheduler,
            errorClassifier,
            eventSinkFactory.Create(workflowRunId, leaseOwner),
            options.CreateLimits(),
            timeProvider,
            graphSerializer,
            new DefaultNodeExecutionTimeoutProvider(timeouts),
            timeouts);
    }
}
