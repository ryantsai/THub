using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Alerts;
using THub.Domain.Alerts;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Alerts;

public sealed class SqlEmailAlertAdministrationStore(
    IDbContextFactory<THubDbContext> contextFactory) : IEmailAlertAdministrationStore
{
    private readonly IDbContextFactory<THubDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task<IReadOnlyList<EmailDeliveryProfile>> ListProfilesAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.EmailDeliveryProfiles
            .AsNoTracking()
            .OrderBy(profile => profile.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<EmailDeliveryProfile?> FindProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.EmailDeliveryProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(profile => profile.Id == profileId, cancellationToken);
    }

    public async Task<EmailAlertAdministrationWriteStatus> AddProfileAsync(
        EmailDeliveryProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        db.EmailDeliveryProfiles.Add(profile);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return EmailAlertAdministrationWriteStatus.Saved;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return EmailAlertAdministrationWriteStatus.DuplicateName;
        }
    }

    public async Task<EmailAlertAdministrationWriteStatus> SaveProfileAsync(
        EmailDeliveryProfile profile,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await db.EmailDeliveryProfiles.SingleOrDefaultAsync(
            candidate => candidate.Id == profile.Id,
            cancellationToken);
        if (current is null)
        {
            return EmailAlertAdministrationWriteStatus.NotFound;
        }

        if (current.UpdatedAtUtc != expectedUpdatedAtUtc.ToUniversalTime())
        {
            return EmailAlertAdministrationWriteStatus.Conflict;
        }

        db.Entry(current).CurrentValues.SetValues(profile);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return EmailAlertAdministrationWriteStatus.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            return EmailAlertAdministrationWriteStatus.Conflict;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return EmailAlertAdministrationWriteStatus.DuplicateName;
        }
    }

    public async Task<bool> WorkflowExistsAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Workflows.AnyAsync(
            workflow => workflow.Id == workflowId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowAlertRule>> ListRulesAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkflowAlertRules
            .AsNoTracking()
            .Where(rule => rule.WorkflowId == workflowId)
            .OrderBy(rule => rule.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowAlertRule>> ListRulesForProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkflowAlertRules
            .AsNoTracking()
            .Where(rule => rule.EmailDeliveryProfileId == profileId)
            .OrderBy(rule => rule.WorkflowId)
            .ThenBy(rule => rule.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkflowAlertRule?> FindRuleAsync(
        Guid ruleId,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkflowAlertRules
            .AsNoTracking()
            .SingleOrDefaultAsync(rule => rule.Id == ruleId, cancellationToken);
    }

    public async Task<EmailAlertAdministrationWriteStatus> AddRuleAsync(
        WorkflowAlertRule rule,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        db.WorkflowAlertRules.Add(rule);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return EmailAlertAdministrationWriteStatus.Saved;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return EmailAlertAdministrationWriteStatus.DuplicateName;
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            return EmailAlertAdministrationWriteStatus.ReferencedResourceUnavailable;
        }
    }

    public async Task<EmailAlertAdministrationWriteStatus> SaveRuleAsync(
        WorkflowAlertRule rule,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await db.WorkflowAlertRules.SingleOrDefaultAsync(
            candidate => candidate.Id == rule.Id,
            cancellationToken);
        if (current is null)
        {
            return EmailAlertAdministrationWriteStatus.NotFound;
        }

        if (current.UpdatedAtUtc != expectedUpdatedAtUtc.ToUniversalTime())
        {
            return EmailAlertAdministrationWriteStatus.Conflict;
        }

        db.Entry(current).CurrentValues.SetValues(rule);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return EmailAlertAdministrationWriteStatus.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            return EmailAlertAdministrationWriteStatus.Conflict;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return EmailAlertAdministrationWriteStatus.DuplicateName;
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            return EmailAlertAdministrationWriteStatus.ReferencedResourceUnavailable;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private static bool IsForeignKeyViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 547 };
}
