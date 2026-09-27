using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_action_audits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PendingActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EventId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    UserConfirmationMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_action_audits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_calendar_action_audits_chat_messages_UserConfirmationMessag~",
                        column: x => x.UserConfirmationMessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_calendar_action_audits_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pending_calendar_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CalendarId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EventId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    HumanSummary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MayHaveAppliedChanges = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_calendar_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pending_calendar_actions_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_action_audits_ActionType",
                table: "calendar_action_audits",
                column: "ActionType");

            migrationBuilder.CreateIndex(
                name: "IX_calendar_action_audits_ConversationId",
                table: "calendar_action_audits",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_calendar_action_audits_CreatedAt",
                table: "calendar_action_audits",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_calendar_action_audits_PendingActionId",
                table: "calendar_action_audits",
                column: "PendingActionId");

            migrationBuilder.CreateIndex(
                name: "IX_calendar_action_audits_Success",
                table: "calendar_action_audits",
                column: "Success");

            migrationBuilder.CreateIndex(
                name: "IX_calendar_action_audits_UserConfirmationMessageId",
                table: "calendar_action_audits",
                column: "UserConfirmationMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_pending_calendar_actions_ActionType",
                table: "pending_calendar_actions",
                column: "ActionType");

            migrationBuilder.CreateIndex(
                name: "IX_pending_calendar_actions_ConversationId_ExpiresAt",
                table: "pending_calendar_actions",
                columns: new[] { "ConversationId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_action_audits");

            migrationBuilder.DropTable(
                name: "pending_calendar_actions");
        }
    }
}
