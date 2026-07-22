namespace THub.Application.Alerts;

public enum AlertResultStatus
{
    Succeeded,
    ValidationFailed,
    NotFound,
    Conflict,
    Unavailable
}

public sealed record AlertProblem(string Code, string Message);

public sealed class AlertResult<T>
{
    private AlertResult(AlertResultStatus status, T? value, AlertProblem? problem)
    {
        Status = status;
        Value = value;
        Problem = problem;
    }

    public AlertResultStatus Status { get; }

    public T? Value { get; }

    public AlertProblem? Problem { get; }

    public bool IsSuccess => Status == AlertResultStatus.Succeeded;

    public static AlertResult<T> Success(T value) =>
        new(AlertResultStatus.Succeeded, value, problem: null);

    public static AlertResult<T> Failure(
        AlertResultStatus status,
        string code,
        string message)
    {
        if (status == AlertResultStatus.Succeeded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                "A failed result cannot use the succeeded status.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new AlertResult<T>(status, value: default, new AlertProblem(code, message));
    }
}

internal static class AlertResults
{
    public static AlertResult<T> Validation<T>(string code, string message) =>
        AlertResult<T>.Failure(AlertResultStatus.ValidationFailed, code, message);

    public static AlertResult<T> NotFound<T>(string code, string message) =>
        AlertResult<T>.Failure(AlertResultStatus.NotFound, code, message);

    public static AlertResult<T> Conflict<T>(string code, string message) =>
        AlertResult<T>.Failure(AlertResultStatus.Conflict, code, message);

    public static AlertResult<T> Unavailable<T>(string code, string message) =>
        AlertResult<T>.Failure(AlertResultStatus.Unavailable, code, message);

    public static AlertResult<T> DomainFailure<T>(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Validation<T>(
            "email.definition_invalid",
            exception is ArgumentOutOfRangeException
                ? "One or more Email settings exceed an allowed bound."
                : exception.Message);
    }

    public static bool IsDomainException(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or OverflowException;
}
