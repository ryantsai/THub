namespace THub.Application.Publications;

public sealed record PublicationConnectionPolicyResult(
    bool IsValid,
    string? ErrorCode = null,
    string? Message = null)
{
    public static PublicationConnectionPolicyResult Success { get; } = new(true);

    public static PublicationConnectionPolicyResult Failure(
        string errorCode,
        string message) =>
        new(false, errorCode, message);
}

public interface IPublicationConnectionPolicy
{
    Task<PublicationConnectionPolicyResult> ValidateAsync(
        Guid readConnectionId,
        Guid? applyConnectionId,
        bool requiresApplyConnection,
        CancellationToken cancellationToken);
}
