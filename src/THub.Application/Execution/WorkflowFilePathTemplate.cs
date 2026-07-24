using System.Globalization;
using System.Text;

namespace THub.Application.Execution;

public static class WorkflowFilePathTemplate
{
    public const int MaximumLength = 1_024;
    private const int MaximumFormatLength = 128;

    public static IReadOnlyList<string> GetVariableNames(string template)
    {
        var parts = Parse(template);
        return parts
            .Where(static part => part.VariableName is not null)
            .Select(static part => part.VariableName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string CreateValidationPath(string template)
    {
        var parts = Parse(template);
        var result = new StringBuilder(template.Length);
        foreach (var part in parts)
        {
            result.Append(part.VariableName is null ? part.Literal : "value");
        }

        return result.ToString();
    }

    public static string Render(
        string template,
        IReadOnlyDictionary<string, TabularValue> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var parts = Parse(template);
        var result = new StringBuilder(template.Length);
        foreach (var part in parts)
        {
            if (part.VariableName is null)
            {
                result.Append(part.Literal);
                continue;
            }

            if (!variables.TryGetValue(part.VariableName, out var value))
            {
                throw new InvalidOperationException(
                    $"File name variable '{part.VariableName}' is not available.");
            }

            var rendered = FormatValue(part.VariableName, value, part.Format);
            EnsureSafeSegment(part.VariableName, rendered);
            result.Append(rendered);
            if (result.Length > MaximumLength)
            {
                throw new InvalidOperationException(
                    $"The rendered file path exceeds {MaximumLength} characters.");
            }
        }

        return result.ToString();
    }

    private static IReadOnlyList<TemplatePart> Parse(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        if (template.Length > MaximumLength || template.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A file path template must be no longer than {MaximumLength} characters and cannot contain control characters.",
                nameof(template));
        }

        var parts = new List<TemplatePart>();
        var literalStart = 0;
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] == '}')
            {
                throw new ArgumentException(
                    "A file path template contains an unmatched closing brace.",
                    nameof(template));
            }

            if (template[index] != '{')
            {
                continue;
            }

            if (index > literalStart)
            {
                parts.Add(new(template[literalStart..index], null, null));
            }

            var closing = template.IndexOf('}', index + 1);
            if (closing < 0 || template.IndexOf('{', index + 1, closing - index - 1) >= 0)
            {
                throw new ArgumentException(
                    "A file path template contains an invalid variable placeholder.",
                    nameof(template));
            }

            var token = template[(index + 1)..closing];
            var separator = token.IndexOf(':');
            var variableName = separator < 0 ? token : token[..separator];
            var format = separator < 0 ? null : token[(separator + 1)..];
            if (!IsValidVariableName(variableName)
                || format is { Length: 0 or > MaximumFormatLength }
                || format?.Any(character => character is '{' or '}' || char.IsControl(character)) == true)
            {
                throw new ArgumentException(
                    "File path placeholders must use {variableName} or {variableName:format}.",
                    nameof(template));
            }

            parts.Add(new(null, variableName, format));
            index = closing;
            literalStart = closing + 1;
        }

        if (literalStart < template.Length)
        {
            parts.Add(new(template[literalStart..], null, null));
        }

        return parts;
    }

    private static string FormatValue(
        string variableName,
        TabularValue value,
        string? format)
    {
        if (value.Kind == TabularValueKind.Null)
        {
            throw new InvalidOperationException(
                $"File name variable '{variableName}' resolved to null.");
        }

        try
        {
            return value.Kind switch
            {
                TabularValueKind.Boolean when format is null =>
                    ((bool)value.Value!).ToString(CultureInfo.InvariantCulture),
                TabularValueKind.Int64 =>
                    ((long)value.Value!).ToString(format, CultureInfo.InvariantCulture),
                TabularValueKind.Decimal =>
                    ((decimal)value.Value!).ToString(format, CultureInfo.InvariantCulture),
                TabularValueKind.Double =>
                    ((double)value.Value!).ToString(format, CultureInfo.InvariantCulture),
                TabularValueKind.String when format is null => (string)value.Value!,
                TabularValueKind.DateTimeOffset =>
                    ((DateTimeOffset)value.Value!).ToString(
                        format ?? "yyyyMMdd_HHmmss_fff",
                        CultureInfo.InvariantCulture),
                TabularValueKind.Guid =>
                    ((Guid)value.Value!).ToString(format ?? "N"),
                TabularValueKind.Binary => throw new InvalidOperationException(
                    $"Binary variable '{variableName}' cannot be used in a file name."),
                _ => throw new InvalidOperationException(
                    $"The format for file name variable '{variableName}' is not supported.")
            };
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"File name variable '{variableName}' uses an invalid format.",
                exception);
        }
    }

    private static void EnsureSafeSegment(string variableName, string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.Any(character =>
                char.IsControl(character)
                || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'))
        {
            throw new InvalidOperationException(
                $"File name variable '{variableName}' produced characters that are not safe in a file name.");
        }
    }

    private static bool IsValidVariableName(string value) =>
        value.Length is > 0 and <= 64
        && (char.IsLetter(value[0]) || value[0] is '_' or '$')
        && value.Skip(1).All(character =>
            char.IsLetterOrDigit(character) || character is '_' or '$');

    private sealed record TemplatePart(
        string? Literal,
        string? VariableName,
        string? Format);
}
