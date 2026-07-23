using THub.Domain.Publications;

namespace THub.Application.Publications;

public sealed record PublicationChangeSetListQuery(
    Guid PublicationId,
    IReadOnlyCollection<Guid> RoleIds,
    IReadOnlyCollection<PublicationChangeSetStatus>? Statuses = null,
    int Take = 50,
    DateTimeOffset? BeforeSubmittedAtUtc = null,
    Guid? BeforeId = null);

public sealed record PublicationChangeDto(
    Guid Id,
    PublicationChangeOperation Operation,
    string? KeyJson,
    string? BeforeJson,
    string? AfterJson);

public sealed record PublicationChangeSetDetailDto(
    Guid Id,
    Guid PublicationId,
    Guid PublicationVersionId,
    PublicationChangeSetStatus Status,
    IReadOnlyList<PublicationChangeDto> Changes,
    string SubmittedBy,
    DateTimeOffset SubmittedAtUtc,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewComment,
    string? ApplyStartedBy,
    DateTimeOffset? ApplyStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? OutcomeDetail,
    DateTimeOffset UpdatedAtUtc);

public sealed record PublicationChangeSetPageDto(
    IReadOnlyList<PublicationChangeSetDto> Items,
    DateTimeOffset? NextBeforeSubmittedAtUtc,
    Guid? NextBeforeId);

public sealed record PublicationChangeSetQueryPage(
    IReadOnlyList<PublicationChangeSet> Items,
    bool HasMore);

public interface IPublicationChangeSetQueryStore
{
    Task<PublicationChangeSetQueryPage> ListAsync(
        Guid publicationId,
        IReadOnlyCollection<PublicationChangeSetStatus> statuses,
        int take,
        DateTimeOffset? beforeSubmittedAtUtc,
        Guid? beforeId,
        CancellationToken cancellationToken);

    Task<PublicationChangeSet?> FindDetailAsync(
        Guid publicationId,
        Guid changeSetId,
        CancellationToken cancellationToken);
}

public sealed class PublicationChangeSetManagementService(
    IPublicationChangeSetQueryStore queryStore,
    PublicationAuthorizationService authorizationService)
{
    private const int MaximumTake = 100;
    private readonly IPublicationChangeSetQueryStore _queryStore =
        queryStore ?? throw new ArgumentNullException(nameof(queryStore));
    private readonly PublicationAuthorizationService _authorizationService =
        authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));

    public async Task<PublicationResult<PublicationChangeSetPageDto>> ListAsync(
        PublicationChangeSetListQuery query,
        CancellationToken cancellationToken)
    {
        if (query is null ||
            query.PublicationId == Guid.Empty ||
            query.Take is < 1 or > MaximumTake ||
            (query.BeforeSubmittedAtUtc is null) != (query.BeforeId is null) ||
            query.BeforeId == Guid.Empty ||
            query.Statuses?.Any(status => !Enum.IsDefined(status)) == true)
        {
            return PublicationResultFactory.Validation<PublicationChangeSetPageDto>(
                "publication.change_query_invalid",
                $"Change-set filters, cursor, and take from 1 to {MaximumTake} must be valid.");
        }

        var authorization = await _authorizationService.AuthorizeAsync(
                query.PublicationId,
                query.RoleIds,
                PublicationOperation.View,
                cancellationToken)
            .ConfigureAwait(false);
        if (!authorization.IsSuccess)
        {
            return CopyFailure<PublicationAuthorizationDto, PublicationChangeSetPageDto>(authorization);
        }

        var statuses = query.Statuses?.Distinct().ToArray() ?? [];
        var page = await _queryStore.ListAsync(
                query.PublicationId,
                statuses,
                query.Take,
                query.BeforeSubmittedAtUtc?.ToUniversalTime(),
                query.BeforeId,
                cancellationToken)
            .ConfigureAwait(false);
        var items = page.Items.Select(PublicationDtoMapper.ToDto).ToArray();
        var last = page.HasMore && page.Items.Count > 0 ? page.Items[^1] : null;
        return PublicationResult<PublicationChangeSetPageDto>.Success(
            new PublicationChangeSetPageDto(
                items,
                last?.SubmittedAtUtc,
                last?.Id));
    }

    public async Task<PublicationResult<PublicationChangeSetDetailDto>> GetAsync(
        Guid publicationId,
        Guid changeSetId,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        if (publicationId == Guid.Empty || changeSetId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<PublicationChangeSetDetailDto>(
                "publication.change_set_id_required",
                "Publication and change-set identifiers are required.");
        }

        var authorization = await _authorizationService.AuthorizeAsync(
                publicationId,
                roleIds,
                PublicationOperation.View,
                cancellationToken)
            .ConfigureAwait(false);
        if (!authorization.IsSuccess)
        {
            return CopyFailure<PublicationAuthorizationDto, PublicationChangeSetDetailDto>(authorization);
        }

        var changeSet = await _queryStore.FindDetailAsync(publicationId, changeSetId, cancellationToken)
            .ConfigureAwait(false);
        return changeSet is null
            ? PublicationResultFactory.NotFound<PublicationChangeSetDetailDto>(
                "publication.change_set_not_found",
                "The publication change set was not found.")
            : PublicationResult<PublicationChangeSetDetailDto>.Success(ToDetail(changeSet));
    }

    private static PublicationChangeSetDetailDto ToDetail(PublicationChangeSet changeSet) =>
        new(
            changeSet.Id,
            changeSet.PublicationId,
            changeSet.PublicationVersionId,
            changeSet.Status,
            changeSet.Changes.Select(change => new PublicationChangeDto(
                change.Id,
                change.Operation,
                change.KeyJson,
                change.BeforeJson,
                change.AfterJson)).ToArray(),
            changeSet.SubmittedBy,
            changeSet.SubmittedAtUtc,
            changeSet.ReviewedBy,
            changeSet.ReviewedAtUtc,
            changeSet.ReviewComment,
            changeSet.ApplyStartedBy,
            changeSet.ApplyStartedAtUtc,
            changeSet.CompletedAtUtc,
            changeSet.OutcomeDetail,
            changeSet.UpdatedAtUtc);

    private static PublicationResult<TTarget> CopyFailure<TSource, TTarget>(
        PublicationResult<TSource> result)
    {
        var problem = result.Problem ?? throw new InvalidOperationException("A failed result requires a problem.");
        return PublicationResult<TTarget>.Failure(problem.Kind, problem.Code, problem.Message);
    }
}

public sealed record PublicationChangeSetClaim(
    PublicationChangeSet ChangeSet,
    string LeaseOwner,
    DateTimeOffset LeaseAcquiredAtUtc,
    DateTimeOffset LeaseExpiresAtUtc);

public enum PublicationChangeSetApplyOutcome
{
    Applied,
    Conflict,
    Failed,
}

public enum PublicationChangeSetProcessStatus
{
    NoWork,
    Applied,
    Conflict,
    Failed,
    LeaseLost,
}

public sealed record PublicationChangeSetProcessResult(
    PublicationChangeSetProcessStatus Status,
    Guid? ChangeSetId,
    string? Detail = null);

public interface IPublicationChangeSetClaimStore
{
    Task<PublicationChangeSetClaim?> ClaimNextAsync(
        string leaseOwner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> RenewAsync(
        Guid changeSetId,
        string leaseOwner,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        Guid changeSetId,
        string leaseOwner,
        PublicationChangeSetApplyOutcome outcome,
        string? detail,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);
}

public interface IPublicationChangeSetProcessor
{
    Task<PublicationChangeSetProcessResult> ProcessNextAsync(
        string workerId,
        CancellationToken cancellationToken);
}
