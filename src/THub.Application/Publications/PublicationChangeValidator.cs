using System.Globalization;
using System.Text.Json;
using THub.Domain.Publications;

namespace THub.Application.Publications;

internal static class PublicationChangeValidator
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    public static PublicationProblem? Validate(
        PublicationVersion version,
        IReadOnlyList<StagePublicationChangeCommand> changes)
    {
        if (changes.Count is < 1 or > PublicationChangeSet.MaximumChanges)
        {
            return Validation(
                "publication.change_count_invalid",
                $"A change set must contain between 1 and {PublicationChangeSet.MaximumChanges} changes.");
        }

        var columns = version.Columns.ToDictionary(
            column => column.PublicAlias,
            StringComparer.OrdinalIgnoreCase);
        var keyAliases = version.Columns
            .Where(column => column.IsKey)
            .Select(column => column.PublicAlias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var insertableAliases = version.Columns
            .Where(PublicationColumnMutationPolicy.CanSupplyOnInsert)
            .Select(column => column.PublicAlias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updateableAliases = version.Columns
            .Where(PublicationColumnMutationPolicy.CanSetOnUpdate)
            .Select(column => column.PublicAlias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foreignKeyGroups = GetForeignKeyGroups(version);
        var foreignKeyPolicyProblem = ValidateForeignKeyPolicy(foreignKeyGroups);
        if (foreignKeyPolicyProblem is not null)
        {
            return foreignKeyPolicyProblem;
        }

        if (version.ConcurrencyMode == PublicationConcurrencyMode.ReadOnly)
        {
            return Validation(
                "publication.editor_read_only",
                "The active editor version does not allow staged changes.");
        }

        foreach (var change in changes)
        {
            if (change is null || !Enum.IsDefined(change.Operation))
            {
                return Validation(
                    "publication.change_operation_invalid",
                    "Every staged change must use a supported operation.");
            }

            var key = ParseObject(
                change.KeyJson,
                "key",
                PublicationChange.MaximumKeyJsonLength,
                allowNull: change.Operation == PublicationChangeOperation.Insert);
            if (key.Problem is not null)
            {
                return key.Problem;
            }

            var before = ParseObject(
                change.BeforeJson,
                "before",
                PublicationChange.MaximumRowJsonLength,
                allowNull: change.Operation == PublicationChangeOperation.Insert);
            if (before.Problem is not null)
            {
                return before.Problem;
            }

            var after = ParseObject(
                change.AfterJson,
                "after",
                PublicationChange.MaximumRowJsonLength,
                allowNull: change.Operation == PublicationChangeOperation.Delete);
            if (after.Problem is not null)
            {
                return after.Problem;
            }

            if (change.Operation != PublicationChangeOperation.Insert &&
                !SetEquals(key.Properties, keyAliases))
            {
                return Validation(
                    "publication.change_key_invalid",
                    "Update and delete keys must contain exactly the active version's key columns.");
            }

            var valueProblem = ValidateValues(key.Values, columns);
            if (valueProblem is not null)
            {
                return valueProblem;
            }

            if (change.Operation != PublicationChangeOperation.Insert)
            {
                if (!before.Properties.All(columns.ContainsKey) ||
                    !keyAliases.IsSubsetOf(before.Properties))
                {
                    return Validation(
                        "publication.change_before_invalid",
                        "Before values must use readable publication aliases and include every key column.");
                }

                valueProblem = ValidateValues(before.Values, columns);
                if (valueProblem is not null)
                {
                    return valueProblem;
                }

                var requiredOriginals = new HashSet<string>(keyAliases, StringComparer.OrdinalIgnoreCase);
                if (version.ConcurrencyMode == PublicationConcurrencyMode.RowVersion)
                {
                    requiredOriginals.UnionWith(version.Columns
                        .Where(column => column.IsConcurrencyToken)
                        .Select(column => column.PublicAlias));
                }
                else if (change.Operation == PublicationChangeOperation.Delete)
                {
                    requiredOriginals.UnionWith(updateableAliases);
                }

                if (!requiredOriginals.IsSubsetOf(before.Properties))
                {
                    return Validation(
                        "publication.change_concurrency_values_missing",
                        "Before values do not contain the required key and concurrency values.");
                }
            }

            if (change.Operation != PublicationChangeOperation.Delete)
            {
                var allowedAfterAliases = change.Operation == PublicationChangeOperation.Insert
                    ? insertableAliases
                    : updateableAliases;
                if (after.Properties.Count == 0 || !after.Properties.IsSubsetOf(allowedAfterAliases))
                {
                    return Validation(
                        "publication.change_after_invalid",
                        change.Operation == PublicationChangeOperation.Insert
                            ? "Insert values must contain only insertable publication aliases."
                            : "Update values must contain only mutable non-key publication aliases.");
                }

                valueProblem = ValidateValues(after.Values, columns);
                if (valueProblem is not null)
                {
                    return valueProblem;
                }

                var foreignKeyProblem = ValidateForeignKeyAtomicity(after, foreignKeyGroups);
                if (foreignKeyProblem is not null)
                {
                    return foreignKeyProblem;
                }

                if (change.Operation == PublicationChangeOperation.Insert)
                {
                    var required = version.Columns
                        .Where(column =>
                            PublicationColumnMutationPolicy.CanSupplyOnInsert(column) &&
                            !column.IsNullable)
                        .Select(column => column.PublicAlias)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!required.IsSubsetOf(after.Properties))
                    {
                        return Validation(
                            "publication.change_required_values_missing",
                            "Insert values do not contain every required insertable column.");
                    }
                }
                else if (version.ConcurrencyMode == PublicationConcurrencyMode.OriginalValues &&
                         !after.Properties.IsSubsetOf(before.Properties))
                {
                    return Validation(
                        "publication.change_original_values_missing",
                        "Original-value concurrency requires a before value for every updated column.");
                }
            }
        }

        return null;
    }

    public static PublicationForeignKeyTupleExtraction ExtractForeignKeyTuples(
        PublicationVersion version,
        IReadOnlyList<StagePublicationChangeCommand> changes)
    {
        var groups = GetForeignKeyGroups(version);
        var tuples = new List<PublicationForeignKeyTuple>();
        foreach (var change in changes.Where(change =>
                     change.Operation is PublicationChangeOperation.Insert or PublicationChangeOperation.Update))
        {
            var after = ParseObject(
                change.AfterJson,
                "after",
                PublicationChange.MaximumRowJsonLength,
                allowNull: false);
            var values = after.Values.ToDictionary(
                value => value.Alias,
                StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                if (!group.Columns.All(column => values.ContainsKey(column.PublicAlias)))
                {
                    continue;
                }

                var keyValues = group.Columns.ToDictionary(
                    column => column.PublicAlias,
                    column => ToSourceValue(values[column.PublicAlias].Value),
                    StringComparer.OrdinalIgnoreCase);
                if (keyValues.Values.All(value => value is null))
                {
                    continue;
                }

                if (tuples.Count == PublicationDataService.MaximumForeignKeyTuples)
                {
                    return new PublicationForeignKeyTupleExtraction(
                        [],
                        Validation(
                            "publication.foreign_key_validation_bounds_exceeded",
                            $"A change set can validate at most {PublicationDataService.MaximumForeignKeyTuples} foreign-key tuples."));
                }

                tuples.Add(new PublicationForeignKeyTuple(
                    tuples.Count,
                    group.ConstraintName,
                    keyValues));
            }
        }

        return new PublicationForeignKeyTupleExtraction(tuples, null);
    }

    private static PublicationProblem? ValidateForeignKeyAtomicity(
        ParsedObject after,
        IReadOnlyList<ForeignKeyGroup> groups)
    {
        var values = after.Values.ToDictionary(value => value.Alias, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var supplied = group.Columns.Count(column => after.Properties.Contains(column.PublicAlias));
            if (supplied == 0)
            {
                continue;
            }

            if (supplied != group.Columns.Count)
            {
                return Validation(
                    "publication.foreign_key_partial_update",
                    $"Foreign key '{group.ConstraintName}' must be supplied as one complete tuple.");
            }

            var nullCount = group.Columns.Count(column =>
                values[column.PublicAlias].Value.ValueKind == JsonValueKind.Null);
            if (nullCount > 0 && nullCount != group.Columns.Count)
            {
                return Validation(
                    "publication.foreign_key_partial_null",
                    $"Foreign key '{group.ConstraintName}' must be entirely null or entirely populated.");
            }
        }

        return null;
    }

    private static PublicationProblem? ValidateForeignKeyPolicy(
        IReadOnlyList<ForeignKeyGroup> groups)
    {
        foreach (var group in groups)
        {
            var foreignKey = group.Columns[0].ForeignKey!;
            if (foreignKey.SearchColumns.Count == 0 ||
                group.Columns.Count != foreignKey.ColumnCount ||
                group.Columns.Any(column => !column.IsReadable) ||
                group.Columns.Any(column =>
                    column.ForeignKey is null ||
                    !string.Equals(column.ForeignKey.ReferencedSchema, foreignKey.ReferencedSchema, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(column.ForeignKey.ReferencedObject, foreignKey.ReferencedObject, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(column.ForeignKey.DisplayColumn, foreignKey.DisplayColumn, StringComparison.OrdinalIgnoreCase) ||
                    !column.ForeignKey.SearchColumns.SequenceEqual(foreignKey.SearchColumns, StringComparer.OrdinalIgnoreCase) ||
                    column.ForeignKey.LookupMode != foreignKey.LookupMode) ||
                (foreignKey.IsComposite &&
                 (group.Columns.Select(column => column.IsWritable).Distinct().Count() != 1 ||
                  group.Columns.Select(column => column.IsNullable).Distinct().Count() != 1)))
            {
                return Validation(
                    "publication.foreign_key_policy_invalid",
                    $"Foreign key '{group.ConstraintName}' is not exposed as one atomic readable tuple.");
            }
        }

        return null;
    }

    private static IReadOnlyList<ForeignKeyGroup> GetForeignKeyGroups(PublicationVersion version) =>
        version.Columns
            .Where(column => column.ForeignKey is not null)
            .GroupBy(column => column.ForeignKey!.ConstraintName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ForeignKeyGroup(
                group.Key,
                group.OrderBy(column => column.ForeignKey!.Ordinal).ToArray()))
            .ToArray();

    private static object? ToSourceValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidOperationException("Validated foreign-key values must be scalar."),
    };

    private static ParsedObject ParseObject(
        string? json,
        string name,
        int maximumLength,
        bool allowNull)
    {
        if (json is null)
        {
            return allowNull
                ? ParsedObject.Empty
                : new ParsedObject([], [], Validation(
                    $"publication.change_{name}_required",
                    $"The {name} JSON object is required for this operation."));
        }

        if (json.Length > maximumLength)
        {
            return new ParsedObject([], [], Validation(
                $"publication.change_{name}_too_large",
                $"The {name} JSON object exceeds its maximum supported size."));
        }

        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ParsedObject([], [], Validation(
                    $"publication.change_{name}_invalid",
                    $"The {name} value must be a JSON object."));
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var values = new List<ParsedValue>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    return new ParsedObject([], [], Validation(
                        $"publication.change_{name}_duplicate",
                        $"The {name} object contains duplicate aliases ignoring case."));
                }

                values.Add(new ParsedValue(property.Name, property.Value.Clone()));
            }

            return new ParsedObject(names, values, null);
        }
        catch (JsonException)
        {
            return new ParsedObject([], [], Validation(
                $"publication.change_{name}_invalid",
                $"The {name} value is not valid bounded JSON."));
        }
    }

    private static PublicationProblem? ValidateValues(
        IReadOnlyList<ParsedValue> values,
        IReadOnlyDictionary<string, PublicationColumn> columns)
    {
        foreach (var value in values)
        {
            if (!columns.TryGetValue(value.Alias, out var column) || !column.IsReadable)
            {
                return Validation(
                    "publication.change_alias_invalid",
                    "A staged value uses an alias that is not exposed by the active version.");
            }

            if (!IsCompatible(value.Value, column))
            {
                return Validation(
                    "publication.change_value_invalid",
                    $"The staged value for '{column.PublicAlias}' is incompatible with its publication type.");
            }
        }

        return null;
    }

    private static bool IsCompatible(JsonElement value, PublicationColumn column)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return column.IsNullable;
        }

        return column.DataType switch
        {
            PublicationDataType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            PublicationDataType.Byte => value.TryGetByte(out _),
            PublicationDataType.Int16 => value.TryGetInt16(out _),
            PublicationDataType.Int32 => value.TryGetInt32(out _),
            PublicationDataType.Int64 => value.TryGetInt64(out _),
            PublicationDataType.Decimal => value.TryGetDecimal(out _),
            PublicationDataType.Single => value.TryGetSingle(out var single) && float.IsFinite(single),
            PublicationDataType.Double => value.TryGetDouble(out var number) && double.IsFinite(number),
            PublicationDataType.Date => IsDate(value),
            PublicationDataType.DateTime => IsDateTime(value),
            PublicationDataType.DateTimeOffset => IsDateTimeOffset(value),
            PublicationDataType.Time => IsTime(value),
            PublicationDataType.Guid => IsGuid(value),
            PublicationDataType.String => IsBoundedString(value, column.MaximumLength),
            PublicationDataType.Binary => IsBase64(value, column.MaximumLength),
            _ => false,
        };
    }

    private static bool IsDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        DateOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsDateTime(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        DateTime.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
            out _);

    private static bool IsDateTimeOffset(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
            out _);

    private static bool IsTime(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        TimeOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsGuid(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _);

    private static bool IsBoundedString(JsonElement value, int? maximumLength) =>
        value.ValueKind == JsonValueKind.String &&
        (maximumLength is null || value.GetString()!.Length <= maximumLength);

    private static bool IsBase64(JsonElement value, int? maximumLength)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var encoded = value.GetString()!;
        try
        {
            var byteCount = Convert.FromBase64String(encoded).Length;
            return maximumLength is null || byteCount <= maximumLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool SetEquals(IReadOnlySet<string> left, IReadOnlySet<string> right) =>
        left.Count == right.Count && left.All(right.Contains);

    private static PublicationProblem Validation(string code, string message) =>
        new(PublicationProblemKind.Validation, code, message);

    private sealed record ParsedValue(string Alias, JsonElement Value);

    private sealed record ParsedObject(
        HashSet<string> Properties,
        IReadOnlyList<ParsedValue> Values,
        PublicationProblem? Problem)
    {
        public static ParsedObject Empty { get; } = new([], [], null);
    }

    private sealed record ForeignKeyGroup(
        string ConstraintName,
        IReadOnlyList<PublicationColumn> Columns);
}

internal sealed record PublicationForeignKeyTupleExtraction(
    IReadOnlyList<PublicationForeignKeyTuple> Tuples,
    PublicationProblem? Problem);
