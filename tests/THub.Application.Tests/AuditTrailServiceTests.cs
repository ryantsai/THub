using THub.Application.Auditing;
using THub.Domain.Auditing;

namespace THub.Application.Tests;

public sealed class AuditTrailServiceTests
{
    [Fact]
    public async Task QueryFailsClosedWhenViewerIsNotAuthorized()
    {
        var store = new RecordingAuditRecordStore();
        var service = new AuditTrailService(store, new AuditViewerAuthorization(false));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ListAsync(new AuditRecordListRequest(), CancellationToken.None));

        Assert.False(store.WasCalled);
    }

    [Fact]
    public async Task AuthorizedQueryPassesValidatedFilterToStore()
    {
        var store = new RecordingAuditRecordStore();
        var service = new AuditTrailService(store, new AuditViewerAuthorization(true));

        var page = await service.ListAsync(
            new AuditRecordListRequest(
                Offset: 10,
                Limit: 25,
                Search: "  workflow  ",
                Outcome: AuditOutcome.Succeeded),
            CancellationToken.None);

        Assert.True(store.WasCalled);
        Assert.Equal("workflow", store.Filter?.Search);
        Assert.Equal(10, page.Offset);
        Assert.Equal(25, page.Limit);
    }

    private sealed class AuditViewerAuthorization(bool canView) : IAuditViewerAuthorization
    {
        public Task<bool> CanViewAsync(CancellationToken cancellationToken) =>
            Task.FromResult(canView);
    }

    private sealed class RecordingAuditRecordStore : IAuditRecordStore
    {
        public bool WasCalled { get; private set; }
        public AuditRecordListFilter? Filter { get; private set; }

        public Task<AuditRecordListPage> ListAsync(
            AuditRecordListFilter filter,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            Filter = filter;
            return Task.FromResult(new AuditRecordListPage([], 0, filter.Offset, filter.Limit));
        }

        public Task<AuditRecordDto?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<AuditRecordDto?>(null);
    }
}
