using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class PoiItinerarioNovosCampos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PoisItinerario_Itinerarios_ItinerarioId",
                table: "PoisItinerario");

            migrationBuilder.DropForeignKey(
                name: "FK_PoisItinerario_Pois_PoiId",
                table: "PoisItinerario");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PoisItinerario",
                table: "PoisItinerario");

            migrationBuilder.RenameTable(
                name: "PoisItinerario",
                newName: "PoiItinerarios");

            migrationBuilder.RenameIndex(
                name: "IX_PoisItinerario_PoiId",
                table: "PoiItinerarios",
                newName: "IX_PoiItinerarios_PoiId");

            migrationBuilder.RenameIndex(
                name: "IX_PoisItinerario_ItinerarioId",
                table: "PoiItinerarios",
                newName: "IX_PoiItinerarios_ItinerarioId");

            migrationBuilder.AddColumn<double>(
                name: "PosicaoLinha",
                table: "PoiItinerarios",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Score",
                table: "PoiItinerarios",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_PoiItinerarios",
                table: "PoiItinerarios",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PoiItinerarios_Itinerarios_ItinerarioId",
                table: "PoiItinerarios",
                column: "ItinerarioId",
                principalTable: "Itinerarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PoiItinerarios_Pois_PoiId",
                table: "PoiItinerarios",
                column: "PoiId",
                principalTable: "Pois",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PoiItinerarios_Itinerarios_ItinerarioId",
                table: "PoiItinerarios");

            migrationBuilder.DropForeignKey(
                name: "FK_PoiItinerarios_Pois_PoiId",
                table: "PoiItinerarios");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PoiItinerarios",
                table: "PoiItinerarios");

            migrationBuilder.DropColumn(
                name: "PosicaoLinha",
                table: "PoiItinerarios");

            migrationBuilder.DropColumn(
                name: "Score",
                table: "PoiItinerarios");

            migrationBuilder.RenameTable(
                name: "PoiItinerarios",
                newName: "PoisItinerario");

            migrationBuilder.RenameIndex(
                name: "IX_PoiItinerarios_PoiId",
                table: "PoisItinerario",
                newName: "IX_PoisItinerario_PoiId");

            migrationBuilder.RenameIndex(
                name: "IX_PoiItinerarios_ItinerarioId",
                table: "PoisItinerario",
                newName: "IX_PoisItinerario_ItinerarioId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PoisItinerario",
                table: "PoisItinerario",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PoisItinerario_Itinerarios_ItinerarioId",
                table: "PoisItinerario",
                column: "ItinerarioId",
                principalTable: "Itinerarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PoisItinerario_Pois_PoiId",
                table: "PoisItinerario",
                column: "PoiId",
                principalTable: "Pois",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
