namespace THub.Domain.Publications;

public sealed class PublicationGrant
{
    private PublicationGrant()
    {
    }

    public PublicationGrant(
        Guid id,
        Guid publicationId,
        Guid roleId,
        bool canView,
        bool canInsert,
        bool canUpdate,
        bool canDelete,
        bool canApprove)
    {
        Id = PublicationGuard.RequireId(id, nameof(id));
        PublicationId = PublicationGuard.RequireId(publicationId, nameof(publicationId));
        RoleId = PublicationGuard.RequireId(roleId, nameof(roleId));
        CanInsert = canInsert;
        CanUpdate = canUpdate;
        CanDelete = canDelete;
        CanApprove = canApprove;

        // Loading, mutating, deleting, and approving data all require the resource to be visible.
        CanView = canView || canInsert || canUpdate || canDelete || canApprove;
    }

    public Guid Id { get; private set; }

    public Guid PublicationId { get; private set; }

    public Guid RoleId { get; private set; }

    public bool CanView { get; private set; }

    public bool CanInsert { get; private set; }

    public bool CanUpdate { get; private set; }

    public bool CanDelete { get; private set; }

    public bool CanApprove { get; private set; }

    public bool Allows(PublicationOperation operation) =>
        PublicationGuard.RequireDefined(operation, nameof(operation)) switch
        {
            PublicationOperation.View => CanView,
            PublicationOperation.Insert => CanInsert,
            PublicationOperation.Update => CanUpdate,
            PublicationOperation.Delete => CanDelete,
            PublicationOperation.Approve => CanApprove,
            _ => false,
        };
}
