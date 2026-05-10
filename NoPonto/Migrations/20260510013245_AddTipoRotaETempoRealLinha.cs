using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class AddTipoRotaETempoRealLinha : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TipoRota",
                table: "Linhas",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TipoRota",
                table: "Linhas");
        }
    }
}
