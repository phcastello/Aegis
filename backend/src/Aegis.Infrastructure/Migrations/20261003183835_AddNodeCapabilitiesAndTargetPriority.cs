using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeCapabilitiesAndTargetPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TargetPriority",
                table: "aegis_nodes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "node_capabilities",
                columns: table => new
                {
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_capabilities", x => new { x.NodeId, x.Name });
                    table.CheckConstraint("ck_node_capability_version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_node_capabilities_aegis_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "aegis_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_target_priority",
                table: "aegis_nodes",
                sql: "\"TargetPriority\" BETWEEN -1000 AND 1000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "node_capabilities");

            migrationBuilder.DropCheckConstraint(
                name: "ck_node_target_priority",
                table: "aegis_nodes");

            migrationBuilder.DropColumn(
                name: "TargetPriority",
                table: "aegis_nodes");
        }
    }
}
