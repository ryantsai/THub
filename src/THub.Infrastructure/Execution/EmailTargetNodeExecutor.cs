using System.Globalization;
using System.Net;
using System.Text;
using THub.Application.Alerts;
using THub.Application.Execution;
using THub.Domain.Alerts;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Infrastructure.Execution;

/// <summary>
/// Converts one bounded tabular input to inline HTML or one CSV attachment, then persists the
/// Email delivery intent through the shared durable outbox.
/// </summary>
public sealed class EmailTargetNodeExecutor(
    WorkflowNodeSettingsValidator settingsValidator,
    IWorkflowStepRunLocator stepRunLocator,
    EmailActionOutboxService outboxService) : IWorkflowNodeExecutor
{
    internal const int MaximumRows = 10_000;
    private const string DataPlaceholder = "{{data}}";
    private const string DataMarker = "__THUB_EMAIL_TARGET_DATA_8E15263F__";

    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Target(
            WorkflowNodeKind.EmailTarget,
            explicitlyIdempotent: true);

    public async ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var settings = (EmailTargetNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context);
        if (input.DataSet.RowCount > MaximumRows)
        {
            throw ResourceLimit(
                "execution.email-target.rows.limit",
                $"Email targets cannot contain more than {MaximumRows} rows.");
        }

        var stepRunId = await stepRunLocator.FindRunningStepIdAsync(
            context.WorkflowRunId,
            context.Node.Id,
            cancellationToken);
        if (stepRunId is null)
        {
            throw ExecutionFailure.ExternalSideEffect(
                "execution.email-target.step_missing",
                "The durable Email target step attempt was not found.");
        }

        var payload = settings.DeliveryMode == EmailTargetDeliveryMode.Inline
            ? await CreateInlinePayloadAsync(input.DataSet, cancellationToken)
            : await CreateAttachmentPayloadAsync(
                input.DataSet,
                settings.AttachmentFileName,
                cancellationToken);
        var message = RenderMessage(settings, context.WorkflowRunId, payload);
        var result = await outboxService.QueueAsync(
            new QueueEmailActionCommand(
                context.WorkflowRunId,
                stepRunId.Value,
                context.Node.Id,
                settings.ProfileId,
                settings.Recipients,
                settings.Subject,
                settings.Body,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["run.id"] = context.WorkflowRunId.ToString("D")
                },
                settings.MaximumAttempts,
                PreparedMessage: message),
            cancellationToken);
        if (!result.IsSuccess)
        {
            var problem = result.Problem;
            var category = result.Status switch
            {
                AlertResultStatus.ValidationFailed or AlertResultStatus.NotFound =>
                    ExecutionErrorCategory.Configuration,
                AlertResultStatus.Unavailable => ExecutionErrorCategory.Connectivity,
                _ => ExecutionErrorCategory.ExternalSideEffect
            };
            throw new WorkflowNodeExecutionException(new ExecutionError(
                problem?.Code ?? "execution.email-target.enqueue",
                category,
                problem?.Message ?? "The Email target delivery intent could not be persisted.",
                isRetryable: false));
        }

        await context.Progress.ReportAsync(
            new WorkflowNodeProgress(
                RowsRead: payload.Rows,
                RowsWritten: payload.Rows,
                BatchesProcessed: payload.Batches,
                BytesRead: input.DataSet.ByteCount,
                BytesWritten: payload.Bytes),
            cancellationToken);
        return WorkflowNodeExecutionResult.WithoutOutput;
    }

    private static EmailMessage RenderMessage(
        EmailTargetNodeSettings settings,
        Guid workflowRunId,
        EmailTargetPayload payload)
    {
        var neutralBody = settings.DeliveryMode == EmailTargetDeliveryMode.Inline
            ? settings.Body.Replace(DataPlaceholder, DataMarker, StringComparison.Ordinal)
            : settings.Body;
        if (neutralBody.Contains(DataMarker, StringComparison.Ordinal)
            && settings.Body.Contains(DataMarker, StringComparison.Ordinal))
        {
            throw ExecutionFailure.Configuration(
                "execution.email-target.template.marker",
                "The Email target body contains a reserved template marker.");
        }

        try
        {
            var rendered = new EmailTemplate(settings.Subject, neutralBody).Render(
                settings.Recipients,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["run.id"] = workflowRunId.ToString("D")
                });
            var body = settings.DeliveryMode == EmailTargetDeliveryMode.Inline
                ? rendered.Body.Replace(DataMarker, payload.InlineHtml, StringComparison.Ordinal)
                : rendered.Body;
            return new EmailMessage(
                rendered.Recipients,
                rendered.Subject,
                body,
                isBodyHtml: true,
                payload.Attachment);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw ExecutionFailure.Configuration(
                "execution.email-target.template.invalid",
                "The Email target template is invalid or exceeds Email message limits.",
                exception);
        }
    }

    private static async Task<EmailTargetPayload> CreateInlinePayloadAsync(
        ITabularDataSet dataSet,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder("<table><thead><tr>");
        foreach (var column in dataSet.Schema.Columns)
        {
            builder.Append("<th>")
                .Append(WebUtility.HtmlEncode(column.Name))
                .Append("</th>");
        }
        builder.Append("</tr></thead><tbody>");

        long rows = 0;
        long batches = 0;
        await foreach (var batch in dataSet.ReadBatchesAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            await using (batch.ConfigureAwait(false))
            {
                batches++;
                foreach (var row in batch.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rows++;
                    EnsureRowLimit(rows);
                    builder.Append("<tr>");
                    foreach (var value in row.Values)
                    {
                        builder.Append("<td>")
                            .Append(WebUtility.HtmlEncode(FormatValue(value)))
                            .Append("</td>");
                    }
                    builder.Append("</tr>");
                    if (builder.Length > EmailDeliveryLimits.AbsoluteMaximumBodyLength)
                    {
                        throw ResourceLimit(
                            "execution.email-target.inline.limit",
                            "The inline Email data exceeds the supported message-body limit.");
                    }
                }
            }
        }

        builder.Append("</tbody></table>");
        if (builder.Length > EmailDeliveryLimits.AbsoluteMaximumBodyLength)
        {
            throw ResourceLimit(
                "execution.email-target.inline.limit",
                "The inline Email data exceeds the supported message-body limit.");
        }

        var html = builder.ToString();
        return new EmailTargetPayload(
            html,
            Attachment: null,
            rows,
            batches,
            Encoding.UTF8.GetByteCount(html));
    }

    private static async Task<EmailTargetPayload> CreateAttachmentPayloadAsync(
        ITabularDataSet dataSet,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            bufferSize: 4_096,
            leaveOpen: true);
        WriteCsvRow(writer, dataSet.Schema.Columns.Select(column => column.Name));

        long rows = 0;
        long batches = 0;
        await foreach (var batch in dataSet.ReadBatchesAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            await using (batch.ConfigureAwait(false))
            {
                batches++;
                foreach (var row in batch.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rows++;
                    EnsureRowLimit(rows);
                    WriteCsvRow(writer, row.Values.Select(FormatValue));
                    writer.Flush();
                    if (stream.Length > EmailAttachment.AbsoluteMaximumBytes)
                    {
                        throw ResourceLimit(
                            "execution.email-target.attachment.limit",
                            "The Email CSV attachment exceeds the supported size limit.");
                    }
                }
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        var content = stream.ToArray();
        var attachment = new EmailAttachment(fileName, "text/csv; charset=utf-8", content);
        return new EmailTargetPayload(
            InlineHtml: string.Empty,
            attachment,
            rows,
            batches,
            content.LongLength);
    }

    private static void WriteCsvRow(TextWriter writer, IEnumerable<string> values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                writer.Write(',');
            }
            first = false;
            if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            {
                writer.Write('"');
                writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal));
                writer.Write('"');
            }
            else
            {
                writer.Write(value);
            }
        }
        writer.Write("\r\n");
    }

    private static string FormatValue(TabularValue value) => value.Kind switch
    {
        TabularValueKind.Null => string.Empty,
        TabularValueKind.Boolean => ((bool)value.Value!).ToString(CultureInfo.InvariantCulture),
        TabularValueKind.Int64 => ((long)value.Value!).ToString(CultureInfo.InvariantCulture),
        TabularValueKind.Decimal => ((decimal)value.Value!).ToString(CultureInfo.InvariantCulture),
        TabularValueKind.Double => ((double)value.Value!).ToString("R", CultureInfo.InvariantCulture),
        TabularValueKind.String => (string)value.Value!,
        TabularValueKind.DateTimeOffset =>
            ((DateTimeOffset)value.Value!).ToString("O", CultureInfo.InvariantCulture),
        TabularValueKind.Guid => ((Guid)value.Value!).ToString("D"),
        TabularValueKind.Binary => Convert.ToBase64String(((ReadOnlyMemory<byte>)value.Value!).Span),
        _ => throw ExecutionFailure.Data(
            "execution.email-target.value.unsupported",
            "The Email target received an unsupported tabular value.")
    };

    private static void EnsureRowLimit(long rows)
    {
        if (rows > MaximumRows)
        {
            throw ResourceLimit(
                "execution.email-target.rows.limit",
                $"Email targets cannot contain more than {MaximumRows} rows.");
        }
    }

    private static WorkflowNodeExecutionException ResourceLimit(string code, string summary) =>
        new(new ExecutionError(
            code,
            ExecutionErrorCategory.ResourceLimit,
            summary,
            isRetryable: false));

    private sealed record EmailTargetPayload(
        string InlineHtml,
        EmailAttachment? Attachment,
        long Rows,
        long Batches,
        long Bytes);
}
