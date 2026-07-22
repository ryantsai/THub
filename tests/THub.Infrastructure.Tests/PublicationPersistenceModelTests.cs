using Microsoft.EntityFrameworkCore;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Tests;

public sealed class PublicationPersistenceModelTests
{
    [Fact]
    public void DetachedAggregateIncludePaths_AreMapped()
    {
        var options = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=THub_ModelOnly;Integrated Security=true;Encrypt=false")
            .Options;
        using var db = new THubDbContext(options);

        var versionSql = db.PublicationVersions
            .AsNoTracking()
            .Include("_columns.ForeignKey")
            .AsSplitQuery()
            .ToQueryString();
        var changeSetSql = db.PublicationChangeSets
            .AsNoTracking()
            .Include("_changes")
            .AsSplitQuery()
            .ToQueryString();
        var cursorTime = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        var cursorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var cursorSql = db.PublicationChangeSets
            .Where(changeSet =>
                changeSet.SubmittedAtUtc < cursorTime ||
                (changeSet.SubmittedAtUtc == cursorTime && changeSet.Id.CompareTo(cursorId) < 0))
            .ToQueryString();

        Assert.Contains("PublicationVersions", versionSql, StringComparison.Ordinal);
        Assert.Contains("PublicationChangeSets", changeSetSql, StringComparison.Ordinal);
        Assert.Contains("SubmittedAtUtc", cursorSql, StringComparison.Ordinal);
        Assert.NotNull(db.Model.FindEntityType("THub.Domain.Publications.PublicationColumn"));
        Assert.NotNull(db.Model.FindEntityType("THub.Domain.Publications.PublicationChange"));
    }
}
