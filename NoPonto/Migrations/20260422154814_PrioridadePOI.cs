using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class PrioridadePOI : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Prioridade",
                table: "Pois",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Prioridade",
                table: "Pois");
        }
    }
}
