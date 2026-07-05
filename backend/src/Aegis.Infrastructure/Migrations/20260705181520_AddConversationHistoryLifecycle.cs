using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationHistoryLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TitleGeneratedAt",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TitleSource",
                table: "conversations",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE conversations
                SET "TitleSource" = 'default'
                WHERE "TitleSource" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_conversations_DeletedAt_UpdatedAt_Id",
                table: "conversations",
                columns: new[] { "DeletedAt", "UpdatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_conversations_DeletedAt_UpdatedAt_Id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "TitleGeneratedAt",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "TitleSource",
                table: "conversations");
        }
    }
}
