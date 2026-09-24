using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class GtfsParadaItinerarioMvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ParadasItinerario_ItinerarioId_Ordem",
                table: "ParadasItinerario");

            migrationBuilder.AddColumn<string>(
                name: "Fonte",
                table: "ParadasItinerario",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "SPATIAL_LEGACY");

            migrationBuilder.AddColumn<Guid>(
                name: "ImportacaoId",
                table: "ParadasItinerario",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SourceShapeDistTraveledMetros",
                table: "ParadasItinerario",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceStopSequence",
                table: "ParadasItinerario",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubstituidaPorImportacaoId",
                table: "ParadasItinerario",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParadasItinerario_ImportacaoId",
                table: "ParadasItinerario",
                column: "ImportacaoId");

            migrationBuilder.CreateIndex(
                name: "IX_ParadasItinerario_ItinerarioId_Ordem",
                table: "ParadasItinerario",
                columns: new[] { "ItinerarioId", "Ordem" },
                unique: true,
                filter: "\"Ativo\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_ParadasItinerario_SubstituidaPorImportacaoId",
                table: "ParadasItinerario",
                column: "SubstituidaPorImportacaoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ParadasItinerario_ImportacaoId",
                table: "ParadasItinerario");

            migrationBuilder.DropIndex(
                name: "IX_ParadasItinerario_ItinerarioId_Ordem",
                table: "ParadasItinerario");

            migrationBuilder.DropIndex(
                name: "IX_ParadasItinerario_SubstituidaPorImportacaoId",
                table: "ParadasItinerario");

            migrationBuilder.DropColumn(
                name: "Fonte",
                table: "ParadasItinerario");

            migrationBuilder.DropColumn(
                name: "ImportacaoId",
                table: "ParadasItinerario");

            migrationBuilder.DropColumn(
                name: "SourceShapeDistTraveledMetros",
                table: "ParadasItinerario");

            migrationBuilder.DropColumn(
                name: "SourceStopSequence",
                table: "ParadasItinerario");

            migrationBuilder.DropColumn(
                name: "SubstituidaPorImportacaoId",
                table: "ParadasItinerario");

            migrationBuilder.CreateIndex(
                name: "IX_ParadasItinerario_ItinerarioId_Ordem",
                table: "ParadasItinerario",
                columns: new[] { "ItinerarioId", "Ordem" });
        }
    }
}
