using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class TelemetriaMlContinua : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelemetriasVeiculoMl",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservacaoId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Modal = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Provedor = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OrdemVeiculo = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CodigoLinha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OrigemPosicao = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LatitudeRecebida = table.Column<double>(type: "double precision", nullable: false),
                    LongitudeRecebida = table.Column<double>(type: "double precision", nullable: false),
                    LatitudeProjetada = table.Column<double>(type: "double precision", nullable: true),
                    LongitudeProjetada = table.Column<double>(type: "double precision", nullable: true),
                    VelocidadeInstantanea = table.Column<double>(type: "double precision", nullable: false),
                    Bearing = table.Column<double>(type: "double precision", nullable: true),
                    TimestampGps = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TimestampEnvioFonte = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TimestampServidorFonte = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RecebidoEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EventoCriadoEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ItinerarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    SentidoId = table.Column<Guid>(type: "uuid", nullable: true),
                    ViagemId = table.Column<Guid>(type: "uuid", nullable: true),
                    PosicaoNaRota = table.Column<double>(type: "double precision", nullable: true),
                    ComprimentoRotaMetros = table.Column<double>(type: "double precision", nullable: true),
                    ProximaParadaItinerarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    DistanciaProximaParadaMetros = table.Column<double>(type: "double precision", nullable: true),
                    VelocidadeMediaCausal = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetriasVeiculoMl", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetriasVeiculoMl_CodigoLinha_TimestampGps",
                table: "TelemetriasVeiculoMl",
                columns: new[] { "CodigoLinha", "TimestampGps" });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetriasVeiculoMl_ObservacaoId",
                table: "TelemetriasVeiculoMl",
                column: "ObservacaoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelemetriasVeiculoMl_OrdemVeiculo_TimestampGps",
                table: "TelemetriasVeiculoMl",
                columns: new[] { "OrdemVeiculo", "TimestampGps" });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetriasVeiculoMl_ViagemId_TimestampGps",
                table: "TelemetriasVeiculoMl",
                columns: new[] { "ViagemId", "TimestampGps" },
                filter: "\"ViagemId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelemetriasVeiculoMl");
        }
    }
}
