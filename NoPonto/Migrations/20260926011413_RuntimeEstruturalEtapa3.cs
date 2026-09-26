using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeEstruturalEtapa3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HistoricoPassagens_Itinerarios_ItinerarioId",
                table: "HistoricoPassagens");

            migrationBuilder.AddColumn<Guid>(
                name: "LinhaId",
                table: "TelemetriasVeiculoMl",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ItinerarioId",
                table: "HistoricoPassagens",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_ViagemId_OcorrenciaParadaPadraoId_Volta",
                table: "HistoricoPassagens",
                columns: new[] { "ViagemId", "OcorrenciaParadaPadraoId", "Volta" },
                unique: true,
                filter: "\"ViagemId\" IS NOT NULL AND \"OcorrenciaParadaPadraoId\" IS NOT NULL AND \"Volta\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_HistoricoPassagens_Itinerarios_ItinerarioId",
                table: "HistoricoPassagens",
                column: "ItinerarioId",
                principalTable: "Itinerarios",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HistoricoPassagens_Itinerarios_ItinerarioId",
                table: "HistoricoPassagens");

            migrationBuilder.DropIndex(
                name: "IX_HistoricoPassagens_ViagemId_OcorrenciaParadaPadraoId_Volta",
                table: "HistoricoPassagens");

            migrationBuilder.DropColumn(
                name: "LinhaId",
                table: "TelemetriasVeiculoMl");

            migrationBuilder.AlterColumn<Guid>(
                name: "ItinerarioId",
                table: "HistoricoPassagens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_HistoricoPassagens_Itinerarios_ItinerarioId",
                table: "HistoricoPassagens",
                column: "ItinerarioId",
                principalTable: "Itinerarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
