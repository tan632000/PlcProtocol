using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndustrialIot.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class EdgeInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "edge_inventory",
                columns: table => new
                {
                    edge_id = table.Column<string>(type: "text", nullable: false),
                    boot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round = table.Column<long>(type: "bigint", nullable: false),
                    received_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edge_inventory", x => x.edge_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edge_inventory");
        }
    }
}
