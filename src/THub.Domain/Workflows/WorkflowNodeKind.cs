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
    Webhook,
    Executable,
    PublishRestApi,
    PublishDataEditor
}

