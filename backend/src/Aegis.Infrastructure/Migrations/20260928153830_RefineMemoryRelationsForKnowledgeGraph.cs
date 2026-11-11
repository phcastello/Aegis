// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefineMemoryRelationsForKnowledgeGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_memory_relations_SubjectEntityId_Predicate_ObjectEntityId",
                table: "memory_relations");

            migrationBuilder.CreateIndex(
                name: "IX_memory_relations_SubjectEntityId_Predicate_ObjectEntityId",
                table: "memory_relations",
                columns: new[] { "SubjectEntityId", "Predicate", "ObjectEntityId" },
                filter: "\"Status\" = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_memory_relations_SubjectEntityId_Predicate_ObjectEntityId",
                table: "memory_relations");

            migrationBuilder.CreateIndex(
                name: "IX_memory_relations_SubjectEntityId_Predicate_ObjectEntityId",
                table: "memory_relations",
                columns: new[] { "SubjectEntityId", "Predicate", "ObjectEntityId" },
                unique: true,
                filter: "\"Status\" = 'Active'");
        }
    }
}
