using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations;

[DbContext(typeof(AegisDbContext))]
[Migration("20260925180000_TrackPossibleEmailEffects")]
public sealed class TrackPossibleEmailEffects : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "MayHaveAppliedChanges",
            table: "pending_email_actions",
            type: "boolean",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "MayHaveAppliedChanges", table: "pending_email_actions");
    }
}
