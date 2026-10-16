using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations;

public partial class Stage07NativeReminderDelivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Retain reminder_delivery_attempts unchanged as legacy Web Push audit.
        // A partially accepted legacy broadcast must not be replayed as a native notification.
        migrationBuilder.Sql("""
            UPDATE reminders r SET "Status" = 'Triggered', "LeaseId" = NULL, "LeaseExpiresAt" = NULL
            WHERE r."Status" IN ('Scheduled', 'Processing') AND
                (r."AcknowledgedAt" IS NOT NULL OR EXISTS (
                    SELECT 1 FROM reminder_delivery_attempts a WHERE a."ReminderId" = r."Id" AND a."AcceptedAt" IS NOT NULL));
            UPDATE reminders SET "Status" = 'Scheduled', "LeaseId" = NULL, "LeaseExpiresAt" = NULL
            WHERE "Status" = 'Processing';
            """);
        migrationBuilder.CreateTable(
            name: "reminder_node_delivery_attempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ReminderId = table.Column<Guid>(type: "uuid", nullable: false),
                NodeId = table.Column<Guid>(type: "uuid", nullable: true),
                CommandId = table.Column<Guid>(type: "uuid", nullable: true),
                CommandExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Attempt = table.Column<int>(type: "integer", nullable: false),
                AttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Result = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                Transport = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                OutcomeAmbiguous = table.Column<bool>(type: "boolean", nullable: false),
                RetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_reminder_node_delivery_attempts", x => x.Id);
                table.ForeignKey("FK_reminder_node_delivery_attempts_reminders_ReminderId", x => x.ReminderId, "reminders", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_reminder_node_delivery_attempts_aegis_nodes_NodeId", x => x.NodeId, "aegis_nodes", "Id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex("IX_reminder_node_delivery_attempts_ReminderId_Attempt", "reminder_node_delivery_attempts", new[] { "ReminderId", "Attempt" }, unique: true);
        migrationBuilder.CreateIndex("IX_reminder_node_delivery_attempts_NodeId", "reminder_node_delivery_attempts", "NodeId");
        migrationBuilder.CreateIndex("IX_reminder_node_delivery_attempts_CommandId", "reminder_node_delivery_attempts", "CommandId");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Legacy table/audit is still present; normalized statuses intentionally stay terminal.
        migrationBuilder.DropTable("reminder_node_delivery_attempts");
    }
}
