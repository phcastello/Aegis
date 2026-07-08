using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailInboxFamiliar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_account_connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EmailAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    AccessTokenEncrypted = table.Column<string>(type: "text", nullable: false),
                    RefreshTokenEncrypted = table.Column<string>(type: "text", nullable: true),
                    AccessTokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Scopes = table.Column<string>(type: "text", nullable: false),
                    DisconnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_account_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "email_action_audits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EmailIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    UserConfirmationMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_action_audits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_email_action_audits_chat_messages_UserConfirmationMessageId",
                        column: x => x.UserConfirmationMessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_email_action_audits_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pending_email_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EmailIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    HumanSummary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_email_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pending_email_actions_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_account_connections_Provider_DisconnectedAt",
                table: "email_account_connections",
                columns: new[] { "Provider", "DisconnectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_email_action_audits_ActionType",
                table: "email_action_audits",
                column: "ActionType");

            migrationBuilder.CreateIndex(
                name: "IX_email_action_audits_ConversationId",
                table: "email_action_audits",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_email_action_audits_CreatedAt",
                table: "email_action_audits",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_email_action_audits_Success",
                table: "email_action_audits",
                column: "Success");

            migrationBuilder.CreateIndex(
                name: "IX_email_action_audits_UserConfirmationMessageId",
                table: "email_action_audits",
                column: "UserConfirmationMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_pending_email_actions_ActionType",
                table: "pending_email_actions",
                column: "ActionType");

            migrationBuilder.CreateIndex(
                name: "IX_pending_email_actions_ConversationId_ExpiresAt",
                table: "pending_email_actions",
                columns: new[] { "ConversationId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_account_connections");

            migrationBuilder.DropTable(
                name: "email_action_audits");

            migrationBuilder.DropTable(
                name: "pending_email_actions");
        }
    }
}
