using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndustrialIot.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "commands",
                columns: table => new
                {
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    edge_id = table.Column<string>(type: "text", nullable: false),
                    tag_id = table.Column<string>(type: "text", nullable: false),
                    target_value = table.Column<string>(type: "jsonb", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_by = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: true),
                    request_hash = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_received_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commands", x => x.command_id);
                });

            migrationBuilder.CreateTable(
                name: "latest_values",
                columns: table => new
                {
                    edge_id = table.Column<string>(type: "text", nullable: false),
                    tag_id = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: true),
                    quality = table.Column<string>(type: "text", nullable: false),
                    timestamp_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_latest_values", x => new { x.edge_id, x.tag_id });
                });

            migrationBuilder.CreateTable(
                name: "telemetry",
                columns: table => new
                {
                    edge_id = table.Column<string>(type: "text", nullable: false),
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    tag_id = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: true),
                    quality = table.Column<string>(type: "text", nullable: false),
                    timestamp_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry", x => new { x.edge_id, x.stream_id, x.sequence });
                });

            migrationBuilder.CreateIndex(
                name: "ix_commands_edge_created",
                table: "commands",
                columns: new[] { "edge_id", "created_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_commands_status_expiry",
                table: "commands",
                columns: new[] { "status", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_commands_issuer_key",
                table: "commands",
                columns: new[] { "issued_by", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_edge_tag_time",
                table: "telemetry",
                columns: new[] { "edge_id", "tag_id", "timestamp_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_received",
                table: "telemetry",
                column: "received_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "commands");

            migrationBuilder.DropTable(
                name: "latest_values");

            migrationBuilder.DropTable(
                name: "telemetry");
        }
    }
}
