namespace THub.Application.Workflows.Management;

public enum WorkflowOperationStatus
{
    Succeeded,
    ValidationFailed,
    NotFound,
    ConcurrencyConflict,
    Conflict,
    InvalidState
}

public sealed record WorkflowIssue(
    string Code,
    string Message,
    string? Field = null,
    string? NodeId = null);

public sealed class WorkflowOperationResult<T>
    where T : class
{
    private WorkflowOperationResult(
        WorkflowOperationStatus status,
        T? value,
        IReadOnlyList<WorkflowIssue> issues)
    {
        Status = status;
        Value = value;
        Issues = issues;
    }

    public WorkflowOperationStatus Status { get; }

    public T? Value { get; }

    public IReadOnlyList<WorkflowIssue> Issues { get; }

    public bool IsSuccess => Status == WorkflowOperationStatus.Succeeded;

    public static WorkflowOperationResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(WorkflowOperationStatus.Succeeded, value, []);
    }

    public static WorkflowOperationResult<T> Failure(
        WorkflowOperationStatus status,
        params WorkflowIssue[] issues)
    {
        if (status == WorkflowOperationStatus.Succeeded)
        {
            throw new ArgumentException(
                "A failed result cannot use the succeeded status.",
                nameof(status));
        }

        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Length == 0)
        {
            throw new ArgumentException(
                "At least one issue is required for a failed result.",
                nameof(issues));
        }

        return new(status, null, Array.AsReadOnly(issues));
    }

    public static WorkflowOperationResult<T> Failure(
        WorkflowOperationStatus status,
        IReadOnlyList<WorkflowIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return Failure(status, [.. issues]);
    }
}
