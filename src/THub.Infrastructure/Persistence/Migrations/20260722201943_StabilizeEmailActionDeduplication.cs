using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StabilizeEmailActionDeduplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkflowNodeId",
                schema: "thub",
                table: "AlertDeliveries",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE delivery
                SET delivery.[WorkflowNodeId] = stepRun.[NodeId]
                FROM [thub].[AlertDeliveries] AS delivery
                INNER JOIN [thub].[WorkflowStepRuns] AS stepRun
                    ON stepRun.[Id] = delivery.[WorkflowStepRunId]
                WHERE delivery.[Source] = N'EmailAction';

                ;WITH ranked AS
                (
                    SELECT
                        delivery.[Id],
                        ROW_NUMBER() OVER
                        (
                            PARTITION BY delivery.[WorkflowRunId], delivery.[WorkflowNodeId]
                            ORDER BY delivery.[CreatedAtUtc], delivery.[Id]
                        ) AS [Ordinal]
                    FROM [thub].[AlertDeliveries] AS delivery
                    WHERE delivery.[Source] = N'EmailAction'
                        AND delivery.[WorkflowNodeId] IS NOT NULL
                )
                UPDATE delivery
                SET delivery.[DeduplicationKey] =
                    N'action:run:'
                    + LOWER(REPLACE(CONVERT(nvarchar(36), delivery.[WorkflowRunId]), N'-', N''))
                    + N':node:'
                    + delivery.[WorkflowNodeId]
                FROM [thub].[AlertDeliveries] AS delivery
                INNER JOIN ranked ON ranked.[Id] = delivery.[Id]
                WHERE ranked.[Ordinal] = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkflowNodeId",
                schema: "thub",
                table: "AlertDeliveries");
        }
    }
}
