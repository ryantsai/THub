using System.Globalization;
using THub.Domain.Publications;

namespace THub.Application.Publications;

public sealed class PublicationDataService(
    IPublicationCatalogStore catalogStore,
    IPublicationSourceDataReader sourceDataReader,
    PublicationAuthorizationService? authorizationService = null)
{
    private const int MaximumFilters = 16;
    private const int MaximumRequestedSorts = 8;
    private const int MaximumCursorLength = 4_096;
    private const int MaximumFilterValueLength = 4_096;
    private const int MaximumLookupTake = 100;
    private const int MaximumLookupSearchLength = 256;
    public const int MaximumForeignKeyTuples = 4_096;

    private readonly IPublicationCatalogStore _catalogStore =
        catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
    private readonly IPublicationSourceDataReader _sourceDataReader =
        sourceDataReader ?? throw new ArgumentNullException(nameof(sourceDataReader));
    private readonly PublicationAuthorizationService? _authorizationService = authorizationService;

    public async Task<PublicationResult<PublicationRowPageDto>> ReadRestRowsAsync(
        AuthenticatedPublicationTokenDto authentication,
        PublicationRestRowsQuery query,
        CancellationToken cancellationToken)
    {
        if (authentication is null || query is null ||
            authentication.PublicationId == Guid.Empty ||
            authentication.PublicationVersionId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<PublicationRowPageDto>(
                "publication.read_request_invalid",
                "Authenticated publication context and a row query are required.");
        }

        var active = await FindActiveVersionAsync(
                authentication.PublicationId,
                authentication.PublicationVersionId,
                PublicationKind.RestApi,
                cancellationToken)
            .ConfigureAwait(false);
        if (!active.IsSuccess)
        {
            return CopyFailure<PublicationVersion, PublicationRowPageDto>(active);
        }

        var version = active.Value!;
        var take = query.PageSize ?? version.Settings.DefaultPageSize;
        var validated = ValidateReadQuery(
            version,
            take,
            version.Settings.MaximumPageSize,
            query.Cursor,
            query.Filters,
            query.Sorts);
        if (!validated.IsSuccess)
        {
            return CopyFailure<PublicationSourceReadQuery, PublicationRowPageDto>(validated);
        }

        return await ReadRowsAsync(version, validated.Value!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublicationResult<PublicationRowPageDto>> ReadEditorRowsAsync(
        Guid publicationId,
        IReadOnlyCollection<PublicationRole> roles,
        PublicationEditorRowsQuery query,
        CancellationToken cancellationToken)
    {
        if (query is null)
        {
            return PublicationResultFactory.Validation<PublicationRowPageDto>(
                "publication.read_request_invalid",
                "An editor row query is required.");
        }

        var authorization = await RequireEditorAuthorizationService().AuthorizeAsync(
                publicationId,
                roles,
                PublicationOperation.View,
                cancellationToken)
            .ConfigureAwait(false);
        if (!authorization.IsSuccess)
        {
            return CopyFailure<PublicationAuthorizationDto, PublicationRowPageDto>(authorization);
        }

        var publication = await _catalogStore.FindAsync(publicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<PublicationRowPageDto>(
                "publication.not_found",
                "The publication was not found.");
        }

        if (publication.ActiveVersionId is not Guid versionId)
        {
            return PublicationResultFactory.Conflict<PublicationRowPageDto>(
                "publication.not_active",
                "The editor publication does not have an active version.");
        }

        var active = await FindActiveVersionAsync(
                publicationId,
                versionId,
                PublicationKind.Editor,
                cancellationToken)
            .ConfigureAwait(false);
        if (!active.IsSuccess)
        {
            return CopyFailure<PublicationVersion, PublicationRowPageDto>(active);
        }

        var version = active.Value!;
        var take = query.WindowSize ?? version.Settings.EditorWindowSize;
        var validated = ValidateReadQuery(
            version,
            take,
            version.Settings.EditorWindowSize,
            query.Cursor,
            query.Filters,
            query.Sorts);
        if (!validated.IsSuccess)
        {
            return CopyFailure<PublicationSourceReadQuery, PublicationRowPageDto>(validated);
        }

        return await ReadRowsAsync(version, validated.Value!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublicationResult<PublicationLookupPageDto>> ReadForeignKeyLookupAsync(
        PublicationForeignKeyLookupQuery query,
        CancellationToken cancellationToken)
    {
        if (query is null || query.PublicationId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<PublicationLookupPageDto>(
                "publication.lookup_request_invalid",
                "A publication and foreign-key lookup query are required.");
        }

        var authorization = await RequireEditorAuthorizationService().AuthorizeAsync(
                query.PublicationId,
                query.Roles,
                PublicationOperation.View,
                cancellationToken)
            .ConfigureAwait(false);
        if (!authorization.IsSuccess)
        {
            return CopyFailure<PublicationAuthorizationDto, PublicationLookupPageDto>(authorization);
        }

        if (string.IsNullOrWhiteSpace(query.ColumnAlias) ||
            query.Take is < 1 or > MaximumLookupTake ||
            query.Search?.Length > MaximumLookupSearchLength ||
            query.Cursor?.Length > MaximumCursorLength)
        {
            return PublicationResultFactory.Validation<PublicationLookupPageDto>(
                "publication.lookup_bounds_invalid",
                $"Lookup take must be between 1 and {MaximumLookupTake}; search and cursor values are bounded.");
        }

        var publication = await _catalogStore.FindAsync(query.PublicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<PublicationLookupPageDto>(
                "publication.not_found",
                "The publication was not found.");
        }

        if (publication.Kind != PublicationKind.Editor ||
            publication.State != PublicationState.Active ||
            publication.ActiveVersionId is not Guid versionId)
        {
            return PublicationResultFactory.Conflict<PublicationLookupPageDto>(
                "publication.editor_not_active",
                "Foreign-key lookups require an active editor publication.");
        }

        var version = await _catalogStore.FindVersionAsync(query.PublicationId, versionId, cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return PublicationResultFactory.Conflict<PublicationLookupPageDto>(
                "publication.active_version_missing",
                "The active publication version is unavailable.");
        }

        var column = version.Columns.SingleOrDefault(column =>
            column.IsReadable &&
            string.Equals(column.PublicAlias, query.ColumnAlias, StringComparison.OrdinalIgnoreCase));
        if (column?.ForeignKey is null || !HasAtomicReadableForeignKeyPolicy(version, column))
        {
            return PublicationResultFactory.Validation<PublicationLookupPageDto>(
                "publication.lookup_column_invalid",
                "The requested column does not expose an approved foreign-key lookup.");
        }

        var sourceResult = await _sourceDataReader.ReadForeignKeyLookupAsync(
                version,
                column,
                new PublicationForeignKeySourceQuery(query.Take, query.Cursor, query.Search?.Trim()),
                cancellationToken)
            .ConfigureAwait(false);
        if (sourceResult.Status != PublicationSourceReadStatus.Success || sourceResult.Value is null)
        {
            return MapSourceFailure<PublicationLookupPageDto>(sourceResult.Status);
        }

        if (sourceResult.Value.Items.Count > query.Take ||
            sourceResult.Value.NextCursor?.Length > MaximumCursorLength)
        {
            return PublicationResultFactory.Unavailable<PublicationLookupPageDto>(
                "publication.source_bounds_violated",
                "The source adapter returned an out-of-bounds lookup page.");
        }

        return PublicationResult<PublicationLookupPageDto>.Success(
            new PublicationLookupPageDto(sourceResult.Value.Items, sourceResult.Value.NextCursor));
    }

    public async Task<PublicationResult<PublicationForeignKeyLabelResult>> ResolveForeignKeyLabelsAsync(
        PublicationForeignKeyLabelQuery query,
        CancellationToken cancellationToken)
    {
        if (query is null || query.PublicationId == Guid.Empty || query.Requests is null ||
            query.Requests.Count > MaximumForeignKeyTuples)
        {
            return PublicationResultFactory.Validation<PublicationForeignKeyLabelResult>(
                "publication.lookup_resolution_invalid",
                $"A publication and at most {MaximumForeignKeyTuples} bounded lookup tuples are required.");
        }

        var authorization = await RequireEditorAuthorizationService().AuthorizeAsync(
                query.PublicationId,
                query.Roles,
                PublicationOperation.View,
                cancellationToken)
            .ConfigureAwait(false);
        if (!authorization.IsSuccess)
        {
            return CopyFailure<PublicationAuthorizationDto, PublicationForeignKeyLabelResult>(authorization);
        }

        var publication = await _catalogStore.FindAsync(query.PublicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<PublicationForeignKeyLabelResult>(
                "publication.not_found",
                "The publication was not found.");
        }

        if (publication.Kind != PublicationKind.Editor ||
            publication.State != PublicationState.Active ||
            publication.ActiveVersionId is not Guid versionId)
        {
            return PublicationResultFactory.Conflict<PublicationForeignKeyLabelResult>(
                "publication.editor_not_active",
                "Foreign-key label resolution requires an active editor publication.");
        }

        var version = await _catalogStore.FindVersionAsync(query.PublicationId, versionId, cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return PublicationResultFactory.Conflict<PublicationForeignKeyLabelResult>(
                "publication.active_version_missing",
                "The active publication version is unavailable.");
        }

        var requestIds = new HashSet<int>();
        var tuples = new List<PublicationForeignKeyTuple>(query.Requests.Count);
        foreach (var request in query.Requests)
        {
            if (request is null || request.RequestId < 0 || !requestIds.Add(request.RequestId) ||
                string.IsNullOrWhiteSpace(request.ColumnAlias) || request.KeyValues is null)
            {
                return InvalidLabelResolution();
            }

            var column = version.Columns.SingleOrDefault(candidate =>
                candidate.IsReadable &&
                candidate.ForeignKey is not null &&
                string.Equals(candidate.PublicAlias, request.ColumnAlias, StringComparison.OrdinalIgnoreCase));
            if (column?.ForeignKey is null)
            {
                return InvalidLabelResolution();
            }

            if (!HasAtomicReadableForeignKeyPolicy(version, column))
            {
                return InvalidLabelResolution();
            }

            var groupAliases = version.Columns
                .Where(candidate => string.Equals(
                    candidate.ForeignKey?.ConstraintName,
                    column.ForeignKey.ConstraintName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.PublicAlias)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (request.KeyValues.Count != groupAliases.Count ||
                request.KeyValues.Keys.Any(key => !groupAliases.Contains(key)))
            {
                return InvalidLabelResolution();
            }

            var nullCount = request.KeyValues.Values.Count(value => value is null or DBNull);
            if (nullCount == request.KeyValues.Count)
            {
                continue;
            }

            if (nullCount > 0)
            {
                return InvalidLabelResolution();
            }

            tuples.Add(new PublicationForeignKeyTuple(
                request.RequestId,
                column.ForeignKey.ConstraintName,
                request.KeyValues));
        }

        if (tuples.Count == 0)
        {
            return PublicationResult<PublicationForeignKeyLabelResult>.Success(
                new PublicationForeignKeyLabelResult([]));
        }

        var sourceResult = await _sourceDataReader.ResolveForeignKeysAsync(
                version,
                tuples,
                cancellationToken)
            .ConfigureAwait(false);
        if (sourceResult.Status != PublicationSourceReadStatus.Success || sourceResult.Value is null)
        {
            return MapSourceFailure<PublicationForeignKeyLabelResult>(sourceResult.Status);
        }

        var labels = sourceResult.Value.Labels;
        if (labels.Count > tuples.Count ||
            labels.Select(label => label.RequestId).Distinct().Count() != labels.Count ||
            labels.Any(label => !requestIds.Contains(label.RequestId) || label.DisplayText.Length > 4_000))
        {
            return PublicationResultFactory.Unavailable<PublicationForeignKeyLabelResult>(
                "publication.source_bounds_violated",
                "The source adapter returned out-of-bounds foreign-key labels.");
        }

        return PublicationResult<PublicationForeignKeyLabelResult>.Success(
            new PublicationForeignKeyLabelResult(labels));
    }

    private static PublicationResult<PublicationForeignKeyLabelResult> InvalidLabelResolution() =>
        PublicationResultFactory.Validation<PublicationForeignKeyLabelResult>(
            "publication.lookup_resolution_invalid",
            "Every lookup tuple must identify one complete, non-partial approved foreign key.");

    private PublicationAuthorizationService RequireEditorAuthorizationService() =>
        _authorizationService ?? throw new InvalidOperationException(
            "Editor data operations are unavailable in the read-only publication API host.");

    private static bool HasAtomicReadableForeignKeyPolicy(
        PublicationVersion version,
        PublicationColumn requestedColumn)
    {
        var foreignKey = requestedColumn.ForeignKey;
        if (foreignKey is null)
        {
            return false;
        }

        var group = version.Columns
            .Where(column => string.Equals(
                column.ForeignKey?.ConstraintName,
                foreignKey.ConstraintName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return foreignKey.SearchColumns.Count > 0 &&
            group.Length == foreignKey.ColumnCount &&
            group.All(column => column.IsReadable) &&
            group.All(column =>
                column.ForeignKey is not null &&
                string.Equals(column.ForeignKey.ReferencedSchema, foreignKey.ReferencedSchema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(column.ForeignKey.ReferencedObject, foreignKey.ReferencedObject, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(column.ForeignKey.DisplayColumn, foreignKey.DisplayColumn, StringComparison.OrdinalIgnoreCase) &&
                column.ForeignKey.SearchColumns.SequenceEqual(foreignKey.SearchColumns, StringComparer.OrdinalIgnoreCase) &&
                column.ForeignKey.LookupMode == foreignKey.LookupMode) &&
            (!foreignKey.IsComposite ||
             (group.Select(column => column.IsWritable).Distinct().Count() == 1 &&
              group.Select(column => column.IsNullable).Distinct().Count() == 1));
    }

    private async Task<PublicationResult<PublicationVersion>> FindActiveVersionAsync(
        Guid publicationId,
        Guid versionId,
        PublicationKind expectedKind,
        CancellationToken cancellationToken)
    {
        var publication = await _catalogStore.FindAsync(publicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<PublicationVersion>(
                "publication.not_found",
                "The publication was not found.");
        }

        if (publication.Kind != expectedKind ||
            publication.State != PublicationState.Active ||
            publication.ActiveVersionId != versionId)
        {
            return PublicationResultFactory.Conflict<PublicationVersion>(
                "publication.not_active",
                "The requested publication version is not active.");
        }

        var version = await _catalogStore.FindVersionAsync(publicationId, versionId, cancellationToken)
            .ConfigureAwait(false);
        return version is null
            ? PublicationResultFactory.Conflict<PublicationVersion>(
                "publication.active_version_missing",
                "The active publication version is unavailable.")
            : PublicationResult<PublicationVersion>.Success(version);
    }

    private async Task<PublicationResult<PublicationRowPageDto>> ReadRowsAsync(
        PublicationVersion version,
        PublicationSourceReadQuery query,
        CancellationToken cancellationToken)
    {
        var sourceResult = await _sourceDataReader.ReadRowsAsync(version, query, cancellationToken)
            .ConfigureAwait(false);
        if (sourceResult.Status != PublicationSourceReadStatus.Success || sourceResult.Value is null)
        {
            return MapSourceFailure<PublicationRowPageDto>(sourceResult.Status);
        }

        var page = sourceResult.Value;
        var readableAliases = version.Columns
            .Where(column => column.IsReadable)
            .Select(column => column.PublicAlias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (page.Rows.Count > query.Take ||
            page.NextCursor?.Length > MaximumCursorLength ||
            page.Rows.Any(row => row.Keys.Any(key => !readableAliases.Contains(key))))
        {
            return PublicationResultFactory.Unavailable<PublicationRowPageDto>(
                "publication.source_bounds_violated",
                "The source adapter returned out-of-bounds or unapproved row data.");
        }

        return PublicationResult<PublicationRowPageDto>.Success(
            new PublicationRowPageDto(
                page.Rows.Select(row => new PublicationRowDto(row)).ToArray(),
                page.NextCursor));
    }

    private static PublicationResult<PublicationSourceReadQuery> ValidateReadQuery(
        PublicationVersion version,
        int take,
        int maximumTake,
        string? cursor,
        IReadOnlyList<PublicationFilter>? filters,
        IReadOnlyList<PublicationSort>? sorts)
    {
        filters ??= [];
        sorts ??= [];
        if (take < 1 || take > maximumTake ||
            cursor?.Length > MaximumCursorLength ||
            filters.Count > MaximumFilters ||
            sorts.Count > MaximumRequestedSorts)
        {
            return PublicationResultFactory.Validation<PublicationSourceReadQuery>(
                "publication.query_bounds_invalid",
                "The requested page, cursor, filter count, or sort count exceeds publication bounds.");
        }

        var columns = version.Columns.ToDictionary(
            column => column.PublicAlias,
            StringComparer.OrdinalIgnoreCase);
        foreach (var filter in filters)
        {
            if (filter is null ||
                !Enum.IsDefined(filter.Operator) ||
                !columns.TryGetValue(filter.ColumnAlias ?? string.Empty, out var column) ||
                !column.IsReadable ||
                !column.IsFilterable ||
                filter.Value?.Length > MaximumFilterValueLength ||
                (filter.Operator is not PublicationFilterOperator.IsNull and
                    not PublicationFilterOperator.IsNotNull && filter.Value is null) ||
                (filter.Operator is PublicationFilterOperator.IsNull or
                    PublicationFilterOperator.IsNotNull && filter.Value is not null) ||
                !IsValidFilterValue(column, filter.Operator, filter.Value))
            {
                return PublicationResultFactory.Validation<PublicationSourceReadQuery>(
                    "publication.filter_invalid",
                    "A filter uses an unapproved column, operator, or value.");
            }
        }

        var requestedSortAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sort in sorts)
        {
            if (sort is null ||
                !columns.TryGetValue(sort.ColumnAlias ?? string.Empty, out var column) ||
                !column.IsReadable ||
                !column.IsSortable ||
                !requestedSortAliases.Add(column.PublicAlias))
            {
                return PublicationResultFactory.Validation<PublicationSourceReadQuery>(
                    "publication.sort_invalid",
                    "A sort uses an unapproved or duplicate column.");
            }
        }

        var deterministicSorts = sorts.ToList();
        foreach (var keyColumn in version.Columns
                     .Where(column => column.IsKey)
                     .OrderBy(column => column.KeyOrdinal))
        {
            if (!deterministicSorts.Any(sort => string.Equals(
                    sort.ColumnAlias,
                    keyColumn.PublicAlias,
                    StringComparison.OrdinalIgnoreCase)))
            {
                deterministicSorts.Add(new PublicationSort(keyColumn.PublicAlias));
            }
        }

        return PublicationResult<PublicationSourceReadQuery>.Success(
            new PublicationSourceReadQuery(take, cursor, filters, deterministicSorts));
    }

    private static bool IsValidFilterValue(
        PublicationColumn column,
        PublicationFilterOperator operation,
        string? value)
    {
        if (operation is PublicationFilterOperator.IsNull or PublicationFilterOperator.IsNotNull)
        {
            return value is null;
        }

        if (value is null)
        {
            return false;
        }

        if (operation is PublicationFilterOperator.StartsWith or PublicationFilterOperator.Contains)
        {
            return column.DataType == PublicationDataType.String &&
                (column.MaximumLength is null || value.Length <= column.MaximumLength);
        }

        const NumberStyles integerStyle = NumberStyles.Integer;
        var culture = CultureInfo.InvariantCulture;
        return column.DataType switch
        {
            PublicationDataType.Boolean => bool.TryParse(value, out _),
            PublicationDataType.Byte => byte.TryParse(value, integerStyle, culture, out _),
            PublicationDataType.Int16 => short.TryParse(value, integerStyle, culture, out _),
            PublicationDataType.Int32 => int.TryParse(value, integerStyle, culture, out _),
            PublicationDataType.Int64 => long.TryParse(value, integerStyle, culture, out _),
            PublicationDataType.Decimal => decimal.TryParse(
                value,
                NumberStyles.Number,
                culture,
                out _),
            PublicationDataType.Single => float.TryParse(
                value,
                NumberStyles.Float,
                culture,
                out var single) && float.IsFinite(single),
            PublicationDataType.Double => double.TryParse(
                value,
                NumberStyles.Float,
                culture,
                out var number) && double.IsFinite(number),
            PublicationDataType.Date => DateOnly.TryParse(
                value,
                culture,
                DateTimeStyles.None,
                out _),
            PublicationDataType.DateTime => DateTime.TryParse(
                value,
                culture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out _),
            PublicationDataType.DateTimeOffset => DateTimeOffset.TryParse(
                value,
                culture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out _),
            PublicationDataType.Time => TimeSpan.TryParse(value, culture, out _),
            PublicationDataType.Guid => Guid.TryParse(value, out _),
            PublicationDataType.String => column.MaximumLength is null || value.Length <= column.MaximumLength,
            PublicationDataType.Binary => IsBoundedBase64(value, column.MaximumLength),
            _ => false,
        };
    }

    private static bool IsBoundedBase64(string value, int? maximumLength)
    {
        try
        {
            var length = Convert.FromBase64String(value).Length;
            return maximumLength is null || length <= maximumLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static PublicationResult<TTarget> CopyFailure<TSource, TTarget>(
        PublicationResult<TSource> result)
    {
        var problem = result.Problem ?? throw new InvalidOperationException("A failed result requires a problem.");
        return PublicationResult<TTarget>.Failure(problem.Kind, problem.Code, problem.Message);
    }

    private static PublicationResult<T> MapSourceFailure<T>(PublicationSourceReadStatus status) => status switch
    {
        PublicationSourceReadStatus.InvalidCursor => PublicationResultFactory.Validation<T>(
            "publication.cursor_invalid",
            "The paging cursor is invalid or no longer applies to this publication version."),
        PublicationSourceReadStatus.SchemaChanged => PublicationResultFactory.Conflict<T>(
            "publication.source_schema_changed",
            "The source schema no longer matches the active publication version."),
        _ => PublicationResultFactory.Unavailable<T>(
            "publication.source_unavailable",
            "The publication source is temporarily unavailable."),
    };
}
