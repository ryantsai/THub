namespace THub.Domain.Alerts;

public sealed class EmailTemplate
{
    private static readonly HashSet<string> AllowedVariableSet = new(StringComparer.Ordinal)
    {
        "workflow.id",
        "workflow.name",
        "run.id",
        "run.status",
        "run.triggeredBy",
        "run.startedAtUtc",
        "run.completedAtUtc",
        "error.code",
        "error.category",
        "error.summary"
    };

    public EmailTemplate(string subject, string body)
    {
        Subject = ValidateTemplate(
            subject,
            nameof(subject),
            EmailDeliveryLimits.AbsoluteMaximumSubjectLength,
            permitEmpty: false);
        Body = ValidateTemplate(
            body,
            nameof(body),
            EmailDeliveryLimits.AbsoluteMaximumBodyLength,
            permitEmpty: true);
    }

    public string Subject { get; }

    public string Body { get; }

    public EmailMessage Render(
        IEnumerable<string> recipients,
        IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        foreach (var key in variables.Keys)
        {
            if (!AllowedVariableSet.Contains(key))
            {
                throw new ArgumentException(
                    $"Template variable '{key}' is not allowed.",
                    nameof(variables));
            }
        }

        return new EmailMessage(
            recipients,
            RenderValue(Subject, variables),
            RenderValue(Body, variables));
    }

    private static string ValidateTemplate(
        string value,
        string parameterName,
        int maximumLength,
        bool permitEmpty)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!permitEmpty && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty template is required.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Template cannot exceed {maximumLength} characters.");
        }

        var position = 0;
        while (position < value.Length)
        {
            var start = value.IndexOf("{{", position, StringComparison.Ordinal);
            var unexpectedEnd = value.IndexOf("}}", position, StringComparison.Ordinal);
            if (unexpectedEnd >= 0 && (start < 0 || unexpectedEnd < start))
            {
                throw new ArgumentException("Template contains an unmatched closing delimiter.", parameterName);
            }

            if (start < 0)
            {
                break;
            }

            var end = value.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new ArgumentException("Template contains an unmatched opening delimiter.", parameterName);
            }

            var variable = value[(start + 2)..end].Trim();
            if (!AllowedVariableSet.Contains(variable))
            {
                throw new ArgumentException(
                    $"Template variable '{variable}' is not allowed.",
                    parameterName);
            }

            position = end + 2;
        }

        return value;
    }

    private static string RenderValue(
        string template,
        IReadOnlyDictionary<string, string?> variables)
    {
        var rendered = new System.Text.StringBuilder(template.Length);
        var position = 0;
        while (position < template.Length)
        {
            var start = template.IndexOf("{{", position, StringComparison.Ordinal);
            if (start < 0)
            {
                rendered.Append(template, position, template.Length - position);
                break;
            }

            rendered.Append(template, position, start - position);
            var end = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            var variable = template[(start + 2)..end].Trim();
            variables.TryGetValue(variable, out var value);
            rendered.Append(value);
            position = end + 2;
        }

        return rendered.ToString();
    }
}
