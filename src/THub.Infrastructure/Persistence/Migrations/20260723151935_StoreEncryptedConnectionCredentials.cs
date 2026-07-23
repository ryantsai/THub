using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreEncryptedConnectionCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EncryptedConnectionCredentials",
                schema: "thub",
                columns: table => new
                {
                    SecretReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    KeyVersion = table.Column<int>(type: "int", nullable: false),
                    Nonce = table.Column<byte[]>(type: "binary(12)", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "varbinary(max)", maxLength: 32768, nullable: false),
                    AuthenticationTag = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncryptedConnectionCredentials", x => x.SecretReference);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EncryptedConnectionCredentials_UpdatedAtUtc",
                schema: "thub",
                table: "EncryptedConnectionCredentials",
                column: "UpdatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EncryptedConnectionCredentials",
                schema: "thub");
        }
    }
}
