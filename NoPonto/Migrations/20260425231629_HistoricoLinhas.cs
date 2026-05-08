using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class HistoricoLinhas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HistoricoPassagens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordem = table.Column<string>(type: "text", nullable: false),
                    CodigoLinha = table.Column<string>(type: "text", nullable: false),
                    ItinerarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParadaId = table.Column<Guid>(type: "uuid", nullable: false),
                    PosicaoNaRota = table.Column<double>(type: "double precision", nullable: false),
                    DistanciaParadaMetros = table.Column<double>(type: "double precision", nullable: false),
                    TimestampGps = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TimestampRegistro = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VelocidadeInstantanea = table.Column<double>(type: "double precision", nullable: false),
                    VelocidadeMedia = table.Column<double>(type: "double precision", nullable: true),
                    HoraDia = table.Column<int>(type: "integer", nullable: false),
                    DiaSemana = table.Column<int>(type: "integer", nullable: false),
                    TempoDesdeParadaAnteriorSegundos = table.Column<double>(type: "double precision", nullable: true),
                    DistanciaTrechoMetros = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricoPassagens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistoricoPassagens_Itinerarios_ItinerarioId",
                        column: x => x.ItinerarioId,
                        principalTable: "Itinerarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HistoricoPassagens_Paradas_ParadaId",
                        column: x => x.ParadaId,
                        principalTable: "Paradas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_CodigoLinha_ItinerarioId_TimestampGps",
                table: "HistoricoPassagens",
                columns: new[] { "CodigoLinha", "ItinerarioId", "TimestampGps" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_ItinerarioId",
                table: "HistoricoPassagens",
                column: "ItinerarioId");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_Ordem_TimestampGps",
                table: "HistoricoPassagens",
                columns: new[] { "Ordem", "TimestampGps" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_ParadaId_TimestampGps",
                table: "HistoricoPassagens",
                columns: new[] { "ParadaId", "TimestampGps" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_TimestampGps",
                table: "HistoricoPassagens",
                column: "TimestampGps");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistoricoPassagens");
        }
    }
}
