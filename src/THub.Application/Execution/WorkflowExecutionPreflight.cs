using THub.Application.Workflows;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Application.Execution;

/// <summary>
/// Performs read-only validation of a node's referenced runtime resources. Implementations must
/// not create external effects; executors remain responsible for rechecking mutable resources.
/// </summary>
public interface IWorkflowNodeResourceValidator
{
    ValueTask ValidateAsync(
        WorkflowNode node,
        WorkflowNodeSettings settings,
        TabularExecutionLimits limits,
        CancellationToken cancellationToken);
}

public interface IWorkflowExecutionPreflightValidator
{
    ValueTask<ExecutionError?> ValidateAsync(
        WorkflowGraph graph,
        TabularExecutionLimits limits,
        CancellationToken cancellationToken);
}

/// <summary>
/// Parses every node before performing any resource I/O, then validates every referenced
/// resource before the execution engine is allowed to invoke an executor.
/// </summary>
public sealed class WorkflowExecutionPreflightValidator(
    WorkflowNodeSettingsValidator settingsValidator,
    IEnumerable<IWorkflowNodeResourceValidator> resourceValidators,
    IExecutionErrorClassifier errorClassifier) : IWorkflowExecutionPreflightValidator
{
    private readonly WorkflowNodeSettingsValidator _settingsValidator =
        settingsValidator ?? throw new ArgumentNullException(nameof(settingsValidator));
    private readonly IReadOnlyList<IWorkflowNodeResourceValidator> _resourceValidators =
        (resourceValidators ?? throw new ArgumentNullException(nameof(resourceValidators))).ToArray();
    private readonly IExecutionErrorClassifier _errorClassifier =
        errorClassifier ?? throw new ArgumentNullException(nameof(errorClassifier));

    public async ValueTask<ExecutionError?> ValidateAsync(
        WorkflowGraph graph,
        TabularExecutionLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(limits);

        var parsedNodes = new List<(WorkflowNode Node, WorkflowNodeSettings Settings)>(graph.Nodes.Count);
        foreach (var node in graph.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                parsedNodes.Add((node, _settingsValidator.Parse(node)));
            }
            catch (WorkflowNodeSettingsException exception)
            {
                return new ExecutionError(
                    exception.Code,
                    ExecutionErrorCategory.Validation,
                    exception.Message,
                    isRetryable: false);
            }
        }

        foreach (var parsed in parsedNodes)
        {
            foreach (var validator in _resourceValidators)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await validator.ValidateAsync(
                        parsed.Node,
                        parsed.Settings,
                        limits,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    return _errorClassifier.Classify(exception, cancellationRequested: false);
                }
            }
        }

        return null;
    }
}
