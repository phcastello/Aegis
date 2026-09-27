using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingActionSupersession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAt",
                table: "pending_email_actions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededById",
                table: "pending_email_actions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAt",
                table: "pending_calendar_actions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededById",
                table: "pending_calendar_actions",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupersededAt",
                table: "pending_email_actions");

            migrationBuilder.DropColumn(
                name: "SupersededById",
                table: "pending_email_actions");

            migrationBuilder.DropColumn(
                name: "SupersededAt",
                table: "pending_calendar_actions");

            migrationBuilder.DropColumn(
                name: "SupersededById",
                table: "pending_calendar_actions");
        }
    }
}
