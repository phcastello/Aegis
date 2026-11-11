// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
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
