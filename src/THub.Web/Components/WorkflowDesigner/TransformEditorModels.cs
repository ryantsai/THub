using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using THub.Application.Execution;
using THub.Application.Workflows;
using THub.Domain.Workflows;

namespace THub.Web.Components.WorkflowDesigner;

public sealed record WorkflowInputSchemaModel(
    string NodeId,
    string DisplayName,
    TabularSchema? Schema,
    string? SchemaStatus = null);

internal abstract class TransformEditorSettings(WorkflowNodeKind kind)
{
    public WorkflowNodeKind Kind { get; } = kind;
}

internal sealed class EditorList<T>(Action onChanged) : Collection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }

    public void MarkPresent() => onChanged();

    protected override void InsertItem(int index, T item)
    {
        onChanged();
        base.InsertItem(index, item);
    }

    protected override void SetItem(int index, T item)
    {
        onChanged();
        base.SetItem(index, item);
    }

    protected override void RemoveItem(int index)
    {
        onChanged();
        base.RemoveItem(index);
    }

    protected override void ClearItems()
    {
        onChanged();
        base.ClearItems();
    }
}

internal sealed class SelectColumnsEditorSettings
    : TransformEditorSettings
{
    public SelectColumnsEditorSettings(bool includeDefaults = true)
        : base(WorkflowNodeKind.SelectColumns)
    {
        HasColumns = includeDefaults;
        Columns = new(() => HasColumns = true);
    }

    public bool HasColumns { get; private set; }

    public EditorList<string> Columns { get; }

    public void MarkColumnsPresent() => HasColumns = true;
}

internal sealed class FilterRowsEditorSettings
    : TransformEditorSettings
{
    public FilterRowsEditorSettings(bool includeDefaults = true)
        : base(WorkflowNodeKind.FilterRows)
    {
        HasConditions = includeDefaults;
        Conditions = new(() => HasConditions = true);
    }

    public bool HasConditions { get; private set; }

    public EditorList<FilterConditionEditorRow> Conditions { get; }

    public void MarkConditionsPresent() => HasConditions = true;
}

internal sealed class FilterConditionEditorRow(bool includeDefaults = true)
{
    private JsonElement? value;
    private string column = string.Empty;
    private string operation = "equals";

    public string UiId { get; } = TransformEditorRowId.Create();

    public string Column
    {
        get => column;
        set
        {
            column = value;
            HasColumn = true;
        }
    }

    public bool HasColumn { get; private set; } = includeDefaults;

    public string Operator
    {
        get => operation;
        set
        {
            operation = value;
            HasOperator = true;
        }
    }

    public bool HasOperator { get; private set; } = includeDefaults;

    public JsonElement? Value
    {
        get => value;
        set
        {
            this.value = value;
            HasValue = true;
        }
    }

    public bool HasValue { get; private set; }

    public void ClearValue()
    {
        value = null;
        HasValue = false;
    }
}

internal sealed class JoinEditorSettings
    : TransformEditorSettings
{
    private string leftNodeId = string.Empty;
    private string rightNodeId = string.Empty;
    private string joinType = "inner";
    private int maximumBufferedRows = 100_000;

    public JoinEditorSettings(bool includeDefaults = true)
        : base(WorkflowNodeKind.Join)
    {
        HasLeftNodeId = includeDefaults;
        HasRightNodeId = includeDefaults;
        HasLeftKeys = includeDefaults;
        HasRightKeys = includeDefaults;
        HasJoinType = includeDefaults;
        HasMaximumBufferedRows = includeDefaults;
        KeyPairs = new(MarkKeyPairsEdited);
    }

    public string LeftNodeId
    {
        get => leftNodeId;
        set
        {
            leftNodeId = value;
            HasLeftNodeId = true;
        }
    }

    public bool HasLeftNodeId { get; private set; }

    public string RightNodeId
    {
        get => rightNodeId;
        set
        {
            rightNodeId = value;
            HasRightNodeId = true;
        }
    }

    public bool HasRightNodeId { get; private set; }

    public EditorList<JoinKeyPairEditorRow> KeyPairs { get; }

    public bool HasLeftKeys { get; private set; }

    public bool HasRightKeys { get; private set; }

    public string JoinType
    {
        get => joinType;
        set
        {
            joinType = value;
            HasJoinType = true;
        }
    }

    public bool HasJoinType { get; private set; }

    public int MaximumBufferedRows
    {
        get => maximumBufferedRows;
        set
        {
            maximumBufferedRows = value;
            HasMaximumBufferedRows = true;
        }
    }

    public bool HasMaximumBufferedRows { get; private set; }

    public void MarkLeftKeysPresent() => HasLeftKeys = true;

    public void MarkRightKeysPresent() => HasRightKeys = true;

    public void MarkKeyPairsEdited()
    {
        HasLeftKeys = true;
        HasRightKeys = true;
    }

    public void SwapInputs()
    {
        (LeftNodeId, RightNodeId) = (RightNodeId, LeftNodeId);
        foreach (var pair in KeyPairs)
        {
            (pair.LeftKey, pair.RightKey) = (pair.RightKey, pair.LeftKey);
        }
    }
}

internal sealed class JoinKeyPairEditorRow
{
    public string UiId { get; } = TransformEditorRowId.Create();

    public string LeftKey { get; set; } = string.Empty;

    public string RightKey { get; set; } = string.Empty;
}

internal sealed class UnionRowsEditorSettings
    : TransformEditorSettings
{
    private string matchBy = "name";
    private string mode = "all";

    public UnionRowsEditorSettings(bool includeDefaults = true)
        : base(WorkflowNodeKind.UnionRows)
    {
        HasInputNodeIds = includeDefaults;
        HasMatchBy = includeDefaults;
        HasMode = includeDefaults;
        Inputs = new(() => HasInputNodeIds = true);
    }

    public EditorList<UnionInputEditorRow> Inputs { get; }

    public bool HasInputNodeIds { get; private set; }

    public string MatchBy
    {
        get => matchBy;
        set
        {
            matchBy = value;
            HasMatchBy = true;
        }
    }

    public bool HasMatchBy { get; private set; }

    public string Mode
    {
        get => mode;
        set
        {
            mode = value;
            HasMode = true;
        }
    }

    public bool HasMode { get; private set; }

    public void MarkInputNodeIdsPresent() => HasInputNodeIds = true;
}

internal sealed class UnionInputEditorRow
{
    public UnionInputEditorRow(string nodeId)
    {
        NodeId = nodeId;
    }

    public string UiId { get; } = TransformEditorRowId.Create();

    public string NodeId { get; set; }
}

internal sealed class DeriveColumnsEditorSettings
    : TransformEditorSettings
{
    public DeriveColumnsEditorSettings(bool includeDefaults = true)
        : base(WorkflowNodeKind.DeriveColumns)
    {
        HasColumns = includeDefaults;
        Columns = new(() => HasColumns = true);
    }

    public EditorList<DerivedColumnEditorRow> Columns { get; }

    public bool HasColumns { get; private set; }

    public void MarkColumnsPresent() => HasColumns = true;
}

internal sealed class DerivedColumnEditorRow(bool includeDefaults = true)
{
    private string name = string.Empty;
    private string dataType = nameof(TabularDataType.String);
    private bool isNullable = true;
    private string expression = string.Empty;

    public string UiId { get; } = TransformEditorRowId.Create();

    public string Name
    {
        get => name;
        set
        {
            name = value;
            HasName = true;
        }
    }

    public bool HasName { get; private set; } = includeDefaults;

    public string DataType
    {
        get => dataType;
        set
        {
            dataType = value;
            HasDataType = true;
        }
    }

    public bool HasDataType { get; private set; } = includeDefaults;

    public bool IsNullable
    {
        get => isNullable;
        set
        {
            isNullable = value;
            HasIsNullable = true;
        }
    }

    public bool HasIsNullable { get; private set; } = includeDefaults;

    public string Expression
    {
        get => expression;
        set
        {
            expression = value;
            HasExpression = true;
        }
    }

    public bool HasExpression { get; private set; } = includeDefaults;
}

internal sealed class AggregateRowsEditorSettings
    : TransformEditorSettings
{
    private int maximumGroups = 100_000;

    public AggregateRowsEditorSettings(bool includeDefaults = true)
        : base(WorkflowNodeKind.AggregateRows)
    {
        HasGroupBy = includeDefaults;
        HasAggregates = includeDefaults;
        HasMaximumGroups = includeDefaults;
        GroupBy = new(() => HasGroupBy = true);
        Aggregates = new(() => HasAggregates = true);
    }

    public EditorList<string> GroupBy { get; }

    public bool HasGroupBy { get; private set; }

    public EditorList<AggregateColumnEditorRow> Aggregates { get; }

    public bool HasAggregates { get; private set; }

    public int MaximumGroups
    {
        get => maximumGroups;
        set
        {
            maximumGroups = value;
            HasMaximumGroups = true;
        }
    }

    public bool HasMaximumGroups { get; private set; }

    public void MarkGroupByPresent() => HasGroupBy = true;

    public void MarkAggregatesPresent() => HasAggregates = true;
}

internal sealed class AggregateColumnEditorRow(bool includeDefaults = true)
{
    private string name = string.Empty;
    private string operation = "count";

    public string UiId { get; } = TransformEditorRowId.Create();

    public string Name
    {
        get => name;
        set
        {
            name = value;
            HasName = true;
        }
    }

    public bool HasName { get; private set; } = includeDefaults;

    public string Operation
    {
        get => operation;
        set
        {
            operation = value;
            HasOperation = true;
        }
    }

    public bool HasOperation { get; private set; } = includeDefaults;

    public string? Column { get; set; }
}

internal sealed class DistinctRowsEditorSettings
    : TransformEditorSettings
{
    private int maximumKeys = 100_000;

    public DistinctRowsEditorSettings(bool includeDefaults = true)
        : base(WorkflowNodeKind.DistinctRows)
    {
        HasMaximumKeys = includeDefaults;
        Columns = new(() => { });
    }

    public bool UseAllColumns { get; set; } = true;

    public EditorList<string> Columns { get; }

    public int MaximumKeys
    {
        get => maximumKeys;
        set
        {
            maximumKeys = value;
            HasMaximumKeys = true;
        }
    }

    public bool HasMaximumKeys { get; private set; }
}

internal sealed class SortRowsEditorSettings
    : TransformEditorSettings
{
    private int maximumBufferedRows = 100_000;

    public SortRowsEditorSettings(bool includeDefaults = true)
        : base(WorkflowNodeKind.SortRows)
    {
        HasKeys = includeDefaults;
        HasMaximumBufferedRows = includeDefaults;
        Keys = new(() => HasKeys = true);
    }

    public EditorList<SortKeyEditorRow> Keys { get; }

    public bool HasKeys { get; private set; }

    public int MaximumBufferedRows
    {
        get => maximumBufferedRows;
        set
        {
            maximumBufferedRows = value;
            HasMaximumBufferedRows = true;
        }
    }

    public bool HasMaximumBufferedRows { get; private set; }

    public void MarkKeysPresent() => HasKeys = true;
}

internal sealed class SortKeyEditorRow(bool includeDefaults = true)
{
    private string column = string.Empty;
    private string direction = "ascending";
    private string nulls = "last";

    public string UiId { get; } = TransformEditorRowId.Create();

    public string Column
    {
        get => column;
        set
        {
            column = value;
            HasColumn = true;
        }
    }

    public bool HasColumn { get; private set; } = includeDefaults;

    public string Direction
    {
        get => direction;
        set
        {
            direction = value;
            HasDirection = true;
        }
    }

    public bool HasDirection { get; private set; } = includeDefaults;

    public string Nulls
    {
        get => nulls;
        set
        {
            nulls = value;
            HasNulls = true;
        }
    }

    public bool HasNulls { get; private set; } = includeDefaults;
}

internal sealed record TransformSettingsParseResult(
    TransformEditorSettings? Settings,
    string? Error,
    string? ValidationError)
{
    public bool IsSuccess => Settings is not null;

    public bool IsPublishable => IsSuccess && ValidationError is null;

    public static TransformSettingsParseResult Draft(
        TransformEditorSettings settings,
        string? validationError) =>
        new(settings, null, validationError);

    public static TransformSettingsParseResult Failure(string error) =>
        new(null, error, null);
}

internal sealed record TransformSettingsSerializationResult(
    string? SettingsJson,
    string? Error,
    string? ValidationError)
{
    public bool IsSuccess => SettingsJson is not null;

    public bool IsPublishable => IsSuccess && ValidationError is null;

    public static TransformSettingsSerializationResult Candidate(
        string settingsJson,
        string? validationError) =>
        new(settingsJson, null, validationError);

    public static TransformSettingsSerializationResult Failure(string error) =>
        new(null, error, null);
}

internal static class TransformEditorSettingsCodec
{
    private static readonly WorkflowNodeSettingsValidator SettingsValidator = new();

    public static TransformSettingsParseResult Parse(
        WorkflowNodeKind kind,
        string settingsJson)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(settingsJson);
            if (settingsJson.Length > WorkflowGraphValidator.MaximumNodeSettingsCharacters)
            {
                return TransformSettingsParseResult.Failure(
                    $"Transform settings cannot exceed {WorkflowGraphValidator.MaximumNodeSettingsCharacters} characters.");
            }

            using var document = JsonDocument.Parse(settingsJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return TransformSettingsParseResult.Failure(
                    "Transform settings must be a JSON object.");
            }

            var settings = ReadStructuralDraft(kind, document.RootElement);
            return TransformSettingsParseResult.Draft(
                settings,
                GetValidationError(kind, settingsJson, settings));
        }
        catch (Exception exception) when (exception is JsonException
            or WorkflowNodeSettingsException
            or FormatException
            or InvalidOperationException
            or OverflowException
            or ArgumentException)
        {
            return TransformSettingsParseResult.Failure(exception.Message);
        }
    }

    public static TransformEditorSettings Create(WorkflowNodeKind kind) => kind switch
    {
        WorkflowNodeKind.SelectColumns => new SelectColumnsEditorSettings(),
        WorkflowNodeKind.FilterRows => new FilterRowsEditorSettings(),
        WorkflowNodeKind.Join => new JoinEditorSettings(),
        WorkflowNodeKind.UnionRows => new UnionRowsEditorSettings(),
        WorkflowNodeKind.DeriveColumns => new DeriveColumnsEditorSettings(),
        WorkflowNodeKind.AggregateRows => new AggregateRowsEditorSettings(),
        WorkflowNodeKind.DistinctRows => new DistinctRowsEditorSettings(),
        WorkflowNodeKind.SortRows => new SortRowsEditorSettings(),
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "The node kind does not have a transform editor."),
    };

    public static TransformSettingsSerializationResult Serialize(
        TransformEditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            ValidateModelStructureBounds(settings);
            var settingsJson = WriteCanonical(settings);
            var reload = Parse(settings.Kind, settingsJson);
            if (!reload.IsSuccess)
            {
                throw new FormatException(
                    reload.Error ?? "The candidate settings are not structurally reloadable.");
            }

            return TransformSettingsSerializationResult.Candidate(
                settingsJson,
                reload.ValidationError);
        }
        catch (Exception exception) when (exception is WorkflowNodeSettingsException
            or JsonException
            or FormatException
            or InvalidOperationException
            or OverflowException
            or ArgumentException)
        {
            return TransformSettingsSerializationResult.Failure(exception.Message);
        }
    }

    private static string WriteCanonical(TransformEditorSettings settings)
    {
        using var buffer = new BoundedSettingsBufferWriter(
            Encoding.UTF8.GetMaxByteCount(
                WorkflowGraphValidator.MaximumNodeSettingsCharacters));
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
        {
            writer.WriteStartObject();
            switch (settings)
            {
                case SelectColumnsEditorSettings select:
                    if (select.HasColumns)
                    {
                        WriteStringArray(writer, "columns", select.Columns);
                    }
                    break;
                case FilterRowsEditorSettings filter:
                    WriteFilter(writer, filter);
                    break;
                case JoinEditorSettings join:
                    WriteJoin(writer, join);
                    break;
                case UnionRowsEditorSettings union:
                    WriteUnion(writer, union);
                    break;
                case DeriveColumnsEditorSettings derive:
                    WriteDerive(writer, derive);
                    break;
                case AggregateRowsEditorSettings aggregate:
                    WriteAggregate(writer, aggregate);
                    break;
                case DistinctRowsEditorSettings distinct:
                    WriteDistinct(writer, distinct);
                    break;
                case SortRowsEditorSettings sort:
                    WriteSort(writer, sort);
                    break;
                default:
                    throw new ArgumentException(
                        "The settings model is not supported by the transform editor.",
                        nameof(settings));
            }

            writer.WriteEndObject();
        }

        var settingsJson = Encoding.UTF8.GetString(buffer.WrittenSpan);
        if (settingsJson.Length > WorkflowGraphValidator.MaximumNodeSettingsCharacters)
        {
            throw new InvalidOperationException(
                $"Transform settings cannot exceed {WorkflowGraphValidator.MaximumNodeSettingsCharacters} characters.");
        }

        return settingsJson;
    }

    private static string? GetValidationError(
        WorkflowNodeKind kind,
        string settingsJson,
        TransformEditorSettings settings)
    {
        var presenceError = GetRequiredPresenceError(settings);
        if (presenceError is not null)
        {
            return presenceError;
        }

        try
        {
            SettingsValidator.Parse(
                new WorkflowNode(
                    "transform-editor",
                    kind,
                    "Transform",
                    0,
                    0,
                    settingsJson));
            return null;
        }
        catch (WorkflowNodeSettingsException exception)
        {
            return exception.Message;
        }
    }

    private static string? GetRequiredPresenceError(
        TransformEditorSettings settings)
    {
        return settings switch
        {
            SelectColumnsEditorSettings select when !select.HasColumns =>
                "Required setting 'columns' is missing.",
            FilterRowsEditorSettings filter when !filter.HasConditions =>
                "Required setting 'conditions' is missing.",
            FilterRowsEditorSettings filter
                when filter.Conditions.Any(condition => !condition.HasColumn) =>
                "Required filter setting 'column' is missing.",
            FilterRowsEditorSettings filter
                when filter.Conditions.Any(condition => !condition.HasOperator) =>
                "Required filter setting 'operator' is missing.",
            JoinEditorSettings join when !join.HasLeftNodeId =>
                "Required join setting 'leftNodeId' is missing.",
            JoinEditorSettings join when !join.HasRightNodeId =>
                "Required join setting 'rightNodeId' is missing.",
            JoinEditorSettings join when !join.HasLeftKeys =>
                "Required join setting 'leftKeys' is missing.",
            JoinEditorSettings join when !join.HasRightKeys =>
                "Required join setting 'rightKeys' is missing.",
            JoinEditorSettings join when !join.HasMaximumBufferedRows =>
                "Required join setting 'maximumBufferedRows' is missing.",
            UnionRowsEditorSettings union when !union.HasInputNodeIds =>
                "Required union setting 'inputNodeIds' is missing.",
            UnionRowsEditorSettings union when !union.HasMatchBy =>
                "Required union setting 'matchBy' is missing.",
            UnionRowsEditorSettings union when !union.HasMode =>
                "Required union setting 'mode' is missing.",
            DeriveColumnsEditorSettings derive when !derive.HasColumns =>
                "Required setting 'columns' is missing.",
            DeriveColumnsEditorSettings derive
                when derive.Columns.Any(column => !column.HasName) =>
                "Required derived-column setting 'name' is missing.",
            DeriveColumnsEditorSettings derive
                when derive.Columns.Any(column => !column.HasDataType) =>
                "Required derived-column setting 'type' is missing.",
            DeriveColumnsEditorSettings derive
                when derive.Columns.Any(column => !column.HasIsNullable) =>
                "Required derived-column setting 'nullable' is missing.",
            DeriveColumnsEditorSettings derive
                when derive.Columns.Any(column => !column.HasExpression) =>
                "Required derived-column setting 'expression' is missing.",
            AggregateRowsEditorSettings aggregate when !aggregate.HasGroupBy =>
                "Required aggregate setting 'groupBy' is missing.",
            AggregateRowsEditorSettings aggregate when !aggregate.HasAggregates =>
                "Required aggregate setting 'aggregates' is missing.",
            AggregateRowsEditorSettings aggregate
                when aggregate.Aggregates.Any(column => !column.HasName) =>
                "Required aggregate-column setting 'name' is missing.",
            AggregateRowsEditorSettings aggregate
                when aggregate.Aggregates.Any(column => !column.HasOperation) =>
                "Required aggregate-column setting 'operation' is missing.",
            AggregateRowsEditorSettings aggregate when !aggregate.HasMaximumGroups =>
                "Required aggregate setting 'maximumGroups' is missing.",
            DistinctRowsEditorSettings distinct when !distinct.HasMaximumKeys =>
                "Required distinct setting 'maximumKeys' is missing.",
            SortRowsEditorSettings sort when !sort.HasKeys =>
                "Required sort setting 'keys' is missing.",
            SortRowsEditorSettings sort
                when sort.Keys.Any(key => !key.HasColumn) =>
                "Required sort-key setting 'column' is missing.",
            SortRowsEditorSettings sort
                when sort.Keys.Any(key => !key.HasDirection) =>
                "Required sort-key setting 'direction' is missing.",
            SortRowsEditorSettings sort
                when sort.Keys.Any(key => !key.HasNulls) =>
                "Required sort-key setting 'nulls' is missing.",
            SortRowsEditorSettings sort when !sort.HasMaximumBufferedRows =>
                "Required sort setting 'maximumBufferedRows' is missing.",
            _ => null,
        };
    }

    private static TransformEditorSettings ReadStructuralDraft(
        WorkflowNodeKind kind,
        JsonElement root) => kind switch
        {
            WorkflowNodeKind.SelectColumns => ReadSelectDraft(root),
            WorkflowNodeKind.FilterRows => ReadFilterDraft(root),
            WorkflowNodeKind.Join => ReadJoinDraft(root),
            WorkflowNodeKind.UnionRows => ReadUnionDraft(root),
            WorkflowNodeKind.DeriveColumns => ReadDeriveDraft(root),
            WorkflowNodeKind.AggregateRows => ReadAggregateDraft(root),
            WorkflowNodeKind.DistinctRows => ReadDistinctDraft(root),
            WorkflowNodeKind.SortRows => ReadSortDraft(root),
            _ => throw new FormatException(
                $"Node kind '{kind}' does not have a transform editor."),
        };

    private static SelectColumnsEditorSettings ReadSelectDraft(JsonElement root)
    {
        EnsureOnly(root, "columns");
        var settings = new SelectColumnsEditorSettings(includeDefaults: false);
        if (root.TryGetProperty("columns", out _))
        {
            settings.MarkColumnsPresent();
            settings.Columns.AddRange(ReadStringArray(root, "columns", 512));
        }

        return settings;
    }

    private static FilterRowsEditorSettings ReadFilterDraft(JsonElement root)
    {
        EnsureOnly(root, "conditions");
        var settings = new FilterRowsEditorSettings(includeDefaults: false);
        if (!root.TryGetProperty("conditions", out _))
        {
            return settings;
        }

        settings.MarkConditionsPresent();
        foreach (var item in ReadObjectArray(root, "conditions", 32))
        {
            EnsureOnly(item, "column", "operator", "value");
            var row = new FilterConditionEditorRow(includeDefaults: false);
            if (item.TryGetProperty("column", out _))
            {
                row.Column = ReadString(item, "column");
            }
            if (item.TryGetProperty("operator", out _))
            {
                row.Operator = ReadString(item, "operator");
            }
            if (item.TryGetProperty("value", out var value))
            {
                if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    throw new FormatException("Filter values must be JSON scalars.");
                }

                row.Value = value.Clone();
            }

            settings.Conditions.Add(row);
        }

        return settings;
    }

    private static JoinEditorSettings ReadJoinDraft(JsonElement root)
    {
        EnsureOnly(
            root,
            "leftNodeId",
            "rightNodeId",
            "leftKeys",
            "rightKeys",
            "type",
            "maximumBufferedRows");
        var hasLeftKeys = root.TryGetProperty("leftKeys", out _);
        var hasRightKeys = root.TryGetProperty("rightKeys", out _);
        var leftKeys = hasLeftKeys
            ? ReadStringArray(root, "leftKeys", 16)
            : [];
        var rightKeys = hasRightKeys
            ? ReadStringArray(root, "rightKeys", 16)
            : [];
        if (leftKeys.Count != rightKeys.Count)
        {
            throw new FormatException("Join key arrays must have the same length.");
        }

        var settings = new JoinEditorSettings(includeDefaults: false);
        if (root.TryGetProperty("leftNodeId", out _))
        {
            settings.LeftNodeId = ReadString(root, "leftNodeId");
        }
        if (root.TryGetProperty("rightNodeId", out _))
        {
            settings.RightNodeId = ReadString(root, "rightNodeId");
        }
        if (hasLeftKeys)
        {
            settings.MarkLeftKeysPresent();
        }
        if (hasRightKeys)
        {
            settings.MarkRightKeysPresent();
        }
        if (root.TryGetProperty("type", out _))
        {
            settings.JoinType = ReadString(root, "type");
        }
        if (root.TryGetProperty("maximumBufferedRows", out _))
        {
            settings.MaximumBufferedRows = ReadInt(root, "maximumBufferedRows", 100_000);
        }
        for (var index = 0; index < leftKeys.Count; index++)
        {
            settings.KeyPairs.Add(new()
            {
                LeftKey = leftKeys[index],
                RightKey = rightKeys[index],
            });
        }

        return settings;
    }

    private static UnionRowsEditorSettings ReadUnionDraft(JsonElement root)
    {
        EnsureOnly(root, "inputNodeIds", "matchBy", "mode");
        var settings = new UnionRowsEditorSettings(includeDefaults: false);
        if (root.TryGetProperty("inputNodeIds", out _))
        {
            settings.MarkInputNodeIdsPresent();
            settings.Inputs.AddRange(
                ReadStringArray(root, "inputNodeIds", 16)
                    .Select(nodeId => new UnionInputEditorRow(nodeId)));
        }
        if (root.TryGetProperty("matchBy", out _))
        {
            settings.MatchBy = ReadString(root, "matchBy");
        }
        if (root.TryGetProperty("mode", out _))
        {
            settings.Mode = ReadString(root, "mode");
        }

        return settings;
    }

    private static DeriveColumnsEditorSettings ReadDeriveDraft(JsonElement root)
    {
        EnsureOnly(root, "columns");
        var settings = new DeriveColumnsEditorSettings(includeDefaults: false);
        if (!root.TryGetProperty("columns", out _))
        {
            return settings;
        }

        settings.MarkColumnsPresent();
        foreach (var item in ReadObjectArray(root, "columns", 64))
        {
            EnsureOnly(item, "name", "type", "nullable", "expression");
            var column = new DerivedColumnEditorRow(includeDefaults: false);
            if (item.TryGetProperty("name", out _))
            {
                column.Name = ReadString(item, "name");
            }
            if (item.TryGetProperty("type", out _))
            {
                column.DataType = ReadDataType(item);
            }
            if (item.TryGetProperty("nullable", out _))
            {
                column.IsNullable = ReadBoolean(item, "nullable", true);
            }
            if (item.TryGetProperty("expression", out _))
            {
                column.Expression = ReadString(item, "expression");
            }

            settings.Columns.Add(column);
        }

        return settings;
    }

    private static AggregateRowsEditorSettings ReadAggregateDraft(JsonElement root)
    {
        EnsureOnly(root, "groupBy", "aggregates", "maximumGroups");
        var settings = new AggregateRowsEditorSettings(includeDefaults: false);
        if (root.TryGetProperty("groupBy", out _))
        {
            settings.MarkGroupByPresent();
            settings.GroupBy.AddRange(ReadStringArray(root, "groupBy", 16));
        }
        if (root.TryGetProperty("maximumGroups", out _))
        {
            settings.MaximumGroups = ReadInt(root, "maximumGroups", 100_000);
        }
        if (!root.TryGetProperty("aggregates", out _))
        {
            return settings;
        }

        settings.MarkAggregatesPresent();
        foreach (var item in ReadObjectArray(root, "aggregates", 64))
        {
            EnsureOnly(item, "name", "operation", "column");
            var aggregate = new AggregateColumnEditorRow(includeDefaults: false);
            if (item.TryGetProperty("name", out _))
            {
                aggregate.Name = ReadString(item, "name");
            }
            if (item.TryGetProperty("operation", out _))
            {
                aggregate.Operation = ReadString(item, "operation");
            }
            if (item.TryGetProperty("column", out _))
            {
                aggregate.Column = ReadOptionalString(item, "column");
            }

            settings.Aggregates.Add(aggregate);
        }

        return settings;
    }

    private static DistinctRowsEditorSettings ReadDistinctDraft(JsonElement root)
    {
        EnsureOnly(root, "columns", "maximumKeys");
        var columns = root.TryGetProperty("columns", out _)
            ? ReadStringArray(root, "columns", 64)
            : null;
        var useAllColumns = columns is null or { Count: 0 };
        var settings = new DistinctRowsEditorSettings(includeDefaults: false)
        {
            UseAllColumns = useAllColumns,
        };
        if (!useAllColumns)
        {
            settings.Columns.AddRange(columns!);
        }
        if (root.TryGetProperty("maximumKeys", out _))
        {
            settings.MaximumKeys = ReadInt(root, "maximumKeys", 100_000);
        }

        return settings;
    }

    private static SortRowsEditorSettings ReadSortDraft(JsonElement root)
    {
        EnsureOnly(root, "keys", "maximumBufferedRows");
        var settings = new SortRowsEditorSettings(includeDefaults: false);
        if (root.TryGetProperty("maximumBufferedRows", out _))
        {
            settings.MaximumBufferedRows = ReadInt(root, "maximumBufferedRows", 100_000);
        }
        if (!root.TryGetProperty("keys", out _))
        {
            return settings;
        }

        settings.MarkKeysPresent();
        foreach (var item in ReadObjectArray(root, "keys", 16))
        {
            EnsureOnly(item, "column", "direction", "nulls");
            var key = new SortKeyEditorRow(includeDefaults: false);
            if (item.TryGetProperty("column", out _))
            {
                key.Column = ReadString(item, "column");
            }
            if (item.TryGetProperty("direction", out _))
            {
                key.Direction = ReadString(item, "direction");
            }
            if (item.TryGetProperty("nulls", out _))
            {
                key.Nulls = ReadString(item, "nulls");
            }

            settings.Keys.Add(key);
        }

        return settings;
    }

    private static void ValidateModelStructureBounds(TransformEditorSettings settings)
    {
        switch (settings)
        {
            case SelectColumnsEditorSettings select:
                EnsureCount(select.Columns.Count, 512, "Selected columns");
                break;
            case FilterRowsEditorSettings filter:
                EnsureCount(filter.Conditions.Count, 32, "Filter conditions");
                break;
            case JoinEditorSettings join:
                EnsureCount(join.KeyPairs.Count, 16, "Join key pairs");
                break;
            case UnionRowsEditorSettings union:
                EnsureCount(union.Inputs.Count, 16, "Union inputs");
                break;
            case DeriveColumnsEditorSettings derive:
                EnsureCount(derive.Columns.Count, 64, "Derived columns");
                break;
            case AggregateRowsEditorSettings aggregate:
                EnsureCount(aggregate.GroupBy.Count, 16, "Grouping columns");
                EnsureCount(aggregate.Aggregates.Count, 64, "Aggregate outputs");
                break;
            case DistinctRowsEditorSettings distinct:
                EnsureCount(distinct.Columns.Count, 64, "Distinct columns");
                break;
            case SortRowsEditorSettings sort:
                EnsureCount(sort.Keys.Count, 16, "Sort keys");
                break;
            default:
                throw new ArgumentException(
                    "The settings model is not supported by the transform editor.",
                    nameof(settings));
        }
    }

    private static void EnsureCount(int count, int maximum, string label)
    {
        if (count > maximum)
        {
            throw new InvalidOperationException(
                $"{label} cannot contain more than {maximum} items.");
        }
    }

    private static IReadOnlyList<JsonElement> ReadObjectArray(
        JsonElement root,
        string propertyName,
        int maximumCount)
    {
        var array = ReadArray(root, propertyName, maximumCount);
        var values = new List<JsonElement>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException(
                    $"'{propertyName}' must contain JSON objects.");
            }

            values.Add(item);
        }

        return values;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement root,
        string propertyName,
        int maximumCount)
    {
        var array = ReadArray(root, propertyName, maximumCount);
        var values = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new FormatException(
                    $"'{propertyName}' must contain text values.");
            }

            values.Add(item.GetString() ?? string.Empty);
        }

        return values;
    }

    private static JsonElement ReadArray(
        JsonElement root,
        string propertyName,
        int maximumCount)
    {
        if (!root.TryGetProperty(propertyName, out var array))
        {
            using var empty = JsonDocument.Parse("[]");
            return empty.RootElement.Clone();
        }
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"'{propertyName}' must be an array.");
        }
        if (array.GetArrayLength() > maximumCount)
        {
            throw new FormatException(
                $"'{propertyName}' exceeds the safe editor limit of {maximumCount} items.");
        }

        return array;
    }

    private static string ReadString(
        JsonElement root,
        string propertyName,
        string defaultValue = "")
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"'{propertyName}' must be text.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"'{propertyName}' must be text.");
        }

        return value.GetString();
    }

    private static int ReadInt(
        JsonElement root,
        string propertyName,
        int defaultValue)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number))
        {
            throw new FormatException($"'{propertyName}' must be a whole number.");
        }

        return number;
    }

    private static bool ReadBoolean(
        JsonElement root,
        string propertyName,
        bool defaultValue)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FormatException($"'{propertyName}' must be true or false.");
        }

        return value.GetBoolean();
    }

    private static string ReadDataType(JsonElement root)
    {
        var value = ReadString(root, "type", nameof(TabularDataType.String));
        if (Enum.TryParse<TabularDataType>(value, ignoreCase: true, out var dataType)
            && Enum.IsDefined(dataType)
            && !int.TryParse(value, out _))
        {
            return dataType.ToString();
        }

        return value;
    }

    private static void EnsureOnly(JsonElement root, params string[] allowedProperties)
    {
        var allowed = allowedProperties.ToHashSet(StringComparer.Ordinal);
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!encountered.Add(property.Name))
            {
                throw new FormatException($"Setting '{property.Name}' is duplicated.");
            }
            if (!allowed.Contains(property.Name))
            {
                throw new FormatException(
                    $"Setting '{property.Name}' is not supported by this editor.");
            }
        }
    }

    private static void WriteFilter(Utf8JsonWriter writer, FilterRowsEditorSettings settings)
    {
        if (!settings.HasConditions)
        {
            return;
        }

        writer.WriteStartArray("conditions");
        foreach (var condition in settings.Conditions)
        {
            writer.WriteStartObject();
            if (condition.HasColumn)
            {
                writer.WriteString("column", condition.Column);
            }
            if (condition.HasOperator)
            {
                writer.WriteString("operator", condition.Operator);
            }
            if ((!condition.HasOperator
                    || condition.Operator is not ("isNull" or "isNotNull"))
                && condition.HasValue)
            {
                writer.WritePropertyName("value");
                if (condition.Value is { } value)
                {
                    value.WriteTo(writer);
                }
                else
                {
                    writer.WriteNullValue();
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteJoin(Utf8JsonWriter writer, JoinEditorSettings settings)
    {
        if (settings.HasLeftNodeId)
        {
            writer.WriteString("leftNodeId", settings.LeftNodeId);
        }
        if (settings.HasRightNodeId)
        {
            writer.WriteString("rightNodeId", settings.RightNodeId);
        }
        if (settings.HasLeftKeys)
        {
            WriteStringArray(
                writer,
                "leftKeys",
                settings.KeyPairs.Select(pair => pair.LeftKey));
        }
        if (settings.HasRightKeys)
        {
            WriteStringArray(
                writer,
                "rightKeys",
                settings.KeyPairs.Select(pair => pair.RightKey));
        }
        if (settings.HasJoinType)
        {
            writer.WriteString("type", settings.JoinType);
        }
        if (settings.HasMaximumBufferedRows)
        {
            writer.WriteNumber("maximumBufferedRows", settings.MaximumBufferedRows);
        }
    }

    private static void WriteUnion(Utf8JsonWriter writer, UnionRowsEditorSettings settings)
    {
        if (settings.HasInputNodeIds)
        {
            WriteStringArray(
                writer,
                "inputNodeIds",
                settings.Inputs.Select(input => input.NodeId));
        }
        if (settings.HasMatchBy)
        {
            writer.WriteString("matchBy", settings.MatchBy);
        }
        if (settings.HasMode)
        {
            writer.WriteString("mode", settings.Mode);
        }
    }

    private static void WriteDerive(Utf8JsonWriter writer, DeriveColumnsEditorSettings settings)
    {
        if (!settings.HasColumns)
        {
            return;
        }

        writer.WriteStartArray("columns");
        foreach (var column in settings.Columns)
        {
            writer.WriteStartObject();
            if (column.HasName)
            {
                writer.WriteString("name", column.Name);
            }
            if (column.HasDataType)
            {
                writer.WriteString("type", WriteDataType(column.DataType));
            }
            if (column.HasIsNullable)
            {
                writer.WriteBoolean("nullable", column.IsNullable);
            }
            if (column.HasExpression)
            {
                writer.WriteString("expression", column.Expression);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteAggregate(
        Utf8JsonWriter writer,
        AggregateRowsEditorSettings settings)
    {
        if (settings.HasGroupBy)
        {
            WriteStringArray(writer, "groupBy", settings.GroupBy);
        }
        if (settings.HasAggregates)
        {
            writer.WriteStartArray("aggregates");
            foreach (var aggregate in settings.Aggregates)
            {
                writer.WriteStartObject();
                if (aggregate.HasName)
                {
                    writer.WriteString("name", aggregate.Name);
                }
                if (aggregate.HasOperation)
                {
                    writer.WriteString("operation", aggregate.Operation);
                }
                if (aggregate.Column is not null)
                {
                    writer.WriteString("column", aggregate.Column);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        if (settings.HasMaximumGroups)
        {
            writer.WriteNumber("maximumGroups", settings.MaximumGroups);
        }
    }

    private static void WriteDistinct(
        Utf8JsonWriter writer,
        DistinctRowsEditorSettings settings)
    {
        if (!settings.UseAllColumns)
        {
            WriteStringArray(writer, "columns", settings.Columns);
        }

        if (settings.HasMaximumKeys)
        {
            writer.WriteNumber("maximumKeys", settings.MaximumKeys);
        }
    }

    private static void WriteSort(Utf8JsonWriter writer, SortRowsEditorSettings settings)
    {
        if (settings.HasKeys)
        {
            writer.WriteStartArray("keys");
            foreach (var key in settings.Keys)
            {
                writer.WriteStartObject();
                if (key.HasColumn)
                {
                    writer.WriteString("column", key.Column);
                }
                if (key.HasDirection)
                {
                    writer.WriteString("direction", key.Direction);
                }
                if (key.HasNulls)
                {
                    writer.WriteString("nulls", key.Nulls);
                }
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        if (settings.HasMaximumBufferedRows)
        {
            writer.WriteNumber("maximumBufferedRows", settings.MaximumBufferedRows);
        }
    }

    private static void WriteStringArray(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string WriteDataType(string dataType)
    {
        if (Enum.TryParse<TabularDataType>(
                dataType,
                ignoreCase: true,
                out var parsed)
            && Enum.IsDefined(parsed)
            && !int.TryParse(dataType, out _))
        {
            return parsed.ToString();
        }

        return dataType;
    }

}

internal sealed class BoundedSettingsBufferWriter
    : IBufferWriter<byte>, IDisposable
{
    private const int InitialCapacity = 1_024;
    private readonly int maximumBytes;
    private byte[] buffer;
    private int writtenCount;
    private bool disposed;

    public BoundedSettingsBufferWriter(int maximumBytes)
    {
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        this.maximumBytes = maximumBytes;
        buffer = ArrayPool<byte>.Shared.Rent(
            Math.Min(InitialCapacity, maximumBytes));
    }

    public ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return buffer.AsSpan(0, writtenCount);
        }
    }

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (count < 0 || count > maximumBytes - writtenCount)
        {
            throw TooLarge();
        }

        writtenCount += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return buffer.AsMemory(
            writtenCount,
            Math.Min(buffer.Length, maximumBytes) - writtenCount);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return buffer.AsSpan(
            writtenCount,
            Math.Min(buffer.Length, maximumBytes) - writtenCount);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        buffer = [];
        writtenCount = 0;
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sizeHint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        }

        var requiredAdditionalBytes = Math.Max(sizeHint, 1);
        if (requiredAdditionalBytes > maximumBytes - writtenCount)
        {
            throw TooLarge();
        }
        if (requiredAdditionalBytes <= buffer.Length - writtenCount)
        {
            return;
        }

        var requiredCapacity = writtenCount + requiredAdditionalBytes;
        var doubledCapacity = Math.Min(
            (long)maximumBytes,
            (long)buffer.Length * 2);
        var nextCapacity = (int)Math.Max(requiredCapacity, doubledCapacity);
        var next = ArrayPool<byte>.Shared.Rent(nextCapacity);
        buffer.AsSpan(0, writtenCount).CopyTo(next);
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        buffer = next;
    }

    private static InvalidOperationException TooLarge() =>
        new(
            $"Transform settings cannot exceed {WorkflowGraphValidator.MaximumNodeSettingsCharacters} characters.");
}

internal enum InputSynchronizationOutcome
{
    Applied,
    CapacityExceeded,
}

internal sealed record JoinInputSynchronizationResult(
    InputSynchronizationOutcome Outcome,
    string LeftNodeId,
    string RightNodeId,
    int ProposedInputCount);

internal sealed record UnionInputSynchronizationItem(
    string UiId,
    string NodeId);

internal sealed record UnionInputSynchronizationResult(
    InputSynchronizationOutcome Outcome,
    IReadOnlyList<UnionInputSynchronizationItem> Inputs,
    int ProposedInputCount)
{
    public IReadOnlyList<string> InputNodeIds =>
        Array.AsReadOnly(Inputs.Select(input => input.NodeId).ToArray());
}

internal static class TransformInputSynchronizer
{
    private const int JoinCapacity = 2;
    private const int UnionCapacity = 16;

    public static JoinInputSynchronizationResult SynchronizeJoin(
        JoinEditorSettings settings,
        IReadOnlyList<string> incomingNodeIds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var incoming = DistinctNodeIds(incomingNodeIds);
        if (incoming.Count > JoinCapacity)
        {
            return new(
                InputSynchronizationOutcome.CapacityExceeded,
                settings.LeftNodeId,
                settings.RightNodeId,
                incoming.Count);
        }

        var ordered = SynchronizeOrder(
            [settings.LeftNodeId, settings.RightNodeId],
            incoming);
        return new(
            InputSynchronizationOutcome.Applied,
            ordered.ElementAtOrDefault(0) ?? string.Empty,
            ordered.ElementAtOrDefault(1) ?? string.Empty,
            incoming.Count);
    }

    public static UnionInputSynchronizationResult SynchronizeUnion(
        UnionRowsEditorSettings settings,
        IReadOnlyList<string> incomingNodeIds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var incoming = DistinctNodeIds(incomingNodeIds);
        if (incoming.Count > UnionCapacity)
        {
            return new(
                InputSynchronizationOutcome.CapacityExceeded,
                SnapshotInputs(settings.Inputs),
                incoming.Count);
        }

        return new(
            InputSynchronizationOutcome.Applied,
            SynchronizeUnionInputs(settings.Inputs, incoming),
            incoming.Count);
    }

    private static IReadOnlyList<UnionInputSynchronizationItem> SynchronizeUnionInputs(
        IReadOnlyList<UnionInputEditorRow> persistedInputs,
        IReadOnlyList<string> incomingNodeIds)
    {
        var incomingSet = incomingNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retainedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var synchronized = new List<UnionInputSynchronizationItem>();
        foreach (var input in persistedInputs)
        {
            if (!string.IsNullOrWhiteSpace(input.NodeId)
                && incomingSet.Contains(input.NodeId)
                && retainedIds.Add(input.NodeId))
            {
                synchronized.Add(new(input.UiId, input.NodeId));
            }
        }

        synchronized.AddRange(
            incomingNodeIds
                .Where(nodeId => !retainedIds.Contains(nodeId))
                .Select(nodeId => new UnionInputSynchronizationItem(
                    TransformEditorRowId.Create(),
                    nodeId)));
        return synchronized.AsReadOnly();
    }

    private static IReadOnlyList<UnionInputSynchronizationItem> SnapshotInputs(
        IReadOnlyList<UnionInputEditorRow> inputs) =>
        inputs
            .Select(input => new UnionInputSynchronizationItem(input.UiId, input.NodeId))
            .ToList()
            .AsReadOnly();

    private static IReadOnlyList<string> SynchronizeOrder(
        IReadOnlyList<string> persistedNodeIds,
        IReadOnlyList<string> incomingNodeIds)
    {
        var incomingSet = incomingNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = persistedNodeIds
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(incomingSet.Contains)
            .ToList();
        var retained = ordered.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ordered.AddRange(
            incomingNodeIds
                .Where(nodeId => !retained.Contains(nodeId)));
        return ordered.AsReadOnly();
    }

    private static IReadOnlyList<string> DistinctNodeIds(
        IReadOnlyList<string> incomingNodeIds)
    {
        ArgumentNullException.ThrowIfNull(incomingNodeIds);
        return incomingNodeIds
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal static class TransformEditorRowId
{
    public static string Create() => Guid.NewGuid().ToString("N");
}

internal sealed record FilterScalarDraftResult(
    JsonElement Value,
    string? ErrorCode)
{
    public bool IsValid => ErrorCode is null;
}

internal static class FilterScalarDraftParser
{
    public static FilterScalarDraftResult Revalidate(
        JsonElement value,
        TabularColumn? column)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return new(
                value.Clone(),
                column is { IsNullable: false }
                    ? "filter.value.required"
                    : null);
        }

        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
        return Parse(text, column);
    }

    public static FilterScalarDraftResult Parse(
        string text,
        TabularColumn? column)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (column is null || column.DataType == TabularDataType.String)
        {
            return Valid(JsonSerializer.SerializeToElement(text));
        }
        if (text.Length == 0)
        {
            return column.IsNullable
                ? Valid(JsonSerializer.SerializeToElement<object?>(null))
                : Invalid(text, "filter.value.required");
        }

        return column.DataType switch
        {
            TabularDataType.Boolean => bool.TryParse(text, out var value)
                ? Valid(JsonSerializer.SerializeToElement(value))
                : Invalid(text, "filter.value.boolean"),
            TabularDataType.Int64 => long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value)
                    ? Valid(JsonSerializer.SerializeToElement(value))
                    : Invalid(text, "filter.value.int64"),
            TabularDataType.Decimal => decimal.TryParse(
                text,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var value)
                    ? Valid(JsonSerializer.SerializeToElement(value))
                    : Invalid(text, "filter.value.decimal"),
            TabularDataType.Double => ParseDouble(text),
            TabularDataType.DateTimeOffset => DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value)
                    ? Valid(JsonSerializer.SerializeToElement(
                        value.ToString("O", CultureInfo.InvariantCulture)))
                    : Invalid(text, "filter.value.dateTimeOffset"),
            TabularDataType.Guid => Guid.TryParse(text, out var value)
                ? Valid(JsonSerializer.SerializeToElement(value.ToString("D")))
                : Invalid(text, "filter.value.guid"),
            TabularDataType.Binary => ParseBinary(text),
            _ => Invalid(text, "filter.value.type"),
        };
    }

    private static FilterScalarDraftResult ParseDouble(string text)
    {
        if (!double.TryParse(
                text,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var value)
            || !double.IsFinite(value))
        {
            return Invalid(text, "filter.value.double");
        }

        return Valid(JsonSerializer.SerializeToElement(value));
    }

    private static FilterScalarDraftResult ParseBinary(string text)
    {
        try
        {
            var bytes = Convert.FromBase64String(text);
            return Valid(JsonSerializer.SerializeToElement(
                Convert.ToBase64String(bytes)));
        }
        catch (FormatException)
        {
            return Invalid(text, "filter.value.binary");
        }
    }

    private static FilterScalarDraftResult Valid(JsonElement value) =>
        new(value, null);

    private static FilterScalarDraftResult Invalid(
        string text,
        string errorCode) =>
        new(JsonSerializer.SerializeToElement(text), errorCode);
}

internal sealed class StableEditorRowKeys
{
    private readonly List<string> keys = [];

    public int Count => keys.Count;

    public string Get(int index) => keys[index];

    public void Reset(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        keys.Clear();
        for (var index = 0; index < count; index++)
        {
            Add();
        }
    }

    public void Add() => keys.Add(TransformEditorRowId.Create());

    public void RemoveAt(int index) => keys.RemoveAt(index);

    public void Move(int index, int target)
    {
        if (index < 0
            || index >= keys.Count
            || target < 0
            || target >= keys.Count
            || index == target)
        {
            return;
        }

        var key = keys[index];
        keys.RemoveAt(index);
        keys.Insert(target, key);
    }
}

internal sealed record DesignerEdge(string From, string To);

internal static class DesignerEdgeSet
{
    public static bool Contains(
        IEnumerable<DesignerEdge> edges,
        string fromNodeId,
        string toNodeId) =>
        edges.Any(edge =>
            Same(edge.From, fromNodeId)
            && Same(edge.To, toNodeId));

    public static int DistinctIncomingCount(
        IEnumerable<DesignerEdge> edges,
        string nodeId) =>
        Incoming(edges, nodeId).Count;

    public static IReadOnlyList<string> Incoming(
        IEnumerable<DesignerEdge> edges,
        string nodeId) =>
        edges
            .Where(edge => Same(edge.To, nodeId))
            .Select(edge => edge.From)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> Downstream(
        IEnumerable<DesignerEdge> edges,
        string nodeId) =>
        edges
            .Where(edge => Same(edge.From, nodeId))
            .Select(edge => edge.To)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static void RemoveIncident(
        List<DesignerEdge> edges,
        string nodeId) =>
        edges.RemoveAll(edge =>
            Same(edge.From, nodeId)
            || Same(edge.To, nodeId));

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
