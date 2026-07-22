using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableExecutionAlertsAndPublications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CronExpression",
                schema: "thub",
                table: "Workflows",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAtUtc",
                schema: "thub",
                table: "Workflows",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DraftRevision",
                schema: "thub",
                table: "Workflows",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedVersionId",
                schema: "thub",
                table: "Workflows",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublishedVersionNumber",
                schema: "thub",
                table: "Workflows",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "thub",
                table: "Workflows",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                schema: "thub",
                table: "WorkflowRuns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationRequestedAtUtc",
                schema: "thub",
                table: "WorkflowRuns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationRequestedBy",
                schema: "thub",
                table: "WorkflowRuns",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorJson",
                schema: "thub",
                table: "WorkflowRuns",
                type: "nvarchar(2048)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastHeartbeatAtUtc",
                schema: "thub",
                table: "WorkflowRuns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAtUtc",
                schema: "thub",
                table: "WorkflowRuns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                schema: "thub",
                table: "WorkflowRuns",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RetryOfRunId",
                schema: "thub",
                table: "WorkflowRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "thub",
                table: "WorkflowRuns",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowVersionId",
                schema: "thub",
                table: "WorkflowRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmailDeliveryProfiles",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SmtpHost = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    SmtpPort = table.Column<int>(type: "int", nullable: false),
                    TransportSecurity = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SenderAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CredentialSecretReference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AllowedRecipientDomainsJson = table.Column<string>(type: "nvarchar(4000)", nullable: false),
                    LimitsJson = table.Column<string>(type: "nvarchar(1000)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailDeliveryProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowStepRuns",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    QueuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowsRead = table.Column<long>(type: "bigint", nullable: false),
                    RowsWritten = table.Column<long>(type: "bigint", nullable: false),
                    BatchesProcessed = table.Column<long>(type: "bigint", nullable: false),
                    BytesRead = table.Column<long>(type: "bigint", nullable: false),
                    BytesWritten = table.Column<long>(type: "bigint", nullable: false),
                    ErrorJson = table.Column<string>(type: "nvarchar(2048)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowStepRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowStepRuns_WorkflowRuns_WorkflowRunId",
                        column: x => x.WorkflowRunId,
                        principalSchema: "thub",
                        principalTable: "WorkflowRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowVersions",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    GraphJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Checksum = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    PublishedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowVersions_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalSchema: "thub",
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                IF EXISTS
                (
                    SELECT 1
                    FROM [thub].[WorkflowRuns] AS run
                    INNER JOIN [thub].[Workflows] AS workflow
                        ON workflow.[Id] = run.[WorkflowId]
                    WHERE run.[WorkflowVersion] <> workflow.[Version]
                )
                BEGIN
                    THROW 51000,
                        'THub cannot reconstruct immutable snapshots for legacy runs whose version differs from the current workflow version. Preserve or remove those runs through an approved data-remediation process before retrying this migration.',
                        1;
                END;

                IF EXISTS
                (
                    SELECT 1
                    FROM [thub].[Workflows] AS workflow
                    WHERE (workflow.[Status] IN (N'Published', N'Paused')
                            OR EXISTS
                            (
                                SELECT 1
                                FROM [thub].[WorkflowRuns] AS run
                                WHERE run.[WorkflowId] = workflow.[Id]
                            ))
                        AND (workflow.[Version] <= 0
                            OR LEN(LTRIM(RTRIM(workflow.[Owner]))) = 0
                            OR LEN(workflow.[GraphJson]) > 2000000)
                )
                BEGIN
                    THROW 51001,
                        'THub found invalid legacy workflow metadata while creating immutable snapshots. Correct the workflow version, owner, or graph size before retrying this migration.',
                        1;
                END;

                IF EXISTS
                (
                    SELECT 1
                    FROM [thub].[Workflows] AS workflow
                    WHERE (workflow.[Status] IN (N'Published', N'Paused')
                            OR EXISTS
                            (
                                SELECT 1
                                FROM [thub].[WorkflowRuns] AS run
                                WHERE run.[WorkflowId] = workflow.[Id]
                            ))
                        AND
                        (
                            ISJSON(workflow.[GraphJson]) <> 1
                            OR LEFT(LTRIM(COALESCE(JSON_QUERY(
                                CASE WHEN ISJSON(workflow.[GraphJson]) = 1
                                    THEN workflow.[GraphJson]
                                    ELSE N'{}'
                                END,
                                '$.nodes'), N'')), 1) <> N'['
                            OR LEFT(LTRIM(COALESCE(JSON_QUERY(
                                CASE WHEN ISJSON(workflow.[GraphJson]) = 1
                                    THEN workflow.[GraphJson]
                                    ELSE N'{}'
                                END,
                                '$.edges'), N'')), 1) <> N'['
                            OR
                            EXISTS
                            (
                                SELECT 1
                                FROM OPENJSON(
                                    CASE WHEN ISJSON(workflow.[GraphJson]) = 1
                                        THEN workflow.[GraphJson]
                                        ELSE N'{}'
                                    END) AS property
                                WHERE property.[key] = N'schemaVersion'
                                    AND (property.[type] <> 2 OR property.[value] <> N'1')
                            )
                        )
                )
                BEGIN
                    THROW 51002,
                        'THub found a legacy workflow graph that cannot be represented as schema version 1. Correct the graph document before retrying this migration; no substitute graph was created.',
                        1;
                END;

                ;WITH VersionSnapshots AS
                (
                    SELECT
                        workflow.[Id] AS [WorkflowId],
                        workflow.[Version],
                        CASE
                            WHEN NOT EXISTS
                                (SELECT 1
                                 FROM OPENJSON(workflow.[GraphJson]) AS property
                                 WHERE property.[key] = N'schemaVersion')
                                THEN JSON_MODIFY(workflow.[GraphJson], '$.schemaVersion', 1)
                            ELSE workflow.[GraphJson]
                        END AS [GraphSnapshot],
                        workflow.[Owner],
                        workflow.[UpdatedAtUtc]
                    FROM [thub].[Workflows] AS workflow
                    WHERE workflow.[Status] IN (N'Published', N'Paused')
                        OR EXISTS
                        (
                            SELECT 1
                            FROM [thub].[WorkflowRuns] AS run
                            WHERE run.[WorkflowId] = workflow.[Id]
                        )
                )
                INSERT INTO [thub].[WorkflowVersions]
                    ([Id], [WorkflowId], [Version], [SchemaVersion], [GraphJson], [Checksum], [PublishedBy], [PublishedAtUtc])
                SELECT
                    CONVERT(uniqueidentifier, SUBSTRING(HASHBYTES(
                        'SHA2_256',
                        CONVERT(varchar(8000), CONCAT(
                            N'thub-workflow-version:',
                            LOWER(REPLACE(CONVERT(nvarchar(36), snapshot.[WorkflowId]), N'-', N'')),
                            N':',
                            CONVERT(nvarchar(11), snapshot.[Version]))
                            COLLATE Latin1_General_100_BIN2_UTF8)), 1, 16)),
                    snapshot.[WorkflowId],
                    snapshot.[Version],
                    1,
                    snapshot.[GraphSnapshot],
                    CONVERT(char(64), HASHBYTES(
                        'SHA2_256',
                        CONVERT(varchar(max), snapshot.[GraphSnapshot] COLLATE Latin1_General_100_BIN2_UTF8)), 2),
                    snapshot.[Owner],
                    snapshot.[UpdatedAtUtc]
                FROM VersionSnapshots AS snapshot;

                UPDATE run
                SET [WorkflowVersionId] = version.[Id]
                FROM [thub].[WorkflowRuns] AS run
                INNER JOIN [thub].[WorkflowVersions] AS version
                    ON version.[WorkflowId] = run.[WorkflowId]
                    AND version.[Version] = run.[WorkflowVersion];

                UPDATE workflow
                SET
                    [PublishedVersionId] = version.[Id],
                    [PublishedVersionNumber] = version.[Version]
                FROM [thub].[Workflows] AS workflow
                INNER JOIN [thub].[WorkflowVersions] AS version
                    ON version.[WorkflowId] = workflow.[Id]
                    AND version.[Version] = workflow.[Version]
                WHERE workflow.[Status] IN (N'Published', N'Paused');
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "WorkflowVersionId",
                schema: "thub",
                table: "WorkflowRuns",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "WorkflowAlertRules",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmailDeliveryProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Triggers = table.Column<int>(type: "int", nullable: false),
                    RecipientsJson = table.Column<string>(type: "nvarchar(4000)", nullable: false),
                    TemplateJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowAlertRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowAlertRules_EmailDeliveryProfiles_EmailDeliveryProfileId",
                        column: x => x.EmailDeliveryProfileId,
                        principalSchema: "thub",
                        principalTable: "EmailDeliveryProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowAlertRules_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalSchema: "thub",
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AlertDeliveries",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmailDeliveryProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Event = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    WorkflowAlertRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkflowStepRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    StableMessageId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MessageJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MaximumAttempts = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastErrorJson = table.Column<string>(type: "nvarchar(2048)", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertDeliveries_EmailDeliveryProfiles_EmailDeliveryProfileId",
                        column: x => x.EmailDeliveryProfileId,
                        principalSchema: "thub",
                        principalTable: "EmailDeliveryProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AlertDeliveries_WorkflowAlertRules_WorkflowAlertRuleId",
                        column: x => x.WorkflowAlertRuleId,
                        principalSchema: "thub",
                        principalTable: "WorkflowAlertRules",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AlertDeliveries_WorkflowRuns_WorkflowRunId",
                        column: x => x.WorkflowRunId,
                        principalSchema: "thub",
                        principalTable: "WorkflowRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AlertDeliveries_WorkflowStepRuns_WorkflowStepRunId",
                        column: x => x.WorkflowStepRunId,
                        principalSchema: "thub",
                        principalTable: "WorkflowStepRuns",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PublicationAccessTokens",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Selector = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Verifier = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    AlgorithmVersion = table.Column<int>(type: "int", nullable: false),
                    DisplayPrefix = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcceptedRequestCount = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationAccessTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicationChanges",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChangeSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    KeyJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicationChangeSets",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SubmittedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReviewedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewComment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ApplyStartedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ApplyStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OutcomeDetail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationChangeSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicationColumnForeignKeys",
                schema: "thub",
                columns: table => new
                {
                    PublicationColumnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ForeignKeyConstraintName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ForeignKeyOrdinal = table.Column<int>(type: "int", nullable: false),
                    ForeignKeyColumnCount = table.Column<int>(type: "int", nullable: false),
                    ForeignKeyReferencedSchema = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ForeignKeyReferencedObject = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ForeignKeyReferencedColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ForeignKeyDisplayColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ForeignKeyLookupMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ForeignKeySearchColumnsJson = table.Column<string>(type: "nvarchar(4000)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationColumnForeignKeys", x => x.PublicationColumnId);
                });

            migrationBuilder.CreateTable(
                name: "PublicationColumns",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PublicAlias = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceTypeName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IsNullable = table.Column<bool>(type: "bit", nullable: false),
                    IsReadable = table.Column<bool>(type: "bit", nullable: false),
                    IsFilterable = table.Column<bool>(type: "bit", nullable: false),
                    IsSortable = table.Column<bool>(type: "bit", nullable: false),
                    IsWritable = table.Column<bool>(type: "bit", nullable: false),
                    IsKey = table.Column<bool>(type: "bit", nullable: false),
                    KeyOrdinal = table.Column<int>(type: "int", nullable: true),
                    IsConcurrencyToken = table.Column<bool>(type: "bit", nullable: false),
                    IsGenerated = table.Column<bool>(type: "bit", nullable: false),
                    MaximumLength = table.Column<int>(type: "int", nullable: true),
                    NumericPrecision = table.Column<byte>(type: "tinyint", nullable: true),
                    NumericScale = table.Column<byte>(type: "tinyint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationColumns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicationGrants",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CanView = table.Column<bool>(type: "bit", nullable: false),
                    CanInsert = table.Column<bool>(type: "bit", nullable: false),
                    CanUpdate = table.Column<bool>(type: "bit", nullable: false),
                    CanDelete = table.Column<bool>(type: "bit", nullable: false),
                    CanApprove = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Publications",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActiveVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Publications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicationVersions",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSchema = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceObject = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceObjectKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SchemaFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ConcurrencyMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DefaultPageSize = table.Column<int>(type: "int", nullable: false),
                    MaximumPageSize = table.Column<int>(type: "int", nullable: false),
                    RequestsPerWindow = table.Column<int>(type: "int", nullable: false),
                    RateLimitWindowSeconds = table.Column<int>(type: "int", nullable: false),
                    MaximumConcurrentRequests = table.Column<int>(type: "int", nullable: false),
                    EditorWindowSize = table.Column<int>(type: "int", nullable: false),
                    RequestTimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    CommandTimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    MaximumResponseBytes = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationVersions_Connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalSchema: "thub",
                        principalTable: "Connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PublicationVersions_Publications_PublicationId",
                        column: x => x.PublicationId,
                        principalSchema: "thub",
                        principalTable: "Publications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_PublishedVersionId",
                schema: "thub",
                table: "Workflows",
                column: "PublishedVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_RetryOfRunId",
                schema: "thub",
                table: "WorkflowRuns",
                column: "RetryOfRunId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Status_LeaseExpiresAtUtc",
                schema: "thub",
                table: "WorkflowRuns",
                columns: new[] { "Status", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_WorkflowVersionId",
                schema: "thub",
                table: "WorkflowRuns",
                column: "WorkflowVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_DeduplicationKey",
                schema: "thub",
                table: "AlertDeliveries",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_EmailDeliveryProfileId",
                schema: "thub",
                table: "AlertDeliveries",
                column: "EmailDeliveryProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_Status_NextAttemptAtUtc_LeaseExpiresAtUtc",
                schema: "thub",
                table: "AlertDeliveries",
                columns: new[] { "Status", "NextAttemptAtUtc", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_WorkflowAlertRuleId",
                schema: "thub",
                table: "AlertDeliveries",
                column: "WorkflowAlertRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_WorkflowRunId",
                schema: "thub",
                table: "AlertDeliveries",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_WorkflowStepRunId",
                schema: "thub",
                table: "AlertDeliveries",
                column: "WorkflowStepRunId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailDeliveryProfiles_Name",
                schema: "thub",
                table: "EmailDeliveryProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationAccessTokens_PublicationId_Name",
                schema: "thub",
                table: "PublicationAccessTokens",
                columns: new[] { "PublicationId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationAccessTokens_PublicationId_RevokedAtUtc_ExpiresAtUtc",
                schema: "thub",
                table: "PublicationAccessTokens",
                columns: new[] { "PublicationId", "RevokedAtUtc", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationAccessTokens_Selector",
                schema: "thub",
                table: "PublicationAccessTokens",
                column: "Selector",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationChanges_ChangeSetId_Operation",
                schema: "thub",
                table: "PublicationChanges",
                columns: new[] { "ChangeSetId", "Operation" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationChangeSets_PublicationId",
                schema: "thub",
                table: "PublicationChangeSets",
                column: "PublicationId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationChangeSets_PublicationVersionId",
                schema: "thub",
                table: "PublicationChangeSets",
                column: "PublicationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationChangeSets_Status_UpdatedAtUtc",
                schema: "thub",
                table: "PublicationChangeSets",
                columns: new[] { "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationColumns_PublicationVersionId_KeyOrdinal",
                schema: "thub",
                table: "PublicationColumns",
                columns: new[] { "PublicationVersionId", "KeyOrdinal" },
                unique: true,
                filter: "[KeyOrdinal] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationColumns_PublicationVersionId_Ordinal",
                schema: "thub",
                table: "PublicationColumns",
                columns: new[] { "PublicationVersionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationColumns_PublicationVersionId_PublicAlias",
                schema: "thub",
                table: "PublicationColumns",
                columns: new[] { "PublicationVersionId", "PublicAlias" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationColumns_PublicationVersionId_SourceName",
                schema: "thub",
                table: "PublicationColumns",
                columns: new[] { "PublicationVersionId", "SourceName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationGrants_PublicationId_Role",
                schema: "thub",
                table: "PublicationGrants",
                columns: new[] { "PublicationId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Publications_ActiveVersionId",
                schema: "thub",
                table: "Publications",
                column: "ActiveVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Publications_Kind_State",
                schema: "thub",
                table: "Publications",
                columns: new[] { "Kind", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Publications_Slug",
                schema: "thub",
                table: "Publications",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationVersions_ConnectionId",
                schema: "thub",
                table: "PublicationVersions",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationVersions_ConnectionId_SourceSchema_SourceObject",
                schema: "thub",
                table: "PublicationVersions",
                columns: new[] { "ConnectionId", "SourceSchema", "SourceObject" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationVersions_PublicationId_VersionNumber",
                schema: "thub",
                table: "PublicationVersions",
                columns: new[] { "PublicationId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAlertRules_EmailDeliveryProfileId",
                schema: "thub",
                table: "WorkflowAlertRules",
                column: "EmailDeliveryProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAlertRules_WorkflowId_IsEnabled",
                schema: "thub",
                table: "WorkflowAlertRules",
                columns: new[] { "WorkflowId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowAlertRules_WorkflowId_Name",
                schema: "thub",
                table: "WorkflowAlertRules",
                columns: new[] { "WorkflowId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStepRuns_Status_QueuedAtUtc",
                schema: "thub",
                table: "WorkflowStepRuns",
                columns: new[] { "Status", "QueuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStepRuns_WorkflowRunId_NodeId_Attempt",
                schema: "thub",
                table: "WorkflowStepRuns",
                columns: new[] { "WorkflowRunId", "NodeId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowVersions_WorkflowId_Version",
                schema: "thub",
                table: "WorkflowVersions",
                columns: new[] { "WorkflowId", "Version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowRuns_WorkflowRuns_RetryOfRunId",
                schema: "thub",
                table: "WorkflowRuns",
                column: "RetryOfRunId",
                principalSchema: "thub",
                principalTable: "WorkflowRuns",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowRuns_WorkflowVersions_WorkflowVersionId",
                schema: "thub",
                table: "WorkflowRuns",
                column: "WorkflowVersionId",
                principalSchema: "thub",
                principalTable: "WorkflowVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Workflows_WorkflowVersions_PublishedVersionId",
                schema: "thub",
                table: "Workflows",
                column: "PublishedVersionId",
                principalSchema: "thub",
                principalTable: "WorkflowVersions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationAccessTokens_Publications_PublicationId",
                schema: "thub",
                table: "PublicationAccessTokens",
                column: "PublicationId",
                principalSchema: "thub",
                principalTable: "Publications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationChanges_PublicationChangeSets_ChangeSetId",
                schema: "thub",
                table: "PublicationChanges",
                column: "ChangeSetId",
                principalSchema: "thub",
                principalTable: "PublicationChangeSets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationChangeSets_PublicationVersions_PublicationVersionId",
                schema: "thub",
                table: "PublicationChangeSets",
                column: "PublicationVersionId",
                principalSchema: "thub",
                principalTable: "PublicationVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationChangeSets_Publications_PublicationId",
                schema: "thub",
                table: "PublicationChangeSets",
                column: "PublicationId",
                principalSchema: "thub",
                principalTable: "Publications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationColumnForeignKeys_PublicationColumns_PublicationColumnId",
                schema: "thub",
                table: "PublicationColumnForeignKeys",
                column: "PublicationColumnId",
                principalSchema: "thub",
                principalTable: "PublicationColumns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationColumns_PublicationVersions_PublicationVersionId",
                schema: "thub",
                table: "PublicationColumns",
                column: "PublicationVersionId",
                principalSchema: "thub",
                principalTable: "PublicationVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationGrants_Publications_PublicationId",
                schema: "thub",
                table: "PublicationGrants",
                column: "PublicationId",
                principalSchema: "thub",
                principalTable: "Publications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Publications_PublicationVersions_ActiveVersionId",
                schema: "thub",
                table: "Publications",
                column: "ActiveVersionId",
                principalSchema: "thub",
                principalTable: "PublicationVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowRuns_WorkflowRuns_RetryOfRunId",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowRuns_WorkflowVersions_WorkflowVersionId",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_Workflows_WorkflowVersions_PublishedVersionId",
                schema: "thub",
                table: "Workflows");

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationVersions_Publications_PublicationId",
                schema: "thub",
                table: "PublicationVersions");

            migrationBuilder.DropTable(
                name: "AlertDeliveries",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "PublicationAccessTokens",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "PublicationChanges",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "PublicationColumnForeignKeys",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "PublicationGrants",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "WorkflowVersions",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "WorkflowAlertRules",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "WorkflowStepRuns",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "PublicationChangeSets",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "PublicationColumns",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "EmailDeliveryProfiles",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "Publications",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "PublicationVersions",
                schema: "thub");

            migrationBuilder.DropIndex(
                name: "IX_Workflows_PublishedVersionId",
                schema: "thub",
                table: "Workflows");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_RetryOfRunId",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_Status_LeaseExpiresAtUtc",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_WorkflowVersionId",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                schema: "thub",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "DraftRevision",
                schema: "thub",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "PublishedVersionId",
                schema: "thub",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "PublishedVersionNumber",
                schema: "thub",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "thub",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedAtUtc",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedBy",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "ErrorJson",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAtUtc",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "RetryOfRunId",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "WorkflowVersionId",
                schema: "thub",
                table: "WorkflowRuns");

            migrationBuilder.AlterColumn<string>(
                name: "CronExpression",
                schema: "thub",
                table: "Workflows",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
