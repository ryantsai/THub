using System.Collections.ObjectModel;

namespace THub.Domain.Publications;

public sealed class PublicationForeignKey
{
    public const int MaximumSearchColumns = 8;

    private readonly List<string> _searchColumns = [];

    private PublicationForeignKey()
    {
    }

    public PublicationForeignKey(
        string constraintName,
        int ordinal,
        int columnCount,
        string referencedSchema,
        string referencedObject,
        string referencedColumn,
        string displayColumn,
        IEnumerable<string> searchColumns,
        PublicationLookupMode lookupMode)
    {
        ConstraintName = PublicationGuard.Require(constraintName, nameof(constraintName), 128);
        if (columnCount < 1 || columnCount > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(columnCount),
                columnCount,
                "Foreign keys must contain between 1 and 16 columns.");
        }

        if (ordinal < 0 || ordinal >= columnCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                ordinal,
                "Foreign-key ordinal must be within its column count.");
        }

        Ordinal = ordinal;
        ColumnCount = columnCount;
        ReferencedSchema = PublicationGuard.Require(referencedSchema, nameof(referencedSchema), 128);
        ReferencedObject = PublicationGuard.Require(referencedObject, nameof(referencedObject), 128);
        ReferencedColumn = PublicationGuard.Require(referencedColumn, nameof(referencedColumn), 128);
        DisplayColumn = PublicationGuard.Require(displayColumn, nameof(displayColumn), 128);
        LookupMode = PublicationGuard.RequireDefined(lookupMode, nameof(lookupMode));

        ArgumentNullException.ThrowIfNull(searchColumns);
        foreach (var searchColumn in searchColumns)
        {
            var normalized = PublicationGuard.Require(searchColumn, nameof(searchColumns), 128);
            if (_searchColumns.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Foreign-key search columns must be unique.", nameof(searchColumns));
            }

            _searchColumns.Add(normalized);
        }

        if (_searchColumns.Count == 0)
        {
            throw new ArgumentException(
                "At least one explicitly approved foreign-key search column is required.",
                nameof(searchColumns));
        }

        if (_searchColumns.Count > MaximumSearchColumns)
        {
            throw new ArgumentOutOfRangeException(
                nameof(searchColumns),
                $"A foreign-key lookup can expose at most {MaximumSearchColumns} search columns.");
        }

        if (ColumnCount > 1 && LookupMode == PublicationLookupMode.ListValidation)
        {
            throw new ArgumentException(
                "Composite foreign keys require a logical dropdown or server-filtered editor.",
                nameof(lookupMode));
        }
    }

    public string ConstraintName { get; private set; } = string.Empty;

    public int Ordinal { get; private set; }

    public int ColumnCount { get; private set; }

    public bool IsComposite => ColumnCount > 1;

    public string ReferencedSchema { get; private set; } = string.Empty;

    public string ReferencedObject { get; private set; } = string.Empty;

    public string ReferencedColumn { get; private set; } = string.Empty;

    public string DisplayColumn { get; private set; } = string.Empty;

    public bool StoresDisplayValue => string.Equals(
        ReferencedColumn,
        DisplayColumn,
        StringComparison.OrdinalIgnoreCase);

    public ReadOnlyCollection<string> SearchColumns => _searchColumns.AsReadOnly();

    public PublicationLookupMode LookupMode { get; private set; }
}
