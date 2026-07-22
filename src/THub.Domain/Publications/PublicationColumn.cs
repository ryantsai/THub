namespace THub.Domain.Publications;

public sealed class PublicationColumn
{
    public const int MaximumAliasLength = 100;
    public const int MaximumDeclaredValueLength = 1_000_000;

    private PublicationColumn()
    {
    }

    public PublicationColumn(
        Guid id,
        Guid publicationVersionId,
        int ordinal,
        string sourceName,
        string publicAlias,
        PublicationDataType dataType,
        string sourceTypeName,
        bool isNullable,
        bool isReadable,
        bool isFilterable,
        bool isSortable,
        bool isWritable,
        bool isKey = false,
        int? keyOrdinal = null,
        bool isConcurrencyToken = false,
        bool isGenerated = false,
        int? maximumLength = null,
        byte? numericPrecision = null,
        byte? numericScale = null,
        PublicationForeignKey? foreignKey = null)
    {
        Id = PublicationGuard.RequireId(id, nameof(id));
        PublicationVersionId = PublicationGuard.RequireId(publicationVersionId, nameof(publicationVersionId));
        if (ordinal < 0 || ordinal >= 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Column ordinal must be between 0 and 1023.");
        }

        Ordinal = ordinal;
        SourceName = PublicationGuard.Require(sourceName, nameof(sourceName), 128);
        PublicAlias = RequireAlias(publicAlias);
        DataType = PublicationGuard.RequireDefined(dataType, nameof(dataType));
        SourceTypeName = PublicationGuard.Require(sourceTypeName, nameof(sourceTypeName), 128);
        IsNullable = isNullable;
        IsReadable = isReadable;
        IsFilterable = isFilterable;
        IsSortable = isSortable;
        IsWritable = isWritable;
        IsKey = isKey;
        IsConcurrencyToken = isConcurrencyToken;
        IsGenerated = isGenerated;
        ForeignKey = foreignKey;

        if ((IsFilterable || IsSortable || IsWritable || IsKey) && !IsReadable)
        {
            throw new ArgumentException(
                "Filterable, sortable, writable, and key columns must also be readable.",
                nameof(isReadable));
        }

        if (IsKey)
        {
            if (IsNullable)
            {
                throw new ArgumentException("Key columns cannot be nullable.", nameof(isNullable));
            }

            if (!IsSortable)
            {
                throw new ArgumentException("Key columns must be sortable for deterministic paging.", nameof(isSortable));
            }

            if (keyOrdinal is null or < 0 or >= 16)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keyOrdinal),
                    keyOrdinal,
                    "Key ordinal must be between 0 and 15 for a key column.");
            }

            if (IsWritable)
            {
                throw new ArgumentException("Key columns cannot be writable.", nameof(isWritable));
            }

            KeyOrdinal = keyOrdinal;
        }
        else if (keyOrdinal is not null)
        {
            throw new ArgumentException("A non-key column cannot have a key ordinal.", nameof(keyOrdinal));
        }

        if (IsWritable && (IsGenerated || IsConcurrencyToken))
        {
            throw new ArgumentException(
                "Generated and concurrency-token columns cannot be writable.",
                nameof(isWritable));
        }

        if (maximumLength is <= 0 or > MaximumDeclaredValueLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLength),
                maximumLength,
                $"Maximum length must be between 1 and {MaximumDeclaredValueLength} when supplied.");
        }

        MaximumLength = maximumLength;

        if (numericPrecision is 0 or > 38)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numericPrecision),
                numericPrecision,
                "Numeric precision must be between 1 and 38 when supplied.");
        }

        if (numericScale is not null && numericPrecision is null)
        {
            throw new ArgumentException("Numeric scale requires a numeric precision.", nameof(numericScale));
        }

        if (numericScale > numericPrecision)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numericScale),
                numericScale,
                "Numeric scale cannot exceed numeric precision.");
        }

        NumericPrecision = numericPrecision;
        NumericScale = numericScale;
    }

    public Guid Id { get; private set; }

    public Guid PublicationVersionId { get; private set; }

    public int Ordinal { get; private set; }

    public string SourceName { get; private set; } = string.Empty;

    public string PublicAlias { get; private set; } = string.Empty;

    public PublicationDataType DataType { get; private set; }

    public string SourceTypeName { get; private set; } = string.Empty;

    public bool IsNullable { get; private set; }

    public bool IsReadable { get; private set; }

    public bool IsFilterable { get; private set; }

    public bool IsSortable { get; private set; }

    public bool IsWritable { get; private set; }

    public bool IsKey { get; private set; }

    public int? KeyOrdinal { get; private set; }

    public bool IsConcurrencyToken { get; private set; }

    public bool IsGenerated { get; private set; }

    public int? MaximumLength { get; private set; }

    public byte? NumericPrecision { get; private set; }

    public byte? NumericScale { get; private set; }

    public PublicationForeignKey? ForeignKey { get; private set; }

    private static string RequireAlias(string alias)
    {
        var normalized = PublicationGuard.Require(alias, nameof(alias), MaximumAliasLength);
        if (!char.IsAsciiLetter(normalized[0]))
        {
            throw new ArgumentException("Public alias must start with an ASCII letter.", nameof(alias));
        }

        if (normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException(
                "Public alias may contain only ASCII letters, numbers, underscores, and hyphens.",
                nameof(alias));
        }

        return normalized;
    }
}
