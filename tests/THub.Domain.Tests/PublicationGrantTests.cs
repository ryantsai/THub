using THub.Domain.Publications;

namespace THub.Domain.Tests;

public sealed class PublicationGrantTests
{
    [Fact]
    public void AnyPrivilegedOperationImpliesView()
    {
        var grant = CreateGrant(canView: false, canInsert: true, canApprove: true);

        Assert.True(grant.CanView);
        Assert.True(grant.Allows(PublicationOperation.View));
        Assert.True(grant.Allows(PublicationOperation.Insert));
        Assert.True(grant.Allows(PublicationOperation.Approve));
    }

    [Fact]
    public void InsertUpdateDeleteAndApproveRemainSeparateCapabilities()
    {
        var grant = CreateGrant(canView: true, canUpdate: true);

        Assert.True(grant.CanView);
        Assert.False(grant.CanInsert);
        Assert.True(grant.CanUpdate);
        Assert.False(grant.CanDelete);
        Assert.False(grant.CanApprove);
    }

    private static PublicationGrant CreateGrant(
        bool canView,
        bool canInsert = false,
        bool canUpdate = false,
        bool canDelete = false,
        bool canApprove = false) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PublicationRole.Operator,
            canView,
            canInsert,
            canUpdate,
            canDelete,
            canApprove);
}
