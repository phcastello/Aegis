using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SharedReminderIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SharedIncidentId",
                table: "reminder_interventions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "shared_reminder_incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ObserverNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subsystem = table.Column<string>(type: "text", nullable: false),
                    Cause = table.Column<string>(type: "text", nullable: false),
                    RequiredActionKey = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HealthySince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Diagnosis = table.Column<string>(type: "text", nullable: false),
                    RequiredAction = table.Column<string>(type: "text", nullable: false),
                    AffectedRuleCount = table.Column<int>(type: "integer", nullable: false),
                    ProblemSignature = table.Column<string>(type: "text", nullable: false),
                    GenerationAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_reminder_incidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_shared_reminder_incidents_chat_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_interventions_SharedIncidentId",
                table: "reminder_interventions",
                column: "SharedIncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_shared_reminder_incidents_GroupKey",
                table: "shared_reminder_incidents",
                column: "GroupKey",
                unique: true,
                filter: "\"ResolvedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_shared_reminder_incidents_MessageId",
                table: "shared_reminder_incidents",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_shared_reminder_incidents_State_NextAttemptAt",
                table: "shared_reminder_incidents",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_reminder_interventions_shared_reminder_incidents_SharedInci~",
                table: "reminder_interventions",
                column: "SharedIncidentId",
                principalTable: "shared_reminder_incidents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_reminder_interventions_shared_reminder_incidents_SharedInci~",
                table: "reminder_interventions");

            migrationBuilder.DropTable(
                name: "shared_reminder_incidents");

            migrationBuilder.DropIndex(
                name: "IX_reminder_interventions_SharedIncidentId",
                table: "reminder_interventions");

            migrationBuilder.DropColumn(
                name: "SharedIncidentId",
                table: "reminder_interventions");
        }
    }
}
