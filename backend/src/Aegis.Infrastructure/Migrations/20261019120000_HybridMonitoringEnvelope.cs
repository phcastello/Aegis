using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations;

public sealed partial class HybridMonitoringEnvelope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "MonitoringEnvelopeJson", table: "places", type: "jsonb", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "MonitoringEnvelopeJson", table: "places");
}
