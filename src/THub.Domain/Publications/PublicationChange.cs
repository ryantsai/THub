namespace THub.Domain.Publications;

public sealed class PublicationChange
{
    public const int MaximumKeyJsonLength = 16 * 1_024;
    public const int MaximumRowJsonLength = 256 * 1_024;

    private PublicationChange()
    {
    }

    public PublicationChange(
        Guid id,
        Guid changeSetId,
        PublicationChangeOperation operation,
        string? keyJson,
        string? beforeJson,
        string? afterJson)
    {
        Id = PublicationGuard.RequireId(id, nameof(id));
        ChangeSetId = PublicationGuard.RequireId(changeSetId, nameof(changeSetId));
        Operation = PublicationGuard.RequireDefined(operation, nameof(operation));

        KeyJson = keyJson is null
            ? null
            : PublicationGuard.RequireJsonObject(keyJson, nameof(keyJson), MaximumKeyJsonLength);
        BeforeJson = beforeJson is null
            ? null
            : PublicationGuard.RequireJsonObject(beforeJson, nameof(beforeJson), MaximumRowJsonLength);
        AfterJson = afterJson is null
            ? null
            : PublicationGuard.RequireJsonObject(afterJson, nameof(afterJson), MaximumRowJsonLength);

        switch (Operation)
        {
            case PublicationChangeOperation.Insert when BeforeJson is not null || AfterJson is null:
                throw new ArgumentException("Insert changes require after JSON and cannot contain before JSON.");

            case PublicationChangeOperation.Update when KeyJson is null || BeforeJson is null || AfterJson is null:
                throw new ArgumentException("Update changes require key, before, and after JSON.");

            case PublicationChangeOperation.Delete when KeyJson is null || BeforeJson is null || AfterJson is not null:
                throw new ArgumentException("Delete changes require key and before JSON and cannot contain after JSON.");
        }
    }

    public Guid Id { get; private set; }

    public Guid ChangeSetId { get; private set; }

    public PublicationChangeOperation Operation { get; private set; }

    public string? KeyJson { get; private set; }

    public string? BeforeJson { get; private set; }

    public string? AfterJson { get; private set; }
}
