using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Infrastructure.Publications;

internal sealed record SqlPublicationSortTerm(
    PublicationColumn Column,
    bool Descending,
    string SqlExpression);

internal static class SqlPublicationCursorCodec
{
    private const int CurrentVersion = 1;
    private const int MaximumCursorLength = 4_096;

    public static string Encode(
        PublicationVersion version,
        IReadOnlyList<PublicationFilter> filters,
        IReadOnlyList<SqlPublicationSortTerm> sorts,
        IReadOnlyDictionary<string, object?> row)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", CurrentVersion);
            writer.WriteString("s", version.SchemaFingerprint);
            writer.WriteString("f", ComputeFilterHash(filters));
            writer.WriteStartArray("o");
            foreach (var sort in sorts)
            {
                writer.WriteStartObject();
                writer.WriteString("a", sort.Column.PublicAlias);
                writer.WriteBoolean("d", sort.Descending);
                if (!row.TryGetValue(sort.Column.PublicAlias, out var value) || value is null)
                {
                    writer.WriteNull("x");
                }
                else
                {
                    writer.WriteString(
                        "x",
                        SqlPublicationValueConverter.FormatCursorValue(value, sort.Column.DataType));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var encoded = Base64UrlEncode(stream.ToArray());
        if (encoded.Length > MaximumCursorLength)
        {
            throw new InvalidOperationException("The generated publication cursor exceeds its bound.");
        }

        return encoded;
    }

    public static bool TryDecode(
        string cursor,
        PublicationVersion version,
        IReadOnlyList<PublicationFilter> filters,
        IReadOnlyList<SqlPublicationSortTerm> sorts,
        out IReadOnlyList<object?> values)
    {
        values = [];
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaximumCursorLength)
        {
            return false;
        }

        try
        {
            var json = Base64UrlDecode(cursor);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasOnlyProperties(root, "v", "s", "f", "o") ||
                root.GetProperty("v").GetInt32() != CurrentVersion ||
                !string.Equals(root.GetProperty("s").GetString(), version.SchemaFingerprint, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("f").GetString(), ComputeFilterHash(filters), StringComparison.Ordinal) ||
                root.GetProperty("o").ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var sortValues = root.GetProperty("o").EnumerateArray().ToArray();
            if (sortValues.Length != sorts.Count)
            {
                return false;
            }

            var parsed = new object?[sorts.Count];
            for (var index = 0; index < sorts.Count; index++)
            {
                var element = sortValues[index];
                var sort = sorts[index];
                if (element.ValueKind != JsonValueKind.Object ||
                    !HasOnlyProperties(element, "a", "d", "x") ||
                    !string.Equals(
                        element.GetProperty("a").GetString(),
                        sort.Column.PublicAlias,
                        StringComparison.Ordinal) ||
                    element.GetProperty("d").GetBoolean() != sort.Descending)
                {
                    return false;
                }

                var valueElement = element.GetProperty("x");
                if (valueElement.ValueKind == JsonValueKind.Null)
                {
                    if (!sort.Column.IsNullable)
                    {
                        return false;
                    }

                    parsed[index] = null;
                    continue;
                }

                if (valueElement.ValueKind != JsonValueKind.String ||
                    !SqlPublicationValueConverter.TryParse(
                        valueElement.GetString()!,
                        sort.Column,
                        out parsed[index]))
                {
                    return false;
                }
            }

            values = parsed;
            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or FormatException
            or InvalidOperationException
            or OverflowException)
        {
            return false;
        }
    }

    private static string ComputeFilterHash(IReadOnlyList<PublicationFilter> filters)
    {
        var canonical = string.Join(
            '\u001e',
            filters
                .Select(filter => string.Join(
                    '\u001f',
                    filter.ColumnAlias.ToUpperInvariant(),
                    ((int)filter.Operator).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    filter.Value ?? "<NULL>"))
                .Order(StringComparer.Ordinal));
        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool HasOnlyProperties(JsonElement element, params string[] expected)
    {
        var expectedNames = expected.ToHashSet(StringComparer.Ordinal);
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expectedNames.Contains(property.Name) || !encountered.Add(property.Name))
            {
                return false;
            }
        }

        return encountered.SetEquals(expectedNames);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;
        if (padding == 1)
        {
            throw new FormatException("Cursor has invalid base64url length.");
        }

        if (padding > 0)
        {
            base64 = base64.PadRight(base64.Length + (4 - padding), '=');
        }

        return Convert.FromBase64String(base64);
    }
}
