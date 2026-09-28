using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIntelligentMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_memory_records_ContentHash",
                table: "memory_records");

            migrationBuilder.CreateTable(
                name: "memory_extraction_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProcessingStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastError = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CandidatesCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedCount = table.Column<int>(type: "integer", nullable: false),
                    ReinforcedCount = table.Column<int>(type: "integer", nullable: false),
                    CorrectedCount = table.Column<int>(type: "integer", nullable: false),
                    TransitionedCount = table.Column<int>(type: "integer", nullable: false),
                    GraphMutationsCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedCount = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_extraction_jobs", x => x.Id);
                    table.CheckConstraint("CK_memory_extraction_attempt", "\"Attempt\" >= 0");
                    table.CheckConstraint("CK_memory_extraction_counts", "\"CandidatesCount\" >= 0 AND \"CreatedCount\" >= 0 AND \"ReinforcedCount\" >= 0 AND \"CorrectedCount\" >= 0 AND \"TransitionedCount\" >= 0 AND \"GraphMutationsCount\" >= 0 AND \"SkippedCount\" >= 0");
                    table.CheckConstraint("CK_memory_extraction_status", "\"Status\" IN ('Pending','Processing','Completed','Failed')");
                    table.ForeignKey(
                        name: "FK_memory_extraction_jobs_chat_messages_UserMessageId",
                        column: x => x.UserMessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_memory_extraction_jobs_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_memory_records_ContentHash",
                table: "memory_records",
                column: "ContentHash",
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_memory_extraction_jobs_ConversationId",
                table: "memory_extraction_jobs",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_extraction_jobs_Status_NextAttemptAt_CreatedAt",
                table: "memory_extraction_jobs",
                columns: new[] { "Status", "NextAttemptAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_extraction_jobs_UserMessageId",
                table: "memory_extraction_jobs",
                column: "UserMessageId",
                unique: true,
                filter: "\"UserMessageId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_extraction_jobs");

            migrationBuilder.DropIndex(
                name: "IX_memory_records_ContentHash",
                table: "memory_records");

            migrationBuilder.CreateIndex(
                name: "IX_memory_records_ContentHash",
                table: "memory_records",
                column: "ContentHash",
                unique: true,
                filter: "\"Status\" = 'Active'");
        }
    }
}
