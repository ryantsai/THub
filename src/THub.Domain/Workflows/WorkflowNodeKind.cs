namespace THub.Domain.Workflows;

public enum WorkflowNodeKind
{
    SqlSource,
    MySqlSource,
    PostgreSqlSource,
    OracleSource,
    FtpSource,
    CsvSource,
    ExcelSource,
    SelectColumns,
    FilterRows,
    Join,
    SqlTarget,
    MySqlTarget,
    PostgreSqlTarget,
    OracleTarget,
    FtpTarget,
    CsvTarget,
    ExcelTarget,
    EmailTarget,
    EmailAlert,
    Webhook,
    Executable,
    PublishRestApi,
    PublishDataEditor
}
