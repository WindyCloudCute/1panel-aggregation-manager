using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PanelFleet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityEntrance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecurityEntrance",
                table: "Nodes",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityEntrance",
                table: "Nodes");
        }
    }
}
