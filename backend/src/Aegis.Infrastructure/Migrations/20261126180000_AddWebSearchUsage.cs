using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    public partial class AddWebSearchUsage : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "web_search_usage",
                columns: table => new
                {
                    CallId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ResponseId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActionType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FirstObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_web_search_usage", x => x.CallId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_web_search_usage_FirstObservedAt",
                table: "web_search_usage",
                column: "FirstObservedAt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "web_search_usage");
        }
    }
}
