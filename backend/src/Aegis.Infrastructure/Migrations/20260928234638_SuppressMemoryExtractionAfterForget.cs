using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SuppressMemoryExtractionAfterForget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_memory_extraction_status",
                table: "memory_extraction_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_memory_extraction_status",
                table: "memory_extraction_jobs",
                sql: "\"Status\" IN ('Pending','Processing','Completed','Failed','Suppressed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_memory_extraction_status",
                table: "memory_extraction_jobs");

            // The previous schema has no terminal suppression status.
            migrationBuilder.Sql("UPDATE memory_extraction_jobs SET \"Status\" = 'Completed' WHERE \"Status\" = 'Suppressed'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_memory_extraction_status",
                table: "memory_extraction_jobs",
                sql: "\"Status\" IN ('Pending','Processing','Completed','Failed')");
        }
    }
}
