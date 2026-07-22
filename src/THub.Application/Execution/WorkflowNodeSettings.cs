using System.Text.Json;
using THub.Application.Workflows;
using THub.Domain.Workflows;

namespace THub.Application.Execution;

public abstract record WorkflowNodeSettings;

public sealed record SqlSourceNodeSettings(
    Guid ConnectionId,
    string Schema,
    string Object,
    int BatchSize,
    IReadOnlyList<string>? Columns) : WorkflowNodeSettings;

public sealed record DelimitedColumnSettings(
    string Name,
    TabularDataType DataType,
    bool IsNullable);

public sealed record CsvSourceNodeSettings(
    Guid ConnectionId,
    string RelativePath,
    bool HasHeader,
    char Delimiter,
    IReadOnlyList<DelimitedColumnSettings>? Columns) : WorkflowNodeSettings;

public sealed record ExcelSourceNodeSettings(
    Guid ConnectionId,
    string RelativePath,
    string Worksheet,
    string? Range,
    bool HasHeader,
    IReadOnlyList<DelimitedColumnSettings>? Columns) : WorkflowNodeSettings;

public sealed record SelectColumnsNodeSettings(
    IReadOnlyList<string> Columns) : WorkflowNodeSettings;

public sealed record FilterConditionSettings(
    string Column,
    string Operator,
    JsonElement Value);

public sealed record FilterRowsNodeSettings(
    IReadOnlyList<FilterConditionSettings> Conditions) : WorkflowNodeSettings;

public sealed record JoinNodeSettings(
    string LeftNodeId,
    string RightNodeId,
    IReadOnlyList<string> LeftKeys,
    IReadOnlyList<string> RightKeys,
    string JoinType,
    int MaximumBufferedRows) : WorkflowNodeSettings;

public sealed record SqlTargetNodeSettings(
    Guid ConnectionId,
    string Schema,
    string Object,
    string Mode,
    IReadOnlyDictionary<string, string> Mappings) : WorkflowNodeSettings;

public sealed record CsvTargetNodeSettings(
    Guid ConnectionId,
    string RelativePath,
    bool IncludeHeader,
    char Delimiter,
    string Mode) : WorkflowNodeSettings;

public sealed record ExcelTargetNodeSettings(
    Guid ConnectionId,
    string RelativePath,
    string Worksheet,
    bool IncludeHeader,
    string Mode) : WorkflowNodeSettings;

public sealed record EmailAlertNodeSettings(
    Guid ProfileId,
    IReadOnlyList<string> Recipients,
    string Subject,
    string Body,
    int MaximumAttempts) : WorkflowNodeSettings;

public sealed class WorkflowNodeSettingsException : Exception
{
    public WorkflowNodeSettingsException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public WorkflowNodeSettingsException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class WorkflowNodeSettingsValidator
{
    private static readonly HashSet<string> SqlSourceProperties =
        ["connectionId", "schema", "object", "batchSize", "columns"];
    private static readonly HashSet<string> CsvSourceProperties =
        ["connectionId", "relativePath", "hasHeader", "delimiter", "columns"];
    private static readonly HashSet<string> ExcelSourceProperties =
        ["connectionId", "relativePath", "worksheet", "range", "hasHeader", "columns"];
    private static readonly HashSet<string> SelectProperties = ["columns"];
    private static readonly HashSet<string> FilterProperties = ["conditions"];
    private static readonly HashSet<string> FilterConditionProperties =
        ["column", "operator", "value"];
    private static readonly HashSet<string> JoinProperties =
        ["leftNodeId", "rightNodeId", "leftKeys", "rightKeys", "type", "maximumBufferedRows"];
    private static readonly HashSet<string> SqlTargetProperties =
        ["connectionId", "schema", "object", "mode", "mappings"];
    private static readonly HashSet<string> CsvTargetProperties =
        ["connectionId", "relativePath", "includeHeader", "delimiter", "mode"];
    private static readonly HashSet<string> ExcelTargetProperties =
        ["connectionId", "relativePath", "worksheet", "includeHeader", "mode"];
    private static readonly HashSet<string> EmailProperties =
        ["profileId", "recipients", "subject", "body", "maximumAttempts"];
    private static readonly HashSet<string> ColumnProperties = ["name", "type", "nullable"];
    private static readonly HashSet<string> FilterOperators = new(
        ["equals", "notEquals", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual", "contains", "startsWith", "endsWith", "isNull", "isNotNull"],
        StringComparer.Ordinal);

    public IReadOnlyList<GraphValidationIssue> Validate(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var issues = new List<GraphValidationIssue>();
        foreach (var node in graph.Nodes)
        {
            try
            {
                var settings = Parse(node);
                if (settings is JoinNodeSettings join)
                {
                    ValidateJoinInputs(graph, node, join);
                }
            }
            catch (WorkflowNodeSettingsException exception)
            {
                issues.Add(new(exception.Code, exception.Message, node.Id));
            }
        }

        return issues;
    }

    private static void ValidateJoinInputs(
        WorkflowGraph graph,
        WorkflowNode node,
        JoinNodeSettings settings)
    {
        if (string.Equals(settings.LeftNodeId, settings.RightNodeId, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("node.join.inputs.distinct", "Join left and right node ids must be distinct.");
        }

        var incoming = graph.Edges
            .Where(edge => string.Equals(edge.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge.FromNodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (incoming.Count != 2
            || !incoming.Contains(settings.LeftNodeId)
            || !incoming.Contains(settings.RightNodeId))
        {
            throw Invalid(
                "node.join.inputs.mismatch",
                "Join leftNodeId and rightNodeId must identify its two incoming workflow edges.");
        }
    }

    public WorkflowNodeSettings Parse(WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        try
        {
            using var document = JsonDocument.Parse(node.SettingsJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("node.settings.object", "Node settings must be a JSON object.");
            }

            return node.Kind switch
            {
                WorkflowNodeKind.SqlSource => ReadSqlSource(root),
                WorkflowNodeKind.CsvSource => ReadCsvSource(root),
                WorkflowNodeKind.ExcelSource => ReadExcelSource(root),
                WorkflowNodeKind.SelectColumns => ReadSelect(root),
                WorkflowNodeKind.FilterRows => ReadFilter(root),
                WorkflowNodeKind.Join => ReadJoin(root),
                WorkflowNodeKind.SqlTarget => ReadSqlTarget(root),
                WorkflowNodeKind.CsvTarget => ReadCsvTarget(root),
                WorkflowNodeKind.ExcelTarget => ReadExcelTarget(root),
                WorkflowNodeKind.EmailAlert => ReadEmail(root),
                WorkflowNodeKind.Webhook or WorkflowNodeKind.Executable =>
                    throw Invalid("node.kind.disabled", $"{node.Kind} execution is disabled by policy."),
                WorkflowNodeKind.PublishRestApi or WorkflowNodeKind.PublishDataEditor =>
                    throw Invalid(
                        "node.publication.separate",
                        "Data publications cannot execute as ordinary workflow nodes."),
                _ => throw Invalid("node.kind.unsupported", "The node kind is not supported.")
            };
        }
        catch (WorkflowNodeSettingsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or FormatException
            or OverflowException
            or ArgumentException)
        {
            throw new WorkflowNodeSettingsException(
                "node.settings.invalid",
                "Node settings are malformed or contain a value of the wrong type.",
                exception);
        }
    }

    private static SqlSourceNodeSettings ReadSqlSource(JsonElement root)
    {
        EnsureOnly(root, SqlSourceProperties);
        var columns = ReadOptionalStringArray(root, "columns", 512);
        return new(
            ReadGuid(root, "connectionId"),
            ReadIdentifier(root, "schema"),
            ReadIdentifier(root, "object"),
            ReadInt(root, "batchSize", 1, 10_000),
            columns);
    }

    private static CsvSourceNodeSettings ReadCsvSource(JsonElement root)
    {
        EnsureOnly(root, CsvSourceProperties);
        var hasHeader = ReadBoolean(root, "hasHeader");
        var columns = ReadOptionalColumns(root);
        if (!hasHeader && columns is null)
        {
            throw Invalid(
                "node.csv.columns.required",
                "CSV sources without a header require an explicit typed columns array.");
        }

        return new(
            ReadGuid(root, "connectionId"),
            ReadRelativePath(root, "relativePath", ".csv"),
            hasHeader,
            ReadDelimiter(root, "delimiter", ','),
            columns);
    }

    private static ExcelSourceNodeSettings ReadExcelSource(JsonElement root)
    {
        EnsureOnly(root, ExcelSourceProperties);
        var hasHeader = ReadBoolean(root, "hasHeader");
        var columns = ReadOptionalColumns(root);
        if (!hasHeader && columns is null)
        {
            throw Invalid(
                "node.excel.columns.required",
                "Excel sources without a header require an explicit typed columns array.");
        }

        return new(
            ReadGuid(root, "connectionId"),
            ReadWorkbookPath(root, "relativePath"),
            ReadText(root, "worksheet", 128),
            ReadOptionalText(root, "range", 128),
            hasHeader,
            columns);
    }

    private static SelectColumnsNodeSettings ReadSelect(JsonElement root)
    {
        EnsureOnly(root, SelectProperties);
        return new(ReadStringArray(root, "columns", 1, 512));
    }

    private static FilterRowsNodeSettings ReadFilter(JsonElement root)
    {
        EnsureOnly(root, FilterProperties);
        var conditionsElement = Require(root, "conditions");
        if (conditionsElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("node.filter.conditions.invalid", "Filter conditions must be an array.");
        }

        var conditions = new List<FilterConditionSettings>();
        foreach (var element in conditionsElement.EnumerateArray())
        {
            if (conditions.Count == 32)
            {
                throw Invalid("node.filter.conditions.limit", "A filter cannot contain more than 32 conditions.");
            }

            EnsureOnly(element, FilterConditionProperties);
            var column = ReadText(element, "column", TabularColumn.MaximumNameLength);
            var operation = ReadText(element, "operator", 32);
            if (!FilterOperators.Contains(operation))
            {
                throw Invalid("node.filter.operator.invalid", $"Filter operator '{operation}' is not supported.");
            }

            var value = element.TryGetProperty("value", out var valueElement)
                ? valueElement.Clone()
                : default;
            if (operation is not ("isNull" or "isNotNull")
                && value.ValueKind == JsonValueKind.Undefined)
            {
                throw Invalid("node.filter.value.required", "This filter operator requires a value.");
            }

            if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                throw Invalid("node.filter.value.scalar", "Filter values must be JSON scalars.");
            }

            conditions.Add(new(column, operation, value));
        }

        if (conditions.Count == 0)
        {
            throw Invalid("node.filter.conditions.required", "At least one filter condition is required.");
        }

        return new(conditions.AsReadOnly());
    }

    private static JoinNodeSettings ReadJoin(JsonElement root)
    {
        EnsureOnly(root, JoinProperties);
        var left = ReadStringArray(root, "leftKeys", 1, 16);
        var right = ReadStringArray(root, "rightKeys", 1, 16);
        if (left.Count != right.Count)
        {
            throw Invalid("node.join.keys.cardinality", "Join key arrays must have the same length.");
        }

        var joinType = ReadOptionalText(root, "type", 16) ?? "inner";
        if (joinType is not ("inner" or "left"))
        {
            throw Invalid("node.join.type.invalid", "Join type must be 'inner' or 'left'.");
        }

        return new(
            ReadNodeId(root, "leftNodeId"),
            ReadNodeId(root, "rightNodeId"),
            left,
            right,
            joinType,
            ReadInt(root, "maximumBufferedRows", 1, 1_000_000));
    }

    private static SqlTargetNodeSettings ReadSqlTarget(JsonElement root)
    {
        EnsureOnly(root, SqlTargetProperties);
        var mode = ReadText(root, "mode", 32);
        if (mode != "insert")
        {
            throw Invalid("node.sql-target.mode.invalid", "SQL target v1 supports only explicit insert mode.");
        }

        return new(
            ReadGuid(root, "connectionId"),
            ReadIdentifier(root, "schema"),
            ReadIdentifier(root, "object"),
            mode,
            ReadMappings(root));
    }

    private static CsvTargetNodeSettings ReadCsvTarget(JsonElement root)
    {
        EnsureOnly(root, CsvTargetProperties);
        var mode = ReadOptionalText(root, "mode", 32) ?? "createNew";
        if (mode != "createNew")
        {
            throw Invalid("node.file-target.mode.invalid", "File target v1 supports only createNew mode.");
        }

        return new(
            ReadGuid(root, "connectionId"),
            ReadRelativePath(root, "relativePath", ".csv"),
            ReadBoolean(root, "includeHeader"),
            ReadDelimiter(root, "delimiter", ','),
            mode);
    }

    private static ExcelTargetNodeSettings ReadExcelTarget(JsonElement root)
    {
        EnsureOnly(root, ExcelTargetProperties);
        var mode = ReadOptionalText(root, "mode", 32) ?? "createNew";
        if (mode != "createNew")
        {
            throw Invalid("node.file-target.mode.invalid", "File target v1 supports only createNew mode.");
        }

        return new(
            ReadGuid(root, "connectionId"),
            ReadWorkbookPath(root, "relativePath"),
            ReadText(root, "worksheet", 128),
            ReadOptionalBoolean(root, "includeHeader") ?? true,
            mode);
    }

    private static EmailAlertNodeSettings ReadEmail(JsonElement root)
    {
        EnsureOnly(root, EmailProperties);
        return new(
            ReadGuid(root, "profileId"),
            ReadStringArray(root, "recipients", 1, 100, 320),
            ReadText(root, "subject", 500),
            ReadMultilineText(root, "body", 100_000),
            ReadOptionalInt(root, "maximumAttempts", 1, 20) ?? 5);
    }

    private static IReadOnlyList<DelimitedColumnSettings>? ReadOptionalColumns(JsonElement root)
    {
        if (!root.TryGetProperty("columns", out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("node.columns.invalid", "Columns must be an array.");
        }

        var columns = new List<DelimitedColumnSettings>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray())
        {
            if (columns.Count == TabularSchema.AbsoluteMaximumColumns)
            {
                throw Invalid("node.columns.limit", "The configured column count exceeds the supported limit.");
            }

            EnsureOnly(item, ColumnProperties);
            var name = ReadText(item, "name", TabularColumn.MaximumNameLength);
            if (!names.Add(name))
            {
                throw Invalid("node.columns.duplicate", $"Column '{name}' is configured more than once.");
            }

            var typeText = ReadText(item, "type", 32);
            if (!Enum.TryParse<TabularDataType>(typeText, ignoreCase: true, out var dataType))
            {
                throw Invalid("node.columns.type.invalid", $"Column type '{typeText}' is not supported.");
            }

            columns.Add(new(name, dataType, ReadOptionalBoolean(item, "nullable") ?? true));
        }

        return columns.Count == 0 ? null : columns.AsReadOnly();
    }

    private static IReadOnlyDictionary<string, string> ReadMappings(JsonElement root)
    {
        if (!root.TryGetProperty("mappings", out var element))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("node.target.mappings.invalid", "Target mappings must be an object.");
        }

        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (mappings.Count == TabularSchema.AbsoluteMaximumColumns)
            {
                throw Invalid("node.target.mappings.limit", "The target mapping count exceeds the supported limit.");
            }

            var source = ValidateText(property.Name, "mapping source", TabularColumn.MaximumNameLength);
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw Invalid("node.target.mapping.invalid", "Every target mapping value must be a column name.");
            }

            var target = ValidateText(
                property.Value.GetString() ?? string.Empty,
                "mapping target",
                TabularColumn.MaximumNameLength);
            if (!mappings.TryAdd(source, target))
            {
                throw Invalid("node.target.mapping.duplicate", $"Source column '{source}' is mapped more than once.");
            }
        }

        return mappings;
    }

    private static IReadOnlyList<string>? ReadOptionalStringArray(
        JsonElement root,
        string propertyName,
        int maximumCount) => root.TryGetProperty(propertyName, out _)
        ? ReadStringArray(root, propertyName, 1, maximumCount)
        : null;

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement root,
        string propertyName,
        int minimumCount,
        int maximumCount,
        int maximumLength = TabularColumn.MaximumNameLength)
    {
        var element = Require(root, propertyName);
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("node.settings.array.invalid", $"'{propertyName}' must be an array.");
        }

        var values = new List<string>();
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray())
        {
            if (values.Count == maximumCount || item.ValueKind != JsonValueKind.String)
            {
                throw Invalid("node.settings.array.limit", $"'{propertyName}' contains too many or invalid values.");
            }

            var value = ValidateText(item.GetString() ?? string.Empty, propertyName, maximumLength);
            if (!distinct.Add(value))
            {
                throw Invalid("node.settings.array.duplicate", $"'{propertyName}' contains duplicate value '{value}'.");
            }

            values.Add(value);
        }

        if (values.Count < minimumCount)
        {
            throw Invalid("node.settings.array.required", $"'{propertyName}' requires at least {minimumCount} value(s).");
        }

        return values.AsReadOnly();
    }

    private static Guid ReadGuid(JsonElement root, string propertyName)
    {
        var text = ReadText(root, propertyName, 36);
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty)
        {
            throw Invalid("node.connection.required", $"'{propertyName}' must be a non-empty GUID.");
        }

        return value;
    }

    private static string ReadIdentifier(JsonElement root, string propertyName) =>
        ReadText(root, propertyName, 128);

    private static string ReadNodeId(JsonElement root, string propertyName)
    {
        var value = ReadText(root, propertyName, 128);
        if (!char.IsLetterOrDigit(value[0])
            || value.Any(character => !char.IsLetterOrDigit(character)
                && character is not ('.' or '_' or '-')))
        {
            throw Invalid("node.join.input.invalid", $"'{propertyName}' is not a valid workflow node id.");
        }

        return value;
    }

    private static string ReadRelativePath(JsonElement root, string propertyName, string extension)
    {
        var value = ReadText(root, propertyName, 1_024);
        if (Path.IsPathRooted(value)
            || value.Contains(':')
            || !string.Equals(Path.GetExtension(value), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                "node.file.path.invalid",
                $"'{propertyName}' must be a relative {extension} path beneath an approved root.");
        }

        return value;
    }

    private static string ReadWorkbookPath(JsonElement root, string propertyName)
    {
        var value = ReadText(root, propertyName, 1_024);
        if (Path.IsPathRooted(value) || value.Contains(':'))
        {
            throw Invalid("node.file.path.invalid", "Workbook paths must be relative to an approved root.");
        }

        var extension = Path.GetExtension(value);
        if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("node.excel.extension.invalid", "Excel v1 supports only .xlsx and .xlsm workbooks.");
        }

        return value;
    }

    private static char ReadDelimiter(JsonElement root, string propertyName, char defaultValue)
    {
        var text = ReadOptionalText(root, propertyName, 1);
        var value = text is null ? defaultValue : text[0];
        if (value is '\r' or '\n' or '"' || char.IsControl(value))
        {
            throw Invalid("node.csv.delimiter.invalid", "CSV delimiter must be one printable character other than a quote.");
        }

        return value;
    }

    private static bool ReadBoolean(JsonElement root, string propertyName) =>
        Require(root, propertyName).GetBoolean();

    private static bool? ReadOptionalBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) ? element.GetBoolean() : null;

    private static int ReadInt(JsonElement root, string propertyName, int minimum, int maximum)
    {
        var value = Require(root, propertyName).GetInt32();
        if (value < minimum || value > maximum)
        {
            throw Invalid("node.settings.number.limit", $"'{propertyName}' must be {minimum} to {maximum}.");
        }

        return value;
    }

    private static int? ReadOptionalInt(
        JsonElement root,
        string propertyName,
        int minimum,
        int maximum) => root.TryGetProperty(propertyName, out _)
        ? ReadInt(root, propertyName, minimum, maximum)
        : null;

    private static string ReadText(JsonElement root, string propertyName, int maximumLength)
    {
        var element = Require(root, propertyName);
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Invalid("node.settings.text.invalid", $"'{propertyName}' must be text.");
        }

        return ValidateText(element.GetString() ?? string.Empty, propertyName, maximumLength);
    }

    private static string? ReadOptionalText(
        JsonElement root,
        string propertyName,
        int maximumLength) => root.TryGetProperty(propertyName, out _)
        ? ReadText(root, propertyName, maximumLength)
        : null;

    private static string ReadMultilineText(
        JsonElement root,
        string propertyName,
        int maximumLength)
    {
        var element = Require(root, propertyName);
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Invalid("node.settings.text.invalid", $"'{propertyName}' must be text.");
        }

        var value = element.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid("node.settings.text.required", $"'{propertyName}' is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength
            || normalized.Any(character => char.IsControl(character)
                && character is not ('\r' or '\n' or '\t')))
        {
            throw Invalid(
                "node.settings.text.limit",
                $"'{propertyName}' is invalid or exceeds {maximumLength} characters.");
        }

        return normalized;
    }

    private static string ValidateText(string value, string propertyName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid("node.settings.text.required", $"'{propertyName}' is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw Invalid("node.settings.text.limit", $"'{propertyName}' is invalid or exceeds {maximumLength} characters.");
        }

        return normalized;
    }

    private static JsonElement Require(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw Invalid("node.settings.property.required", $"Required setting '{propertyName}' is missing.");
        }

        return value;
    }

    private static void EnsureOnly(JsonElement root, IReadOnlySet<string> allowed)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("node.settings.object", "Node settings must be a JSON object.");
        }

        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!encountered.Add(property.Name))
            {
                throw Invalid("node.settings.property.duplicate", $"Setting '{property.Name}' is duplicated.");
            }
            if (!allowed.Contains(property.Name))
            {
                throw Invalid("node.settings.property.unsupported", $"Setting '{property.Name}' is not supported for this node.");
            }
        }
    }

    private static WorkflowNodeSettingsException Invalid(string code, string message) =>
        new(code, message);
}
