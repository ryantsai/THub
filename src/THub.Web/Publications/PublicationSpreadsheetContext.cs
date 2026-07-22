using System.Text.Json;
using Radzen.Documents.Spreadsheet;
using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Web.Publications;

public sealed class PublicationSpreadsheetContext(
    PublicationDataService dataService,
    Guid publicationId,
    IReadOnlyCollection<PublicationRole> roles,
    IReadOnlyList<PublicationColumnDto> columns)
{
    private const int LookupTake = 100;
    private readonly Dictionary<string, IReadOnlyList<PublicationLookupChoice>> _choices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
    private readonly PublicationColumnDto[] _columns = columns.OrderBy(column => column.Ordinal).ToArray();

    public PublicationColumnDto? GetColumn(int worksheetColumn) =>
        worksheetColumn >= 0 && worksheetColumn < _columns.Length ? _columns[worksheetColumn] : null;

    public async Task<(IReadOnlyList<PublicationLookupChoice> Choices, string? Error)> SearchAsync(
        int worksheetColumn,
        string? search,
        CancellationToken cancellationToken)
    {
        var column = GetColumn(worksheetColumn);
        if (column?.ForeignKey is null)
        {
            return ([], "This cell does not have an approved foreign-key lookup.");
        }

        var result = await dataService.ReadForeignKeyLookupAsync(
            new PublicationForeignKeyLookupQuery(
                publicationId,
                column.PublicAlias,
                roles,
                search,
                LookupTake),
            cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return ([], result.Problem?.Message ?? "The lookup is temporarily unavailable.");
        }

        var choices = result.Value.Items
            .Select(item => new PublicationLookupChoice(
                CreateChoiceId(item.KeyValues),
                item.KeyValues,
                item.DisplayText))
            .ToArray();
        _choices[column.ForeignKey.ConstraintName] = choices;
        foreach (var choice in choices)
        {
            _labels[CreateLabelKey(column.ForeignKey.ConstraintName, choice.KeyValues)] = choice.DisplayText;
        }

        return (choices, null);
    }

    public async Task<string?> ResolveLoadedLabelsAsync(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var pendingByLabelKey = new Dictionary<string, PendingLabel>(StringComparer.Ordinal);
        foreach (var group in _columns
                     .Where(column => column.ForeignKey is not null)
                     .GroupBy(column => column.ForeignKey!.ConstraintName, StringComparer.OrdinalIgnoreCase))
        {
            var columns = group.OrderBy(column => column.ForeignKey!.Ordinal).ToArray();
            foreach (var row in rows)
            {
                var values = columns.ToDictionary(
                    column => column.PublicAlias,
                    column => row.TryGetValue(column.PublicAlias, out var value) ? value : null,
                    StringComparer.OrdinalIgnoreCase);
                var nullCount = values.Values.Count(value => value is null or DBNull);
                if (nullCount == values.Count)
                {
                    continue;
                }

                if (nullCount > 0)
                {
                    return $"Source data contains a partial value for foreign key '{group.Key}'.";
                }

                var labelKey = CreateLabelKey(group.Key, values);
                pendingByLabelKey.TryAdd(
                    labelKey,
                    new PendingLabel(group.Key, columns[0].PublicAlias, values));
            }
        }

        if (pendingByLabelKey.Count == 0)
        {
            return null;
        }

        if (pendingByLabelKey.Count > PublicationDataService.MaximumForeignKeyTuples)
        {
            return $"This window contains more than {PublicationDataService.MaximumForeignKeyTuples} distinct foreign-key tuples.";
        }

        var pending = pendingByLabelKey
            .Select((item, requestId) => new PendingLabelRequest(
                requestId,
                item.Key,
                item.Value))
            .ToArray();
        var result = await dataService.ResolveForeignKeyLabelsAsync(
            new PublicationForeignKeyLabelQuery(
                publicationId,
                roles,
                pending.Select(item => new PublicationForeignKeyLabelRequest(
                    item.RequestId,
                    item.Pending.ColumnAlias,
                    item.Pending.KeyValues)).ToArray()),
            cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return result.Problem?.Message ?? "Existing foreign-key labels could not be resolved.";
        }

        var labels = result.Value.Labels.ToDictionary(label => label.RequestId);
        if (labels.Count != pending.Length || pending.Any(item => !labels.ContainsKey(item.RequestId)))
        {
            return "One or more existing foreign-key tuples no longer exist in the approved referenced table.";
        }

        foreach (var item in pending)
        {
            _labels[item.LabelKey] = labels[item.RequestId].DisplayText;
        }

        return null;
    }

    public string GetDisplayText(SpreadsheetCellRenderContextAdapter context)
    {
        var column = GetColumn(context.Column);
        if (column?.ForeignKey is null)
        {
            return context.FormattedValue;
        }

        var keyValues = GetForeignKeyValues(column.ForeignKey.ConstraintName, context.Row, context.Worksheet);
        return _labels.TryGetValue(CreateLabelKey(column.ForeignKey.ConstraintName, keyValues), out var label)
            ? label
            : context.FormattedValue;
    }

    public void ApplyChoice(Worksheet worksheet, int row, PublicationLookupChoice? choice, string constraintName)
    {
        foreach (var (column, index) in _columns.Select((column, index) => (column, index))
                     .Where(item => string.Equals(
                         item.column.ForeignKey?.ConstraintName,
                         constraintName,
                         StringComparison.OrdinalIgnoreCase)))
        {
            object? value = null;
            choice?.KeyValues.TryGetValue(column.PublicAlias, out value);
            worksheet.Cells[row, index].Formula = string.Empty;
            worksheet.Cells[row, index].Value = choice is null
                ? null
                : PublicationSpreadsheetChangeMapper.ToSpreadsheetValue(value, column);
        }
    }

    public IReadOnlyList<PublicationLookupChoice> GetCachedChoices(int worksheetColumn)
    {
        var constraint = GetColumn(worksheetColumn)?.ForeignKey?.ConstraintName;
        return constraint is not null && _choices.TryGetValue(constraint, out var choices) ? choices : [];
    }

    private IReadOnlyDictionary<string, object?> GetForeignKeyValues(
        string constraintName,
        int row,
        Worksheet worksheet)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (column, index) in _columns.Select((column, index) => (column, index))
                     .Where(item => string.Equals(
                         item.column.ForeignKey?.ConstraintName,
                         constraintName,
                         StringComparison.OrdinalIgnoreCase)))
        {
            values[column.PublicAlias] = worksheet.Cells[row, index].Value;
        }

        return values;
    }

    private static string CreateChoiceId(IReadOnlyDictionary<string, object?> values) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)));

    private static string CreateLabelKey(string constraintName, IReadOnlyDictionary<string, object?> values) =>
        $"{constraintName}\u001f{CreateChoiceId(values)}";

    private sealed record PendingLabel(
        string ConstraintName,
        string ColumnAlias,
        IReadOnlyDictionary<string, object?> KeyValues);

    private sealed record PendingLabelRequest(
        int RequestId,
        string LabelKey,
        PendingLabel Pending);
}

public sealed record PublicationLookupChoice(
    string Id,
    IReadOnlyDictionary<string, object?> KeyValues,
    string DisplayText)
{
    public string KeyText => string.Join(
        " · ",
        KeyValues.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Key}: {item.Value}"));
}

public sealed record SpreadsheetCellRenderContextAdapter(
    Worksheet Worksheet,
    int Row,
    int Column,
    string FormattedValue);
