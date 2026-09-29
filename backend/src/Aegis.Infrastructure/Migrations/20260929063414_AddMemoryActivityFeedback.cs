using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryActivityFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memory_activity_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_activity_events", x => x.Id);
                    table.CheckConstraint("CK_memory_activity_kind", "\"Kind\" IN ('Used','Consulted','Created','Updated','Deleted')");
                    table.CheckConstraint("CK_memory_activity_source", "\"Source\" IN ('AutomaticContext','ObservedContext','AutomaticExtraction','ExplicitTool')");
                    table.CheckConstraint("CK_memory_activity_target", "\"TargetType\" IN ('MemoryRecord','MemoryRelation')");
                    table.ForeignKey(
                        name: "FK_memory_activity_events_chat_messages_UserMessageId",
                        column: x => x.UserMessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_memory_activity_events_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_memory_activity_events_ConversationId",
                table: "memory_activity_events",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_activity_events_DedupeKey",
                table: "memory_activity_events",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_activity_events_UserMessageId",
                table: "memory_activity_events",
                column: "UserMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_activity_events");
        }
    }
}
