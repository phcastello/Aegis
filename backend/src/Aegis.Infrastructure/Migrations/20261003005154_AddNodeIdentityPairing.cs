using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aegis.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeIdentityPairing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "aegis_nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    AppVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    PairedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aegis_nodes", x => x.Id);
                    table.CheckConstraint("ck_node_name", "length(trim(\"Name\")) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_node_platform", "\"Platform\" IN ('Android', 'Windows')");
                    table.CheckConstraint("ck_node_protocol", "\"ProtocolVersion\" = 1");
                    table.CheckConstraint("ck_node_revoked_disabled", "\"RevokedAt\" IS NULL OR NOT \"Enabled\"");
                });

            migrationBuilder.CreateTable(
                name: "node_credentials",
                columns: table => new
                {
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_credentials", x => x.NodeId);
                    table.CheckConstraint("ck_node_credential_hash", "octet_length(\"SecretHash\") = 32");
                    table.ForeignKey(
                        name: "FK_node_credentials_aegis_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "aegis_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "node_pairing_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Selector = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    IssuerNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_pairing_codes", x => x.Id);
                    table.CheckConstraint("ck_node_code_attempts", "\"FailedAttempts\" BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_node_code_hash", "octet_length(\"CodeHash\") = 32");
                    table.ForeignKey(
                        name: "FK_node_pairing_codes_aegis_nodes_IssuerNodeId",
                        column: x => x.IssuerNodeId,
                        principalTable: "aegis_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "node_pairing_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairingCodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AppVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RecoveryHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CredentialHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    EncryptedReceipt = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_pairing_attempts", x => x.Id);
                    table.CheckConstraint("ck_node_attempt_hashes", "octet_length(\"RecoveryHash\") = 32 AND octet_length(\"CredentialHash\") = 32");
                    table.CheckConstraint("ck_node_attempt_platform", "\"Platform\" IN ('Android', 'Windows')");
                    table.ForeignKey(
                        name: "FK_node_pairing_attempts_node_pairing_codes_PairingCodeId",
                        column: x => x.PairingCodeId,
                        principalTable: "node_pairing_codes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_aegis_nodes_Enabled_RevokedAt",
                table: "aegis_nodes",
                columns: new[] { "Enabled", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_node_pairing_attempts_ExpiresAt",
                table: "node_pairing_attempts",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_node_pairing_attempts_NodeId",
                table: "node_pairing_attempts",
                column: "NodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_node_pairing_attempts_PairingCodeId",
                table: "node_pairing_attempts",
                column: "PairingCodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_node_pairing_codes_ExpiresAt",
                table: "node_pairing_codes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_node_pairing_codes_IssuerNodeId",
                table: "node_pairing_codes",
                column: "IssuerNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_node_pairing_codes_Selector",
                table: "node_pairing_codes",
                column: "Selector",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "node_credentials");

            migrationBuilder.DropTable(
                name: "node_pairing_attempts");

            migrationBuilder.DropTable(
                name: "node_pairing_codes");

            migrationBuilder.DropTable(
                name: "aegis_nodes");
        }
    }
}
