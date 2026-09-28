using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memory_entities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CanonicalName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_entities", x => x.Id);
                    table.CheckConstraint("CK_memory_entity_name", "length(btrim(\"CanonicalName\")) > 0 AND length(btrim(\"NormalizedName\")) > 0");
                    table.CheckConstraint("CK_memory_entity_revision", "\"Revision\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "memory_projection_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectionTarget = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    AggregateRevision = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ProcessingStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_projection_jobs", x => x.Id);
                    table.CheckConstraint("CK_memory_job_aggregate", "\"AggregateType\" IN ('MemoryRecord','MemoryEntity','MemoryRelation')");
                    table.CheckConstraint("CK_memory_job_attempt", "\"Attempt\" >= 0");
                    table.CheckConstraint("CK_memory_job_operation", "\"Operation\" IN ('Upsert','Delete')");
                    table.CheckConstraint("CK_memory_job_revision", "\"AggregateRevision\" >= 1");
                    table.CheckConstraint("CK_memory_job_status", "\"Status\" IN ('Pending','Processing','Completed','Failed')");
                    table.CheckConstraint("CK_memory_job_target", "\"ProjectionTarget\" IN ('Semantic','Graph')");
                });

            migrationBuilder.CreateTable(
                name: "memory_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededById = table.Column<Guid>(type: "uuid", nullable: true),
                    ForgottenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_records", x => x.Id);
                    table.CheckConstraint("CK_memory_content", "length(btrim(\"Content\")) > 0");
                    table.CheckConstraint("CK_memory_lifecycle", "(\"Status\" = 'Active' AND \"SupersededAt\" IS NULL AND \"SupersededById\" IS NULL AND \"ForgottenAt\" IS NULL) OR (\"Status\" = 'Superseded' AND \"SupersededAt\" IS NOT NULL AND \"SupersededById\" IS NOT NULL AND \"ForgottenAt\" IS NULL) OR (\"Status\" = 'Forgotten' AND \"ForgottenAt\" IS NOT NULL AND \"SupersededAt\" IS NULL AND \"SupersededById\" IS NULL)");
                    table.CheckConstraint("CK_memory_revision", "\"Revision\" >= 1");
                    table.CheckConstraint("CK_memory_status", "\"Status\" IN ('Active','Superseded','Forgotten')");
                    table.CheckConstraint("CK_memory_supersession", "\"SupersededById\" IS NULL OR \"SupersededById\" <> \"Id\"");
                    table.CheckConstraint("CK_memory_validity", "\"ValidFrom\" IS NULL OR \"ValidUntil\" IS NULL OR \"ValidUntil\" > \"ValidFrom\"");
                    table.ForeignKey(
                        name: "FK_memory_records_memory_records_SupersededById",
                        column: x => x.SupersededById,
                        principalTable: "memory_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "memory_entity_aliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Alias = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedAlias = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_entity_aliases", x => x.Id);
                    table.CheckConstraint("CK_memory_alias_name", "length(btrim(\"Alias\")) > 0 AND length(btrim(\"NormalizedAlias\")) > 0");
                    table.ForeignKey(
                        name: "FK_memory_entity_aliases_memory_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "memory_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "memory_relations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Predicate = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ObjectEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededById = table.Column<Guid>(type: "uuid", nullable: true),
                    ForgottenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_relations", x => x.Id);
                    table.CheckConstraint("CK_memory_relation_lifecycle", "(\"Status\" = 'Active' AND \"SupersededAt\" IS NULL AND \"SupersededById\" IS NULL AND \"ForgottenAt\" IS NULL) OR (\"Status\" = 'Superseded' AND \"SupersededAt\" IS NOT NULL AND \"SupersededById\" IS NOT NULL AND \"ForgottenAt\" IS NULL) OR (\"Status\" = 'Forgotten' AND \"ForgottenAt\" IS NOT NULL AND \"SupersededAt\" IS NULL AND \"SupersededById\" IS NULL)");
                    table.CheckConstraint("CK_memory_relation_predicate", "\"Predicate\" ~ '^[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$'");
                    table.CheckConstraint("CK_memory_relation_revision", "\"Revision\" >= 1");
                    table.CheckConstraint("CK_memory_relation_status", "\"Status\" IN ('Active','Superseded','Forgotten')");
                    table.CheckConstraint("CK_memory_relation_supersession", "\"SupersededById\" IS NULL OR \"SupersededById\" <> \"Id\"");
                    table.CheckConstraint("CK_memory_relation_validity", "\"ValidFrom\" IS NULL OR \"ValidUntil\" IS NULL OR \"ValidUntil\" > \"ValidFrom\"");
                    table.ForeignKey(
                        name: "FK_memory_relations_memory_entities_ObjectEntityId",
                        column: x => x.ObjectEntityId,
                        principalTable: "memory_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_memory_relations_memory_entities_SubjectEntityId",
                        column: x => x.SubjectEntityId,
                        principalTable: "memory_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_memory_relations_memory_relations_SupersededById",
                        column: x => x.SupersededById,
                        principalTable: "memory_relations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "memory_evidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_evidence", x => x.Id);
                    table.CheckConstraint("CK_memory_source_kind", "\"SourceKind\" IN ('ExplicitMemoryRequest','UserStatement','ToolObservation','Inference')");
                    table.ForeignKey(
                        name: "FK_memory_evidence_chat_messages_SourceMessageId",
                        column: x => x.SourceMessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_memory_evidence_conversations_SourceConversationId",
                        column: x => x.SourceConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_memory_evidence_memory_records_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "memory_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "memory_relation_evidence",
                columns: table => new
                {
                    RelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_relation_evidence", x => new { x.RelationId, x.MemoryId });
                    table.ForeignKey(
                        name: "FK_memory_relation_evidence_memory_records_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "memory_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_memory_relation_evidence_memory_relations_RelationId",
                        column: x => x.RelationId,
                        principalTable: "memory_relations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_memory_entities_NormalizedName_EntityType",
                table: "memory_entities",
                columns: new[] { "NormalizedName", "EntityType" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_entity_aliases_EntityId_NormalizedAlias",
                table: "memory_entity_aliases",
                columns: new[] { "EntityId", "NormalizedAlias" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_entity_aliases_NormalizedAlias",
                table: "memory_entity_aliases",
                column: "NormalizedAlias");

            migrationBuilder.CreateIndex(
                name: "IX_memory_evidence_MemoryId",
                table: "memory_evidence",
                column: "MemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_evidence_MemoryId_SourceKind_SourceMessageId",
                table: "memory_evidence",
                columns: new[] { "MemoryId", "SourceKind", "SourceMessageId" },
                unique: true,
                filter: "\"SourceMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_memory_evidence_SourceConversationId",
                table: "memory_evidence",
                column: "SourceConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_evidence_SourceMessageId",
                table: "memory_evidence",
                column: "SourceMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_projection_jobs_ProjectionTarget_AggregateType_Aggre~",
                table: "memory_projection_jobs",
                columns: new[] { "ProjectionTarget", "AggregateType", "AggregateId", "AggregateRevision", "Operation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_projection_jobs_Status_NextAttemptAt_CreatedAt",
                table: "memory_projection_jobs",
                columns: new[] { "Status", "NextAttemptAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_records_ContentHash",
                table: "memory_records",
                column: "ContentHash",
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_memory_records_Status_CreatedAt",
                table: "memory_records",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_records_SupersededById",
                table: "memory_records",
                column: "SupersededById");

            migrationBuilder.CreateIndex(
                name: "IX_memory_relation_evidence_MemoryId",
                table: "memory_relation_evidence",
                column: "MemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_relations_ObjectEntityId",
                table: "memory_relations",
                column: "ObjectEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_relations_SubjectEntityId_Predicate_ObjectEntityId",
                table: "memory_relations",
                columns: new[] { "SubjectEntityId", "Predicate", "ObjectEntityId" },
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_memory_relations_SupersededById",
                table: "memory_relations",
                column: "SupersededById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_entity_aliases");

            migrationBuilder.DropTable(
                name: "memory_evidence");

            migrationBuilder.DropTable(
                name: "memory_projection_jobs");

            migrationBuilder.DropTable(
                name: "memory_relation_evidence");

            migrationBuilder.DropTable(
                name: "memory_records");

            migrationBuilder.DropTable(
                name: "memory_relations");

            migrationBuilder.DropTable(
                name: "memory_entities");
        }
    }
}
