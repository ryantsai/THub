using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace THub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "thub");

            migrationBuilder.CreateTable(
                name: "AccessRoles",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SystemRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditRecords",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorIdentifier = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ResourceIdentifier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CorrelationIdentifier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Connections",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connections", x => x.Id);
                });

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
                    AllowedRecipientDomainsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
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
                name: "EncryptedConnectionCredentials",
                schema: "thub",
                columns: table => new
                {
                    SecretReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Nonce = table.Column<byte[]>(type: "binary(12)", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "varbinary(max)", maxLength: 32768, nullable: false),
                    AuthenticationTag = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncryptedConnectionCredentials", x => x.SecretReference);
                });

            migrationBuilder.CreateTable(
                name: "TrustedActions",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CredentialReference = table.Column<string>(type: "nvarchar(185)", maxLength: 185, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrustedActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessResourceGrants",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessResourceGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessResourceGrants_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "thub",
                        principalTable: "AccessRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessRoleAssignments",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrincipalKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PrincipalName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedPrincipalName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessRoleAssignments_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "thub",
                        principalTable: "AccessRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessRolePermissions",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessRolePermissions_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "thub",
                        principalTable: "AccessRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    WorkflowNodeId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
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
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanView = table.Column<bool>(type: "bit", nullable: false),
                    CanInsert = table.Column<bool>(type: "bit", nullable: false),
                    CanUpdate = table.Column<bool>(type: "bit", nullable: false),
                    CanDelete = table.Column<bool>(type: "bit", nullable: false),
                    CanApprove = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationGrants_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "thub",
                        principalTable: "AccessRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    ApplyConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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
                        name: "FK_PublicationVersions_Connections_ApplyConnectionId",
                        column: x => x.ApplyConnectionId,
                        principalSchema: "thub",
                        principalTable: "Connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    RecipientsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
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
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRuns",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowVersion = table.Column<int>(type: "int", nullable: false),
                    RetryOfRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ScheduledForUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    QueuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationRequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationRequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ErrorJson = table.Column<string>(type: "nvarchar(2048)", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowRuns_WorkflowRuns_RetryOfRunId",
                        column: x => x.RetryOfRunId,
                        principalSchema: "thub",
                        principalTable: "WorkflowRuns",
                        principalColumn: "Id");
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
                name: "Workflows",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Owner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    DraftRevision = table.Column<int>(type: "int", nullable: false),
                    GraphJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedVersionNumber = table.Column<int>(type: "int", nullable: true),
                    CronExpression = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NextRunAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.Id);
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

            migrationBuilder.InsertData(
                schema: "thub",
                table: "AccessRoles",
                columns: new[] { "Id", "CreatedAtUtc", "CreatedBy", "Description", "Name", "SystemRole" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), new DateTimeOffset(new DateTime(2026, 7, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "initial-schema", "Unrestricted platform and resource access.", "System Administrator", "SystemAdministrator" },
                    { new Guid("10000000-0000-0000-0000-000000000002"), new DateTimeOffset(new DateTime(2026, 7, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "initial-schema", "Create, edit, publish, execute, and monitor workflows.", "Developer", "Developer" }
                });

            migrationBuilder.InsertData(
                schema: "thub",
                table: "AccessRolePermissions",
                columns: new[] { "Id", "Permission", "RoleId" },
                values: new object[,]
                {
                    { new Guid("11000000-0000-0000-0000-000000000001"), "workflow.view", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("11000000-0000-0000-0000-000000000002"), "workflow.create", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("11000000-0000-0000-0000-000000000003"), "workflow.edit", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("11000000-0000-0000-0000-000000000004"), "workflow.publish", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("11000000-0000-0000-0000-000000000005"), "workflow.execute", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("11000000-0000-0000-0000-000000000006"), "run.view", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("11000000-0000-0000-0000-000000000007"), "schedule.manage", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("11000000-0000-0000-0000-000000000008"), "connection.view", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("11000000-0000-0000-0000-000000000009"), "workflow.delete", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("11000000-0000-0000-0000-000000000010"), "workflow.target.upsert", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("11000000-0000-0000-0000-000000000011"), "workflow.target.delete", new Guid("10000000-0000-0000-0000-000000000002") }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessResourceGrants_ResourceKind_ResourceId_Permission",
                schema: "thub",
                table: "AccessResourceGrants",
                columns: new[] { "ResourceKind", "ResourceId", "Permission" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessResourceGrants_RoleId_ResourceKind_ResourceId_Permission",
                schema: "thub",
                table: "AccessResourceGrants",
                columns: new[] { "RoleId", "ResourceKind", "ResourceId", "Permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoleAssignments_RoleId_PrincipalKind_NormalizedPrincipalName",
                schema: "thub",
                table: "AccessRoleAssignments",
                columns: new[] { "RoleId", "PrincipalKind", "NormalizedPrincipalName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRolePermissions_RoleId_Permission",
                schema: "thub",
                table: "AccessRolePermissions",
                columns: new[] { "RoleId", "Permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoles_Name",
                schema: "thub",
                table: "AccessRoles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoles_SystemRole",
                schema: "thub",
                table: "AccessRoles",
                column: "SystemRole",
                unique: true,
                filter: "[SystemRole] IS NOT NULL");

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
                name: "IX_AuditRecords_Action_OccurredAtUtc",
                schema: "thub",
                table: "AuditRecords",
                columns: new[] { "Action", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_ActorIdentifier_OccurredAtUtc",
                schema: "thub",
                table: "AuditRecords",
                columns: new[] { "ActorIdentifier", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_OccurredAtUtc",
                schema: "thub",
                table: "AuditRecords",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_ResourceType_ResourceIdentifier_OccurredAtUtc",
                schema: "thub",
                table: "AuditRecords",
                columns: new[] { "ResourceType", "ResourceIdentifier", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Connections_Kind_IsEnabled",
                schema: "thub",
                table: "Connections",
                columns: new[] { "Kind", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_Connections_Name",
                schema: "thub",
                table: "Connections",
                column: "Name",
                unique: true,
                filter: "[DeletedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmailDeliveryProfiles_Name",
                schema: "thub",
                table: "EmailDeliveryProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EncryptedConnectionCredentials_UpdatedAtUtc",
                schema: "thub",
                table: "EncryptedConnectionCredentials",
                column: "UpdatedAtUtc");

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
                name: "IX_PublicationGrants_PublicationId_RoleId",
                schema: "thub",
                table: "PublicationGrants",
                columns: new[] { "PublicationId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationGrants_RoleId",
                schema: "thub",
                table: "PublicationGrants",
                column: "RoleId");

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
                name: "IX_PublicationVersions_ApplyConnectionId",
                schema: "thub",
                table: "PublicationVersions",
                column: "ApplyConnectionId");

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
                name: "IX_TrustedActions_Kind_IsEnabled",
                schema: "thub",
                table: "TrustedActions",
                columns: new[] { "Kind", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_TrustedActions_Name",
                schema: "thub",
                table: "TrustedActions",
                column: "Name",
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
                name: "IX_WorkflowRuns_Status_QueuedAtUtc",
                schema: "thub",
                table: "WorkflowRuns",
                columns: new[] { "Status", "QueuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_WorkflowId_WorkflowVersion_ScheduledForUtc",
                schema: "thub",
                table: "WorkflowRuns",
                columns: new[] { "WorkflowId", "WorkflowVersion", "ScheduledForUtc" },
                unique: true,
                filter: "[ScheduledForUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_WorkflowVersionId",
                schema: "thub",
                table: "WorkflowRuns",
                column: "WorkflowVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_Name",
                schema: "thub",
                table: "Workflows",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_PublishedVersionId",
                schema: "thub",
                table: "Workflows",
                column: "PublishedVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_Status_NextRunAtUtc",
                schema: "thub",
                table: "Workflows",
                columns: new[] { "Status", "NextRunAtUtc" });

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
                name: "FK_AlertDeliveries_WorkflowAlertRules_WorkflowAlertRuleId",
                schema: "thub",
                table: "AlertDeliveries",
                column: "WorkflowAlertRuleId",
                principalSchema: "thub",
                principalTable: "WorkflowAlertRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_AlertDeliveries_WorkflowRuns_WorkflowRunId",
                schema: "thub",
                table: "AlertDeliveries",
                column: "WorkflowRunId",
                principalSchema: "thub",
                principalTable: "WorkflowRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AlertDeliveries_WorkflowStepRuns_WorkflowStepRunId",
                schema: "thub",
                table: "AlertDeliveries",
                column: "WorkflowStepRunId",
                principalSchema: "thub",
                principalTable: "WorkflowStepRuns",
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

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowAlertRules_Workflows_WorkflowId",
                schema: "thub",
                table: "WorkflowAlertRules",
                column: "WorkflowId",
                principalSchema: "thub",
                principalTable: "Workflows",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

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
                name: "FK_WorkflowRuns_Workflows_WorkflowId",
                schema: "thub",
                table: "WorkflowRuns",
                column: "WorkflowId",
                principalSchema: "thub",
                principalTable: "Workflows",
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

            InitialSchemaDatabaseObjects.Create(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            InitialSchemaDatabaseObjects.Drop(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationVersions_Publications_PublicationId",
                schema: "thub",
                table: "PublicationVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowVersions_Workflows_WorkflowId",
                schema: "thub",
                table: "WorkflowVersions");

            migrationBuilder.DropTable(
                name: "AccessResourceGrants",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "AccessRoleAssignments",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "AccessRolePermissions",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "AlertDeliveries",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "AuditRecords",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "EncryptedConnectionCredentials",
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
                name: "TrustedActions",
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
                name: "AccessRoles",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "EmailDeliveryProfiles",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "WorkflowRuns",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "Publications",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "PublicationVersions",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "Connections",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "Workflows",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "WorkflowVersions",
                schema: "thub");
        }
    }
}
