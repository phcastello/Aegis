using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReminderWithdrawalReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WithdrawalAttempts",
                table: "reminder_recipients",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WithdrawalCommandExpiresAt",
                table: "reminder_recipients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WithdrawalCommandId",
                table: "reminder_recipients",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WithdrawalNextAttemptAt",
                table: "reminder_recipients",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WithdrawalAttempts",
                table: "reminder_recipients");

            migrationBuilder.DropColumn(
                name: "WithdrawalCommandExpiresAt",
                table: "reminder_recipients");

            migrationBuilder.DropColumn(
                name: "WithdrawalCommandId",
                table: "reminder_recipients");

            migrationBuilder.DropColumn(
                name: "WithdrawalNextAttemptAt",
                table: "reminder_recipients");
        }
    }
}
