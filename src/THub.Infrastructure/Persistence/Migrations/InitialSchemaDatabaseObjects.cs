using Microsoft.EntityFrameworkCore.Migrations;

namespace THub.Infrastructure.Persistence.Migrations;

internal static class InitialSchemaDatabaseObjects
{
    public static void Create(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("quartz");
        migrationBuilder.Sql(
            """
            CREATE TABLE [quartz].[QRTZ_JOB_DETAILS] (
                [SCHED_NAME] nvarchar(120) NOT NULL,
                [JOB_NAME] nvarchar(150) NOT NULL,
                [JOB_GROUP] nvarchar(150) NOT NULL,
                [DESCRIPTION] nvarchar(250) NULL,
                [JOB_CLASS_NAME] nvarchar(250) NOT NULL,
                [IS_DURABLE] bit NOT NULL,
                [IS_NONCONCURRENT] bit NOT NULL,
                [IS_UPDATE_DATA] bit NOT NULL,
                [REQUESTS_RECOVERY] bit NOT NULL,
                [JOB_DATA] varbinary(max) NULL,
                CONSTRAINT [PK_QRTZ_JOB_DETAILS] PRIMARY KEY CLUSTERED
                    ([SCHED_NAME], [JOB_NAME], [JOB_GROUP])
            );

            CREATE TABLE [quartz].[QRTZ_TRIGGERS] (
                [SCHED_NAME] nvarchar(120) NOT NULL,
                [TRIGGER_NAME] nvarchar(150) NOT NULL,
                [TRIGGER_GROUP] nvarchar(150) NOT NULL,
                [JOB_NAME] nvarchar(150) NOT NULL,
                [JOB_GROUP] nvarchar(150) NOT NULL,
                [DESCRIPTION] nvarchar(250) NULL,
                [NEXT_FIRE_TIME] bigint NULL,
                [PREV_FIRE_TIME] bigint NULL,
                [PRIORITY] int NULL,
                [TRIGGER_STATE] nvarchar(16) NOT NULL,
                [TRIGGER_TYPE] nvarchar(8) NOT NULL,
                [START_TIME] bigint NOT NULL,
                [END_TIME] bigint NULL,
                [CALENDAR_NAME] nvarchar(200) NULL,
                [MISFIRE_INSTR] int NULL,
                [MISFIRE_ORIG_FIRE_TIME] bigint NULL,
                [EXECUTION_GROUP] nvarchar(200) NULL,
                [JOB_DATA] varbinary(max) NULL,
                CONSTRAINT [PK_QRTZ_TRIGGERS] PRIMARY KEY CLUSTERED
                    ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]),
                CONSTRAINT [FK_QRTZ_TRIGGERS_QRTZ_JOB_DETAILS] FOREIGN KEY
                    ([SCHED_NAME], [JOB_NAME], [JOB_GROUP])
                    REFERENCES [quartz].[QRTZ_JOB_DETAILS]
                    ([SCHED_NAME], [JOB_NAME], [JOB_GROUP])
            );

            CREATE TABLE [quartz].[QRTZ_CRON_TRIGGERS] (
                [SCHED_NAME] nvarchar(120) NOT NULL,
                [TRIGGER_NAME] nvarchar(150) NOT NULL,
                [TRIGGER_GROUP] nvarchar(150) NOT NULL,
                [CRON_EXPRESSION] nvarchar(120) NOT NULL,
                [TIME_ZONE_ID] nvarchar(80) NULL,
                CONSTRAINT [PK_QRTZ_CRON_TRIGGERS] PRIMARY KEY CLUSTERED
                    ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]),
                CONSTRAINT [FK_QRTZ_CRON_TRIGGERS_QRTZ_TRIGGERS] FOREIGN KEY
                    ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP])
                    REFERENCES [quartz].[QRTZ_TRIGGERS]
                    ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]) ON DELETE CASCADE
            );

            CREATE TABLE [quartz].[QRTZ_SIMPLE_TRIGGERS] (
                [SCHED_NAME] nvarchar(120) NOT NULL,
                [TRIGGER_NAME] nvarchar(150) NOT NULL,
                [TRIGGER_GROUP] nvarchar(150) NOT NULL,
                [REPEAT_COUNT] int NOT NULL,
                [REPEAT_INTERVAL] bigint NOT NULL,
                [TIMES_TRIGGERED] int NOT NULL,
                CONSTRAINT [PK_QRTZ_SIMPLE_TRIGGERS] PRIMARY KEY CLUSTERED
                    ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]),
                CONSTRAINT [FK_QRTZ_SIMPLE_TRIGGERS_QRTZ_TRIGGERS] FOREIGN KEY
                    ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP])
                    REFERENCES [quartz].[QRTZ_TRIGGERS]
                    ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]) ON DELETE CASCADE
            );

            CREATE TABLE [quartz].[QRTZ_SIMPROP_TRIGGERS] (
                [SCHED_NAME] nvarchar(120) NOT NULL,
                [TRIGGER_NAME] nvarchar(150) NOT NULL,
                [TRIGGER_GROUP] nvarchar(150) NOT NULL,
                [STR_PROP_1] nvarchar(512) NULL,
                [STR_PROP_2] nvarchar(512) NULL,
                [STR_PROP_3] nvarchar(512) NULL,
                [INT_PROP_1] int NULL,
                [INT_PROP_2] int NULL,
                [LONG_PROP_1] bigint NULL,
                [LONG_PROP_2] bigint NULL,
                [DEC_PROP_1] numeric(13,4) NULL,
                [DEC_PROP_2] numeric(13,4) NULL,
                [BOOL_PROP_1] bit NULL,
                [BOOL_PROP_2] bit NULL,
                [TIME_ZONE_ID] nvarchar(80) NULL,
                CONSTRAINT [PK_QRTZ_SIMPROP_TRIGGERS] PRIMARY KEY CLUSTERED
                    ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]),
                CONSTRAINT [FK_QRTZ_SIMPROP_TRIGGERS_QRTZ_TRIGGERS] FOREIGN KEY
                    ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP])
                    REFERENCES [quartz].[QRTZ_TRIGGERS]
                    ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]) ON DELETE CASCADE
            );

            CREATE TABLE [quartz].[QRTZ_BLOB_TRIGGERS] (
                [SCHED_NAME] nvarchar(120) NOT NULL,
                [TRIGGER_NAME] nvarchar(150) NOT NULL,
                [TRIGGER_GROUP] nvarchar(150) NOT NULL,
                [BLOB_DATA] varbinary(max) NULL,
                CONSTRAINT [PK_QRTZ_BLOB_TRIGGERS] PRIMARY KEY CLUSTERED
                    ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP])
            );

            CREATE TABLE [quartz].[QRTZ_CALENDARS] (
                [SCHED_NAME] nvarchar(120) NOT NULL,
                [CALENDAR_NAME] nvarchar(200) NOT NULL,
                [CALENDAR] varbinary(max) NOT NULL,
                CONSTRAINT [PK_QRTZ_CALENDARS] PRIMARY KEY CLUSTERED
                    ([SCHED_NAME], [CALENDAR_NAME])
            );

            CREATE TABLE [quartz].[QRTZ_FIRED_TRIGGERS] (
                [SCHED_NAME] nvarchar(120) NOT NULL,
                [ENTRY_ID] nvarchar(140) NOT NULL,
                [TRIGGER_NAME] nvarchar(150) NOT NULL,
                [TRIGGER_GROUP] nvarchar(150) NOT NULL,
                [INSTANCE_NAME] nvarchar(200) NOT NULL,
                [FIRED_TIME] bigint NOT NULL,
                [SCHED_TIME] bigint NOT NULL,
                [PRIORITY] int NOT NULL,
                [STATE] nvarchar(16) NOT NULL,
                [JOB_NAME] nvarchar(150) NULL,
                [JOB_GROUP] nvarchar(150) NULL,
                [IS_NONCONCURRENT] bit NULL,
                [REQUESTS_RECOVERY] bit NULL,
                [EXECUTION_GROUP] nvarchar(200) NULL,
                CONSTRAINT [PK_QRTZ_FIRED_TRIGGERS] PRIMARY KEY CLUSTERED
                    ([SCHED_NAME], [ENTRY_ID])
            );

            CREATE TABLE [quartz].[QRTZ_PAUSED_TRIGGER_GRPS] (
                [SCHED_NAME] nvarchar(120) NOT NULL,
                [TRIGGER_GROUP] nvarchar(150) NOT NULL,
                CONSTRAINT [PK_QRTZ_PAUSED_TRIGGER_GRPS] PRIMARY KEY CLUSTERED
                    ([SCHED_NAME], [TRIGGER_GROUP])
            );

            CREATE TABLE [quartz].[QRTZ_SCHEDULER_STATE] (
                [SCHED_NAME] nvarchar(120) NOT NULL,
                [INSTANCE_NAME] nvarchar(200) NOT NULL,
                [LAST_CHECKIN_TIME] bigint NOT NULL,
                [CHECKIN_INTERVAL] bigint NOT NULL,
                CONSTRAINT [PK_QRTZ_SCHEDULER_STATE] PRIMARY KEY CLUSTERED
                    ([SCHED_NAME], [INSTANCE_NAME])
            );

            CREATE TABLE [quartz].[QRTZ_LOCKS] (
                [SCHED_NAME] nvarchar(120) NOT NULL,
                [LOCK_NAME] nvarchar(40) NOT NULL,
                CONSTRAINT [PK_QRTZ_LOCKS] PRIMARY KEY CLUSTERED
                    ([SCHED_NAME], [LOCK_NAME])
            );

            CREATE INDEX [IDX_QRTZ_T_G_J]
                ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [JOB_GROUP], [JOB_NAME]);
            CREATE INDEX [IDX_QRTZ_T_C]
                ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [CALENDAR_NAME]);
            CREATE INDEX [IDX_QRTZ_T_N_G_STATE]
                ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [TRIGGER_GROUP], [TRIGGER_STATE]);
            CREATE INDEX [IDX_QRTZ_T_STATE]
                ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [TRIGGER_STATE]);
            CREATE INDEX [IDX_QRTZ_T_N_STATE]
                ON [quartz].[QRTZ_TRIGGERS]
                ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP], [TRIGGER_STATE]);
            CREATE INDEX [IDX_QRTZ_T_NEXT_FIRE_TIME]
                ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [NEXT_FIRE_TIME]);
            CREATE INDEX [IDX_QRTZ_T_NFT_ST]
                ON [quartz].[QRTZ_TRIGGERS]
                ([SCHED_NAME], [TRIGGER_STATE], [NEXT_FIRE_TIME]);
            CREATE INDEX [IDX_QRTZ_T_NFT_ST_MISFIRE]
                ON [quartz].[QRTZ_TRIGGERS]
                ([SCHED_NAME], [MISFIRE_INSTR], [NEXT_FIRE_TIME], [TRIGGER_STATE]);
            CREATE INDEX [IDX_QRTZ_T_NFT_ST_MISFIRE_GRP]
                ON [quartz].[QRTZ_TRIGGERS]
                ([SCHED_NAME], [MISFIRE_INSTR], [NEXT_FIRE_TIME], [TRIGGER_GROUP], [TRIGGER_STATE]);
            CREATE INDEX [IDX_QRTZ_FT_INST_JOB_REQ_RCVRY]
                ON [quartz].[QRTZ_FIRED_TRIGGERS]
                ([SCHED_NAME], [INSTANCE_NAME], [REQUESTS_RECOVERY]);
            CREATE INDEX [IDX_QRTZ_FT_G_J]
                ON [quartz].[QRTZ_FIRED_TRIGGERS]
                ([SCHED_NAME], [JOB_GROUP], [JOB_NAME]);
            CREATE INDEX [IDX_QRTZ_FT_G_T]
                ON [quartz].[QRTZ_FIRED_TRIGGERS]
                ([SCHED_NAME], [TRIGGER_GROUP], [TRIGGER_NAME]);

            EXEC(N'
            CREATE TRIGGER [thub].[TR_AuditRecords_AppendOnly]
            ON [thub].[AuditRecords]
            INSTEAD OF UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                THROW 51000, ''Audit records are append-only.'', 1;
            END');
            """);
    }

    public static void Drop(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE [quartz].[QRTZ_CRON_TRIGGERS];
            DROP TABLE [quartz].[QRTZ_SIMPLE_TRIGGERS];
            DROP TABLE [quartz].[QRTZ_SIMPROP_TRIGGERS];
            DROP TABLE [quartz].[QRTZ_BLOB_TRIGGERS];
            DROP TABLE [quartz].[QRTZ_FIRED_TRIGGERS];
            DROP TABLE [quartz].[QRTZ_PAUSED_TRIGGER_GRPS];
            DROP TABLE [quartz].[QRTZ_SCHEDULER_STATE];
            DROP TABLE [quartz].[QRTZ_LOCKS];
            DROP TABLE [quartz].[QRTZ_CALENDARS];
            DROP TABLE [quartz].[QRTZ_TRIGGERS];
            DROP TABLE [quartz].[QRTZ_JOB_DETAILS];
            DROP SCHEMA [quartz];
            """);
    }
}
