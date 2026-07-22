namespace THub.Application.Publications;

public enum PublicationProblemKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Unauthorized,
    Unavailable,
}

public sealed record PublicationProblem(
    PublicationProblemKind Kind,
    string Code,
    string Message);

public sealed class PublicationResult<T>
{
    private PublicationResult(T? value, PublicationProblem? problem)
    {
        Value = value;
        Problem = problem;
    }

    public bool IsSuccess => Problem is null;

    public T? Value { get; }

    public PublicationProblem? Problem { get; }

    public static PublicationResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new PublicationResult<T>(value, null);
    }

    public static PublicationResult<T> Failure(
        PublicationProblemKind kind,
        string code,
        string message) =>
        new(
            default,
            new PublicationProblem(
                kind,
                RequireText(code, nameof(code)),
                RequireText(message, nameof(message))));

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

public sealed record PublicationCompleted
{
    private PublicationCompleted()
    {
    }

    public static PublicationCompleted Value { get; } = new();
}

internal static class PublicationResultFactory
{
    public static PublicationResult<T> Validation<T>(string code, string message) =>
        PublicationResult<T>.Failure(PublicationProblemKind.Validation, code, message);

    public static PublicationResult<T> NotFound<T>(string code, string message) =>
        PublicationResult<T>.Failure(PublicationProblemKind.NotFound, code, message);

    public static PublicationResult<T> Conflict<T>(string code, string message) =>
        PublicationResult<T>.Failure(PublicationProblemKind.Conflict, code, message);

    public static PublicationResult<T> Forbidden<T>(string code, string message) =>
        PublicationResult<T>.Failure(PublicationProblemKind.Forbidden, code, message);

    public static PublicationResult<T> Unauthorized<T>(string code, string message) =>
        PublicationResult<T>.Failure(PublicationProblemKind.Unauthorized, code, message);

    public static PublicationResult<T> Unavailable<T>(string code, string message) =>
        PublicationResult<T>.Failure(PublicationProblemKind.Unavailable, code, message);

    public static PublicationResult<T> FromDomainException<T>(Exception exception) => exception switch
    {
        ArgumentException => Validation<T>("publication.validation", exception.Message),
        InvalidOperationException => Conflict<T>("publication.state_conflict", exception.Message),
        OverflowException => Conflict<T>("publication.counter_overflow", "The operation exceeded a supported counter limit."),
        _ => throw exception,
    };
}
