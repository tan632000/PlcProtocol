using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndustrialIot.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class TelemetryEdgeReceivedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_telemetry_edge_received",
                table: "telemetry",
                columns: new[] { "edge_id", "received_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_telemetry_edge_received",
                table: "telemetry");
        }
    }
}
