using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReminderInterventions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chat_messages_ConversationId",
                table: "chat_messages");

            migrationBuilder.AddColumn<int>(
                name: "ActivationEpoch",
                table: "geographic_reminder_rules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FailureSince",
                table: "geographic_reminder_rules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SyncStartedAt",
                table: "geographic_reminder_rules",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<long>(
                name: "LastProactiveSequence",
                table: "conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ReadProactiveSequence",
                table: "conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "IsProactive",
                table: "chat_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ProactiveSequence",
                table: "chat_messages",
                type: "bigint",
                nullable: true);

            // Existing pending rules keep their real activation age. No history is regenerated.
            migrationBuilder.Sql("UPDATE geographic_reminder_rules SET \"SyncStartedAt\" = \"UpdatedAt\", \"ActivationEpoch\" = 1");

            migrationBuilder.CreateTable(
                name: "proactive_notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Transport = table.Column<string>(type: "text", nullable: true),
                    LastResult = table.Column<string>(type: "text", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proactive_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_proactive_notifications_chat_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reminder_interventions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivationEpoch = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecoveryAttempts = table.Column<int>(type: "integer", nullable: false),
                    RecoveryResultsJson = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Diagnosis = table.Column<string>(type: "text", nullable: true),
                    RequiredAction = table.Column<string>(type: "text", nullable: true),
                    GenerationAttempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_interventions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reminder_interventions_chat_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_reminder_interventions_geographic_reminder_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "geographic_reminder_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_ConversationId_ProactiveSequence",
                table: "chat_messages",
                columns: new[] { "ConversationId", "ProactiveSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_proactive_notifications_MessageId_NodeId",
                table: "proactive_notifications",
                columns: new[] { "MessageId", "NodeId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_proactive_notifications_State_NextAttemptAt",
                table: "proactive_notifications",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_interventions_MessageId",
                table: "reminder_interventions",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_interventions_RuleId_ActivationEpoch",
                table: "reminder_interventions",
                columns: new[] { "RuleId", "ActivationEpoch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reminder_interventions_State_NextAttemptAt",
                table: "reminder_interventions",
                columns: new[] { "State", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "proactive_notifications");

            migrationBuilder.DropTable(
                name: "reminder_interventions");

            migrationBuilder.DropIndex(
                name: "IX_chat_messages_ConversationId_ProactiveSequence",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "ActivationEpoch",
                table: "geographic_reminder_rules");

            migrationBuilder.DropColumn(
                name: "FailureSince",
                table: "geographic_reminder_rules");

            migrationBuilder.DropColumn(
                name: "SyncStartedAt",
                table: "geographic_reminder_rules");

            migrationBuilder.DropColumn(
                name: "LastProactiveSequence",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "ReadProactiveSequence",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "IsProactive",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "ProactiveSequence",
                table: "chat_messages");

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_ConversationId",
                table: "chat_messages",
                column: "ConversationId");
        }
    }
}
