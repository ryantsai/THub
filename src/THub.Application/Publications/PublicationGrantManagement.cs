using System.Security.Cryptography;
using System.Text;
using THub.Domain.Publications;

namespace THub.Application.Publications;

public sealed record PublicationGrantCommand(
    PublicationRole Role,
    bool CanView,
    bool CanInsert,
    bool CanUpdate,
    bool CanDelete,
    bool CanApprove);

public sealed record ReplacePublicationGrantsCommand(
    Guid PublicationId,
    string ExpectedFingerprint,
    IReadOnlyList<PublicationGrantCommand> Grants);

public sealed record PublicationGrantDto(
    PublicationRole Role,
    bool CanView,
    bool CanInsert,
    bool CanUpdate,
    bool CanDelete,
    bool CanApprove);

public sealed record PublicationGrantSnapshotDto(
    Guid PublicationId,
    string Fingerprint,
    IReadOnlyList<PublicationGrantDto> Grants);

public enum PublicationGrantWriteStatus
{
    Saved,
    NotFound,
    Conflict,
}

public interface IPublicationGrantManagementStore
{
    Task<PublicationGrantWriteStatus> ReplaceAsync(
        Guid publicationId,
        string expectedFingerprint,
        IReadOnlyList<PublicationGrant> grants,
        CancellationToken cancellationToken);
}

public sealed class PublicationGrantManagementService(
    IPublicationCatalogStore catalogStore,
    IPublicationGrantStore grantStore,
    IPublicationGrantManagementStore managementStore)
{
    private readonly IPublicationCatalogStore _catalogStore =
        catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
    private readonly IPublicationGrantStore _grantStore =
        grantStore ?? throw new ArgumentNullException(nameof(grantStore));
    private readonly IPublicationGrantManagementStore _managementStore =
        managementStore ?? throw new ArgumentNullException(nameof(managementStore));

    public async Task<PublicationResult<PublicationGrantSnapshotDto>> GetAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        var publicationResult = await RequireEditorAsync(publicationId, cancellationToken)
            .ConfigureAwait(false);
        if (!publicationResult.IsSuccess)
        {
            return CopyFailure<Publication, PublicationGrantSnapshotDto>(publicationResult);
        }

        var grants = await _grantStore.ListAsync(publicationId, cancellationToken).ConfigureAwait(false);
        return PublicationResult<PublicationGrantSnapshotDto>.Success(ToSnapshot(publicationId, grants));
    }

    public async Task<PublicationResult<PublicationGrantSnapshotDto>> ReplaceAsync(
        ReplacePublicationGrantsCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null ||
            command.PublicationId == Guid.Empty ||
            command.Grants is null ||
            string.IsNullOrWhiteSpace(command.ExpectedFingerprint) ||
            command.ExpectedFingerprint.Length > 128)
        {
            return PublicationResultFactory.Validation<PublicationGrantSnapshotDto>(
                "publication.grants_command_invalid",
                "A publication, current grant fingerprint, and bounded grant set are required.");
        }

        if (command.Grants.Count > Enum.GetValues<PublicationRole>().Length ||
            command.Grants.Any(grant => grant is null || !Enum.IsDefined(grant.Role)) ||
            command.Grants.Select(grant => grant.Role).Distinct().Count() != command.Grants.Count)
        {
            return PublicationResultFactory.Validation<PublicationGrantSnapshotDto>(
                "publication.grants_invalid",
                "Each supported role can appear at most once in a grant set.");
        }

        var publicationResult = await RequireEditorAsync(command.PublicationId, cancellationToken)
            .ConfigureAwait(false);
        if (!publicationResult.IsSuccess)
        {
            return CopyFailure<Publication, PublicationGrantSnapshotDto>(publicationResult);
        }

        var grants = command.Grants
            .Select(grant => new PublicationGrant(
                Guid.NewGuid(),
                command.PublicationId,
                grant.Role,
                grant.CanView,
                grant.CanInsert,
                grant.CanUpdate,
                grant.CanDelete,
                grant.CanApprove))
            .ToArray();
        var status = await _managementStore.ReplaceAsync(
                command.PublicationId,
                command.ExpectedFingerprint,
                grants,
                cancellationToken)
            .ConfigureAwait(false);
        if (status == PublicationGrantWriteStatus.NotFound)
        {
            return PublicationResultFactory.NotFound<PublicationGrantSnapshotDto>(
                "publication.not_found",
                "The editor publication was not found.");
        }

        if (status != PublicationGrantWriteStatus.Saved)
        {
            return PublicationResultFactory.Conflict<PublicationGrantSnapshotDto>(
                "publication.grants_conflict",
                "Publication grants changed concurrently. Reload before saving.");
        }

        return PublicationResult<PublicationGrantSnapshotDto>.Success(
            ToSnapshot(command.PublicationId, grants));
    }

    private async Task<PublicationResult<Publication>> RequireEditorAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        if (publicationId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<Publication>(
                "publication.id_required",
                "A publication identifier is required.");
        }

        var publication = await _catalogStore.FindAsync(publicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<Publication>(
                "publication.not_found",
                "The publication was not found.");
        }

        return publication.Kind != PublicationKind.Editor || publication.State == PublicationState.Archived
            ? PublicationResultFactory.Conflict<Publication>(
                "publication.grants_editor_only",
                "Role grants can be managed only for a non-archived editor publication.")
            : PublicationResult<Publication>.Success(publication);
    }

    private static PublicationGrantSnapshotDto ToSnapshot(
        Guid publicationId,
        IReadOnlyList<PublicationGrant> grants) =>
        new(
            publicationId,
            PublicationGrantFingerprint.Compute(grants),
            grants
                .OrderBy(grant => grant.Role)
                .Select(grant => new PublicationGrantDto(
                    grant.Role,
                    grant.CanView,
                    grant.CanInsert,
                    grant.CanUpdate,
                    grant.CanDelete,
                    grant.CanApprove))
                .ToArray());

    private static PublicationResult<TTarget> CopyFailure<TSource, TTarget>(
        PublicationResult<TSource> result)
    {
        var problem = result.Problem ?? throw new InvalidOperationException("A failed result requires a problem.");
        return PublicationResult<TTarget>.Failure(problem.Kind, problem.Code, problem.Message);
    }
}

public static class PublicationGrantFingerprint
{
    public static string Compute(IEnumerable<PublicationGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var canonical = string.Join(
            ';',
            grants
                .OrderBy(grant => grant.Role)
                .Select(grant => string.Concat(
                    ((int)grant.Role).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ':',
                    grant.CanView ? '1' : '0',
                    grant.CanInsert ? '1' : '0',
                    grant.CanUpdate ? '1' : '0',
                    grant.CanDelete ? '1' : '0',
                    grant.CanApprove ? '1' : '0')));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
