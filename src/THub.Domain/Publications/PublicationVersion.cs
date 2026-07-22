using System.Collections.ObjectModel;

namespace THub.Domain.Publications;

public sealed class PublicationVersion
{
    private readonly List<PublicationColumn> _columns = [];

    private PublicationVersion()
    {
    }

    public PublicationVersion(
        Guid id,
        Guid publicationId,
        int versionNumber,
        Guid connectionId,
        string sourceSchema,
        string sourceObject,
        PublicationSourceObjectKind sourceObjectKind,
        string schemaFingerprint,
        PublicationConcurrencyMode concurrencyMode,
        PublicationVersionSettings settings,
        IEnumerable<PublicationColumn> columns,
        string createdBy,
        DateTimeOffset createdAtUtc)
    {
        Id = PublicationGuard.RequireId(id, nameof(id));
        PublicationId = PublicationGuard.RequireId(publicationId, nameof(publicationId));
        if (versionNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(versionNumber), "Version number must be positive.");
        }

        VersionNumber = versionNumber;
        ConnectionId = PublicationGuard.RequireId(connectionId, nameof(connectionId));
        SourceSchema = PublicationGuard.Require(sourceSchema, nameof(sourceSchema), 128);
        SourceObject = PublicationGuard.Require(sourceObject, nameof(sourceObject), 128);
        SourceObjectKind = PublicationGuard.RequireDefined(sourceObjectKind, nameof(sourceObjectKind));
        SchemaFingerprint = PublicationGuard.Require(schemaFingerprint, nameof(schemaFingerprint), 256);
        ConcurrencyMode = PublicationGuard.RequireDefined(concurrencyMode, nameof(concurrencyMode));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        CreatedBy = PublicationGuard.Require(createdBy, nameof(createdBy), Publication.MaximumIdentityLength);
        CreatedAtUtc = PublicationGuard.AsUtc(createdAtUtc);

        ArgumentNullException.ThrowIfNull(columns);
        _columns.AddRange(columns);
        ValidateColumns();
    }

    public Guid Id { get; private set; }

    public Guid PublicationId { get; private set; }

    public int VersionNumber { get; private set; }

    public Guid ConnectionId { get; private set; }

    public string SourceSchema { get; private set; } = string.Empty;

    public string SourceObject { get; private set; } = string.Empty;

    public PublicationSourceObjectKind SourceObjectKind { get; private set; }

    public string SchemaFingerprint { get; private set; } = string.Empty;

    public PublicationConcurrencyMode ConcurrencyMode { get; private set; }

    public PublicationVersionSettings Settings { get; private set; } = null!;

    public ReadOnlyCollection<PublicationColumn> Columns => _columns.AsReadOnly();

    public string CreatedBy { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    private void ValidateColumns()
    {
        if (_columns.Count == 0)
        {
            throw new ArgumentException("A publication version must contain at least one column.", "columns");
        }

        if (_columns.Any(column => column.PublicationVersionId != Id))
        {
            throw new ArgumentException("Every column must belong to this publication version.", "columns");
        }

        RequireUnique(_columns.Select(column => column.Ordinal), "Column ordinals must be unique.");
        RequireUnique(
            _columns.Select(column => column.SourceName),
            "Source column names must be unique ignoring case.");
        RequireUnique(
            _columns.Select(column => column.PublicAlias),
            "Public column aliases must be unique ignoring case.");

        if (!_columns.Any(column => column.IsReadable))
        {
            throw new ArgumentException("A publication version must expose at least one readable column.", "columns");
        }

        var keyColumns = _columns
            .Where(column => column.IsKey)
            .OrderBy(column => column.KeyOrdinal)
            .ToArray();
        if (keyColumns.Length == 0)
        {
            throw new ArgumentException("A deterministic key is required for bounded keyset paging.", "columns");
        }

        for (var expectedOrdinal = 0; expectedOrdinal < keyColumns.Length; expectedOrdinal++)
        {
            if (keyColumns[expectedOrdinal].KeyOrdinal != expectedOrdinal)
            {
                throw new ArgumentException("Key ordinals must be unique and contiguous from zero.", "columns");
            }
        }

        var writableColumns = _columns.Where(column => column.IsWritable).ToArray();
        if (SourceObjectKind == PublicationSourceObjectKind.View &&
            (writableColumns.Length > 0 || ConcurrencyMode != PublicationConcurrencyMode.ReadOnly))
        {
            throw new ArgumentException("View publications must be read-only.", "columns");
        }

        if (writableColumns.Length > 0 && ConcurrencyMode == PublicationConcurrencyMode.ReadOnly)
        {
            throw new ArgumentException("Writable columns require an explicit concurrency mode.", "columns");
        }

        var concurrencyColumns = _columns.Where(column => column.IsConcurrencyToken).ToArray();
        if (ConcurrencyMode == PublicationConcurrencyMode.RowVersion)
        {
            if (concurrencyColumns.Length != 1 ||
                concurrencyColumns[0].DataType != PublicationDataType.Binary ||
                !concurrencyColumns[0].IsGenerated)
            {
                throw new ArgumentException(
                    "Row-version concurrency requires exactly one generated binary concurrency column.",
                    "columns");
            }
        }
        else if (concurrencyColumns.Length > 0)
        {
            throw new ArgumentException(
                "Concurrency-token columns require row-version concurrency mode.",
                "columns");
        }

        ValidateForeignKeys();
    }

    private void ValidateForeignKeys()
    {
        foreach (var group in _columns
                     .Where(column => column.ForeignKey is not null)
                     .GroupBy(
                         column => column.ForeignKey!.ConstraintName,
                         StringComparer.OrdinalIgnoreCase))
        {
            var columns = group.OrderBy(column => column.ForeignKey!.Ordinal).ToArray();
            var first = columns[0].ForeignKey!;
            if (columns.Length != first.ColumnCount)
            {
                throw new ArgumentException(
                    "All columns of a declared foreign key must be included in the publication version.",
                    "columns");
            }

            for (var expectedOrdinal = 0; expectedOrdinal < columns.Length; expectedOrdinal++)
            {
                var foreignKey = columns[expectedOrdinal].ForeignKey!;
                if (foreignKey.Ordinal != expectedOrdinal ||
                    foreignKey.ColumnCount != first.ColumnCount ||
                    !string.Equals(foreignKey.ReferencedSchema, first.ReferencedSchema, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(foreignKey.ReferencedObject, first.ReferencedObject, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(foreignKey.DisplayColumn, first.DisplayColumn, StringComparison.OrdinalIgnoreCase) ||
                    !foreignKey.SearchColumns.SequenceEqual(first.SearchColumns, StringComparer.OrdinalIgnoreCase) ||
                    foreignKey.LookupMode != first.LookupMode)
                {
                    throw new ArgumentException(
                        "Columns in a foreign-key group must define one consistent logical lookup.",
                        "columns");
                }
            }

            if (columns.Any(column => !column.IsReadable))
            {
                throw new ArgumentException(
                    "Every component of an approved foreign-key lookup must be readable.",
                    "columns");
            }

            if (first.IsComposite &&
                (columns.Select(column => column.IsWritable).Distinct().Count() != 1 ||
                 columns.Select(column => column.IsNullable).Distinct().Count() != 1))
            {
                throw new ArgumentException(
                    "Composite foreign-key components must have identical writable and nullable policy.",
                    "columns");
            }
        }
    }

    private static void RequireUnique(IEnumerable<int> values, string message)
    {
        if (values.Distinct().Count() != values.Count())
        {
            throw new ArgumentException(message, "columns");
        }
    }

    private static void RequireUnique(IEnumerable<string> values, string message)
    {
        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count())
        {
            throw new ArgumentException(message, "columns");
        }
    }
}
