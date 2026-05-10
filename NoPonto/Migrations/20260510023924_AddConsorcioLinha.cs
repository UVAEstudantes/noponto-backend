using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class AddConsorcioLinha : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Consorcio",
                table: "Linhas",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Consorcio",
                table: "Linhas");
        }
    }
}
