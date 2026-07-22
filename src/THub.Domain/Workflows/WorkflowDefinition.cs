namespace THub.Domain.Workflows;

public sealed class WorkflowDefinition
{
    public const int MaximumNameLength = 200;
    public const int MaximumDescriptionLength = 2_000;
    public const int MaximumOwnerLength = 256;
    public const int MaximumCronExpressionLength = 200;
    public const int MaximumTimeZoneIdLength = 200;

    private WorkflowDefinition() { }

    public WorkflowDefinition(string name, string owner, string graphJson)
        : this(name, owner, graphJson, DateTimeOffset.UtcNow)
    {
    }

    public WorkflowDefinition(
        string name,
        string owner,
        string graphJson,
        DateTimeOffset createdAtUtc,
        string? description = null)
    {
        var timestamp = DomainGuard.Utc(createdAtUtc, nameof(createdAtUtc));

        Id = Guid.NewGuid();
        Name = DomainGuard.Require(name, nameof(name), MaximumNameLength);
        Description = DomainGuard.Optional(
            description,
            nameof(description),
            MaximumDescriptionLength);
        Owner = DomainGuard.Require(owner, nameof(owner), MaximumOwnerLength);
        GraphJson = DomainGuard.Require(
            graphJson,
            nameof(graphJson),
            WorkflowVersion.MaximumGraphLength);
        CreatedAtUtc = timestamp;
        UpdatedAtUtc = timestamp;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public string Owner { get; private set; } = string.Empty;

    public WorkflowStatus Status { get; private set; } = WorkflowStatus.Draft;

    /// <summary>
    /// The candidate version number. It advances once when a published graph becomes a new draft.
    /// </summary>
    public int Version { get; private set; } = 1;

    /// <summary>
    /// Monotonically increasing revision of editable workflow state. A completed save
    /// advances it exactly once, including metadata- or schedule-only saves.
    /// </summary>
    public int DraftRevision { get; private set; } = 1;

    public string GraphJson { get; private set; } = "{}";

    public Guid? PublishedVersionId { get; private set; }

    public int? PublishedVersionNumber { get; private set; }

    public string? CronExpression { get; private set; }

    public string TimeZoneId { get; private set; } = TimeZoneInfo.Utc.Id;

    public DateTimeOffset? NextRunAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? ArchivedAtUtc { get; private set; }

    public void Rename(string name) => Rename(name, DateTimeOffset.UtcNow);

    public void Rename(string name, DateTimeOffset changedAtUtc)
    {
        EnsureNotArchived();
        Name = DomainGuard.Require(name, nameof(name), MaximumNameLength);
        Touch(changedAtUtc);
    }

    public void SetDescription(string? description) =>
        SetDescription(description, DateTimeOffset.UtcNow);

    public void SetDescription(string? description, DateTimeOffset changedAtUtc)
    {
        EnsureNotArchived();
        Description = DomainGuard.Optional(
            description,
            nameof(description),
            MaximumDescriptionLength);
        Touch(changedAtUtc);
    }

    public void TransferOwnership(string owner, DateTimeOffset changedAtUtc)
    {
        EnsureNotArchived();
        Owner = DomainGuard.Require(owner, nameof(owner), MaximumOwnerLength);
        Touch(changedAtUtc);
    }

    public void UpdateGraph(string graphJson) => UpdateGraph(graphJson, DateTimeOffset.UtcNow);

    public void UpdateGraph(string graphJson, DateTimeOffset changedAtUtc)
    {
        EnsureNotArchived();
        var timestamp = ValidateMutationTime(changedAtUtc);
        var nextGraph = DomainGuard.Require(
            graphJson,
            nameof(graphJson),
            WorkflowVersion.MaximumGraphLength);

        if (string.Equals(GraphJson, nextGraph, StringComparison.Ordinal))
        {
            return;
        }

        if (Status is WorkflowStatus.Published or WorkflowStatus.Paused)
        {
            Version = checked((PublishedVersionNumber ?? Version) + 1);
        }

        GraphJson = nextGraph;
        DraftRevision = checked(DraftRevision + 1);
        Status = WorkflowStatus.Draft;
        NextRunAtUtc = null;
        UpdatedAtUtc = timestamp;
    }

    /// <summary>
    /// Completes one aggregate save after its individual values have been applied.
    /// <see cref="UpdateGraph(string, DateTimeOffset)"/> may already have advanced the
    /// revision; this method prevents that graph change from counting twice while still
    /// advancing metadata- and schedule-only saves.
    /// </summary>
    public void CompleteSave(int startingDraftRevision, DateTimeOffset changedAtUtc)
    {
        EnsureNotArchived();
        var startingRevision = DomainGuard.RequirePositive(
            startingDraftRevision,
            nameof(startingDraftRevision));
        var timestamp = ValidateMutationTime(changedAtUtc);

        if (DraftRevision == startingRevision)
        {
            DraftRevision = checked(DraftRevision + 1);
        }
        else if (DraftRevision != checked(startingRevision + 1))
        {
            throw new InvalidOperationException(
                "A save can advance the workflow draft revision exactly once.");
        }

        UpdatedAtUtc = timestamp;
    }

    public void SetSchedule(
        string? cronExpression,
        string timeZoneId,
        DateTimeOffset? nextRunAtUtc) =>
        SetSchedule(
            cronExpression,
            timeZoneId,
            nextRunAtUtc,
            DateTimeOffset.UtcNow);

    public void SetSchedule(
        string? cronExpression,
        string timeZoneId,
        DateTimeOffset? nextRunAtUtc,
        DateTimeOffset changedAtUtc)
    {
        EnsureNotArchived();

        var normalizedCron = string.IsNullOrWhiteSpace(cronExpression)
            ? null
            : DomainGuard.Require(
                cronExpression,
                nameof(cronExpression),
                MaximumCronExpressionLength);
        var normalizedTimeZone = DomainGuard.Require(
            timeZoneId,
            nameof(timeZoneId),
            MaximumTimeZoneIdLength);
        var normalizedNextRun = nextRunAtUtc?.ToUniversalTime();

        if (normalizedCron is null && normalizedNextRun is not null)
        {
            throw new ArgumentException(
                "A next run cannot be set when the workflow has no schedule.",
                nameof(nextRunAtUtc));
        }

        if (Status is not WorkflowStatus.Published && normalizedNextRun is not null)
        {
            throw new InvalidOperationException(
                "Only a published workflow can have a next scheduled occurrence.");
        }

        CronExpression = normalizedCron;
        TimeZoneId = normalizedTimeZone;
        NextRunAtUtc = normalizedNextRun;
        Touch(changedAtUtc);
    }

    public void Publish(
        WorkflowVersion publishedVersion,
        DateTimeOffset? nextRunAtUtc = null,
        DateTimeOffset? changedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(publishedVersion);
        EnsureNotArchived();

        if (Status is not (WorkflowStatus.Draft or WorkflowStatus.Paused))
        {
            throw new InvalidOperationException(
                $"A workflow in the {Status} state cannot be published.");
        }

        if (publishedVersion.WorkflowId != Id)
        {
            throw new ArgumentException(
                "The published snapshot belongs to a different workflow.",
                nameof(publishedVersion));
        }

        if (publishedVersion.Version != Version)
        {
            throw new ArgumentException(
                "The published snapshot version does not match the current draft version.",
                nameof(publishedVersion));
        }

        if (!string.Equals(publishedVersion.GraphJson, GraphJson, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The published snapshot does not contain the current draft graph.",
                nameof(publishedVersion));
        }

        if (Status == WorkflowStatus.Paused && PublishedVersionId != publishedVersion.Id)
        {
            throw new InvalidOperationException(
                "A paused workflow can only resume its existing immutable version.");
        }

        var publishedAtUtc = ValidateMutationTime(
            changedAtUtc ?? publishedVersion.PublishedAtUtc);
        var normalizedNextRun = ValidateNextRun(nextRunAtUtc);

        PublishedVersionId = publishedVersion.Id;
        PublishedVersionNumber = publishedVersion.Version;
        Status = WorkflowStatus.Published;
        NextRunAtUtc = normalizedNextRun;
        UpdatedAtUtc = publishedAtUtc;
    }

    public void Pause() => Pause(DateTimeOffset.UtcNow);

    public void Pause(DateTimeOffset pausedAtUtc)
    {
        if (Status != WorkflowStatus.Published)
        {
            throw new InvalidOperationException("Only a published workflow can be paused.");
        }

        Status = WorkflowStatus.Paused;
        NextRunAtUtc = null;
        Touch(pausedAtUtc);
    }

    public void Archive(DateTimeOffset archivedAtUtc)
    {
        EnsureNotArchived();
        var timestamp = ValidateMutationTime(archivedAtUtc);

        Status = WorkflowStatus.Archived;
        NextRunAtUtc = null;
        ArchivedAtUtc = timestamp;
        UpdatedAtUtc = timestamp;
    }

    public void MarkScheduled(DateTimeOffset? nextRunAtUtc) =>
        MarkScheduled(nextRunAtUtc, DateTimeOffset.UtcNow);

    public void MarkScheduled(DateTimeOffset? nextRunAtUtc, DateTimeOffset changedAtUtc)
    {
        if (Status != WorkflowStatus.Published)
        {
            throw new InvalidOperationException(
                "Only a published workflow can advance its schedule.");
        }

        NextRunAtUtc = ValidateNextRun(nextRunAtUtc);
        Touch(changedAtUtc);
    }

    private DateTimeOffset? ValidateNextRun(DateTimeOffset? nextRunAtUtc)
    {
        if (nextRunAtUtc is not null && CronExpression is null)
        {
            throw new InvalidOperationException(
                "A next run cannot be set when the workflow has no schedule.");
        }

        return nextRunAtUtc?.ToUniversalTime();
    }

    private void Touch(DateTimeOffset changedAtUtc) =>
        UpdatedAtUtc = ValidateMutationTime(changedAtUtc);

    private DateTimeOffset ValidateMutationTime(DateTimeOffset changedAtUtc) =>
        DomainGuard.OnOrAfter(changedAtUtc, UpdatedAtUtc, nameof(changedAtUtc));

    private void EnsureNotArchived()
    {
        if (Status == WorkflowStatus.Archived)
        {
            throw new InvalidOperationException("An archived workflow cannot be changed.");
        }
    }
}
