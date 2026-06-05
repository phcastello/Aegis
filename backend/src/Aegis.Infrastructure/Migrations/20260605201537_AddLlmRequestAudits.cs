using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmRequestAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_request_audits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssistantMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    RequestPayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: true),
                    ResponseBody = table.Column<string>(type: "text", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    ErrorType = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_request_audits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_llm_request_audits_chat_messages_AssistantMessageId",
                        column: x => x.AssistantMessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_llm_request_audits_chat_messages_UserMessageId",
                        column: x => x.UserMessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_llm_request_audits_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_llm_request_audits_AssistantMessageId",
                table: "llm_request_audits",
                column: "AssistantMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_llm_request_audits_ConversationId",
                table: "llm_request_audits",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_llm_request_audits_CreatedAt",
                table: "llm_request_audits",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_llm_request_audits_Success",
                table: "llm_request_audits",
                column: "Success");

            migrationBuilder.CreateIndex(
                name: "IX_llm_request_audits_UserMessageId",
                table: "llm_request_audits",
                column: "UserMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_request_audits");
        }
    }
}
