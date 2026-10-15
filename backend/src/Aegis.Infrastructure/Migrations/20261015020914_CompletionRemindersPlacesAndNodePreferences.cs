using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompletionRemindersPlacesAndNodePreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_reminder_node_delivery_attempts_aegis_nodes_NodeId",
                table: "reminder_node_delivery_attempts");

            migrationBuilder.DropIndex(
                name: "IX_reminder_node_delivery_attempts_ReminderId_Attempt",
                table: "reminder_node_delivery_attempts");

            migrationBuilder.AddColumn<string>(
                name: "DeliveryPolicy",
                table: "reminders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "allEligible");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeliveryUntil",
                table: "reminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Generation",
                table: "reminders",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "IsException",
                table: "reminders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ObserverNodeId",
                table: "reminders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ObserverPostedAt",
                table: "reminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OccurrenceKey",
                table: "reminders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "reminders",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "SeriesId",
                table: "reminders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceEventId",
                table: "reminders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Generation",
                table: "reminder_node_delivery_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "RecipientId",
                table: "reminder_node_delivery_attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiveReminders",
                table: "aegis_nodes",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "geographic_reminder_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    PlaceIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ObserverNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Transition = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Repeat = table.Column<bool>(type: "boolean", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeliveryPolicy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SyncStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SyncedRevision = table.Column<int>(type: "integer", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastEventSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastEventAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_geographic_reminder_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_geographic_reminder_rules_conversations_SourceConversationId",
                        column: x => x.SourceConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "places",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CirclesJson = table.Column<string>(type: "jsonb", nullable: false),
                    AliasesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_places", x => x.Id);
                    table.ForeignKey(
                        name: "FK_places_places_ParentId",
                        column: x => x.ParentId,
                        principalTable: "places",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reminder_action_receipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReminderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedRevision = table.Column<int>(type: "integer", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SnoozeMinutes = table.Column<int>(type: "integer", nullable: false),
                    Applied = table.Column<bool>(type: "boolean", nullable: false),
                    ResponseJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_action_receipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reminder_action_receipts_reminders_ReminderId",
                        column: x => x.ReminderId,
                        principalTable: "reminders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reminder_recipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReminderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: true),
                    CommandExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Ambiguous = table.Column<bool>(type: "boolean", nullable: false),
                    ProviderAcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WithdrawalQueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Transport = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_recipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reminder_recipients_aegis_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "aegis_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_reminder_recipients_reminders_ReminderId",
                        column: x => x.ReminderId,
                        principalTable: "reminders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reminder_series",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RuleJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    DeliveryPolicy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResumeAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastMaterializedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GeneratedCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_series", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reminder_series_conversations_SourceConversationId",
                        column: x => x.SourceConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "geographic_reminder_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObserverNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    RuleRevision = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_geographic_reminder_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_geographic_reminder_events_aegis_nodes_ObserverNodeId",
                        column: x => x.ObserverNodeId,
                        principalTable: "aegis_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_geographic_reminder_events_geographic_reminder_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "geographic_reminder_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "node_location_profiles",
                columns: table => new
                {
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FixedPlaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsUserSource = table.Column<bool>(type: "boolean", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    AccuracyMeters = table.Column<double>(type: "double precision", nullable: true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ResolvedPlaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastRequestId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_location_profiles", x => x.NodeId);
                    table.ForeignKey(
                        name: "FK_node_location_profiles_aegis_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "aegis_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_node_location_profiles_places_FixedPlaceId",
                        column: x => x.FixedPlaceId,
                        principalTable: "places",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_node_location_profiles_places_ResolvedPlaceId",
                        column: x => x.ResolvedPlaceId,
                        principalTable: "places",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reminders_SeriesId_OccurrenceKey",
                table: "reminders",
                columns: new[] { "SeriesId", "OccurrenceKey" },
                unique: true,
                filter: "\"SeriesId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_reminders_SourceEventId",
                table: "reminders",
                column: "SourceEventId",
                unique: true,
                filter: "\"SourceEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_node_delivery_attempts_ReminderId_Attempt",
                table: "reminder_node_delivery_attempts",
                columns: new[] { "ReminderId", "Attempt" });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_node_delivery_attempts_ReminderId_Generation_NodeI~",
                table: "reminder_node_delivery_attempts",
                columns: new[] { "ReminderId", "Generation", "NodeId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_geographic_reminder_events_ObserverNodeId",
                table: "geographic_reminder_events",
                column: "ObserverNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_geographic_reminder_events_OccurrenceId",
                table: "geographic_reminder_events",
                column: "OccurrenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_geographic_reminder_events_RuleId_Sequence",
                table: "geographic_reminder_events",
                columns: new[] { "RuleId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_geographic_reminder_rules_ObserverNodeId_Status",
                table: "geographic_reminder_rules",
                columns: new[] { "ObserverNodeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_geographic_reminder_rules_SourceConversationId",
                table: "geographic_reminder_rules",
                column: "SourceConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_node_location_profiles_FixedPlaceId",
                table: "node_location_profiles",
                column: "FixedPlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_node_location_profiles_IsUserSource",
                table: "node_location_profiles",
                column: "IsUserSource",
                unique: true,
                filter: "\"IsUserSource\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_node_location_profiles_ResolvedPlaceId",
                table: "node_location_profiles",
                column: "ResolvedPlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_places_ParentId",
                table: "places",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_action_receipts_ReminderId",
                table: "reminder_action_receipts",
                column: "ReminderId");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_recipients_NodeId",
                table: "reminder_recipients",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_recipients_ReminderId_Generation_NodeId",
                table: "reminder_recipients",
                columns: new[] { "ReminderId", "Generation", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reminder_series_SourceConversationId",
                table: "reminder_series",
                column: "SourceConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_series_Status",
                table: "reminder_series",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_reminder_node_delivery_attempts_aegis_nodes_NodeId",
                table: "reminder_node_delivery_attempts",
                column: "NodeId",
                principalTable: "aegis_nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_reminders_reminder_series_SeriesId",
                table: "reminders",
                column: "SeriesId",
                principalTable: "reminder_series",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_reminder_node_delivery_attempts_aegis_nodes_NodeId",
                table: "reminder_node_delivery_attempts");

            migrationBuilder.DropForeignKey(
                name: "FK_reminders_reminder_series_SeriesId",
                table: "reminders");

            migrationBuilder.DropTable(
                name: "geographic_reminder_events");

            migrationBuilder.DropTable(
                name: "node_location_profiles");

            migrationBuilder.DropTable(
                name: "reminder_action_receipts");

            migrationBuilder.DropTable(
                name: "reminder_recipients");

            migrationBuilder.DropTable(
                name: "reminder_series");

            migrationBuilder.DropTable(
                name: "geographic_reminder_rules");

            migrationBuilder.DropTable(
                name: "places");

            migrationBuilder.DropIndex(
                name: "IX_reminders_SeriesId_OccurrenceKey",
                table: "reminders");

            migrationBuilder.DropIndex(
                name: "IX_reminders_SourceEventId",
                table: "reminders");

            migrationBuilder.DropIndex(
                name: "IX_reminder_node_delivery_attempts_ReminderId_Attempt",
                table: "reminder_node_delivery_attempts");

            migrationBuilder.DropIndex(
                name: "IX_reminder_node_delivery_attempts_ReminderId_Generation_NodeI~",
                table: "reminder_node_delivery_attempts");

            migrationBuilder.DropColumn(
                name: "DeliveryPolicy",
                table: "reminders");

            migrationBuilder.DropColumn(
                name: "DeliveryUntil",
                table: "reminders");

            migrationBuilder.DropColumn(
                name: "Generation",
                table: "reminders");

            migrationBuilder.DropColumn(
                name: "IsException",
                table: "reminders");

            migrationBuilder.DropColumn(
                name: "ObserverNodeId",
                table: "reminders");

            migrationBuilder.DropColumn(
                name: "ObserverPostedAt",
                table: "reminders");

            migrationBuilder.DropColumn(
                name: "OccurrenceKey",
                table: "reminders");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "reminders");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "reminders");

            migrationBuilder.DropColumn(
                name: "SourceEventId",
                table: "reminders");

            migrationBuilder.DropColumn(
                name: "Generation",
                table: "reminder_node_delivery_attempts");

            migrationBuilder.DropColumn(
                name: "RecipientId",
                table: "reminder_node_delivery_attempts");

            migrationBuilder.DropColumn(
                name: "ReceiveReminders",
                table: "aegis_nodes");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_node_delivery_attempts_ReminderId_Attempt",
                table: "reminder_node_delivery_attempts",
                columns: new[] { "ReminderId", "Attempt" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_reminder_node_delivery_attempts_aegis_nodes_NodeId",
                table: "reminder_node_delivery_attempts",
                column: "NodeId",
                principalTable: "aegis_nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
