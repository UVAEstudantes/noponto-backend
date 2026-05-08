using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class AddParadaItinerarioRelacionamento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "PosicaoLinha",
                table: "ParadasItinerario",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateIndex(
                name: "IX_ParadasItinerario_ItinerarioId_Ordem",
                table: "ParadasItinerario",
                columns: new[] { "ItinerarioId", "Ordem" });

            migrationBuilder.CreateIndex(
                name: "IX_Itinerarios_Geometria",
                table: "Itinerarios",
                column: "Geometria")
                .Annotation("Npgsql:IndexMethod", "GIST");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ParadasItinerario_ItinerarioId_Ordem",
                table: "ParadasItinerario");

            migrationBuilder.DropIndex(
                name: "IX_Itinerarios_Geometria",
                table: "Itinerarios");

            migrationBuilder.DropColumn(
                name: "PosicaoLinha",
                table: "ParadasItinerario");
        }
    }
}
