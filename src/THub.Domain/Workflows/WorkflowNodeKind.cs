namespace THub.Domain.Workflows;

public enum WorkflowNodeKind
{
    SqlSource,
    CsvSource,
    ExcelSource,
    SelectColumns,
    FilterRows,
    Join,
    SqlTarget,
    CsvTarget,
    ExcelTarget,
    EmailAlert,
    Webhook,
    Executable,
    PublishRestApi,
    PublishDataEditor
}
