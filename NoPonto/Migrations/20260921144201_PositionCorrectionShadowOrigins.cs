using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class PositionCorrectionShadowOrigins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PositionCorrectionShadowOrigins",
                columns: table => new
                {
                    ShadowOriginId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ObservacaoId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContractVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PolicyFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CausalStateVersion = table.Column<int>(type: "integer", nullable: false),
                    TimestampGpsOrigemUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Modal = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Provedor = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OrdemVeiculo = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CodigoLinha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ItinerarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    SentidoId = table.Column<Guid>(type: "uuid", nullable: true),
                    ViagemId = table.Column<Guid>(type: "uuid", nullable: true),
                    PosicaoB = table.Column<double>(type: "double precision", nullable: false),
                    ComprimentoRotaMetros = table.Column<double>(type: "double precision", nullable: false),
                    VelocidadeInstantaneaKmh = table.Column<double>(type: "double precision", nullable: true),
                    VelocidadeMediaLegacyKmh = table.Column<double>(type: "double precision", nullable: true),
                    EstadoMovimento = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SamplesBeforeCap = table.Column<int>(type: "integer", nullable: true),
                    SamplesUsed = table.Column<int>(type: "integer", nullable: false),
                    MaxSamplesConfigured = table.Column<int>(type: "integer", nullable: false),
                    MaxSamplesReached = table.Column<bool>(type: "boolean", nullable: false),
                    RecebidoEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PersistidoEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AmostrasCausais = table.Column<string>(type: "jsonb", nullable: false),
                    SinaisParada = table.Column<string>(type: "jsonb", nullable: false),
                    CandidateResults = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositionCorrectionShadowOrigins", x => x.ShadowOriginId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PositionCorrectionShadowOrigins_ObservacaoId",
                table: "PositionCorrectionShadowOrigins",
                column: "ObservacaoId");

            migrationBuilder.CreateIndex(
                name: "IX_PositionCorrectionShadowOrigins_PolicyFingerprint_Timestamp~",
                table: "PositionCorrectionShadowOrigins",
                columns: new[] { "PolicyFingerprint", "TimestampGpsOrigemUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PositionCorrectionShadowOrigins_TimestampGpsOrigemUtc",
                table: "PositionCorrectionShadowOrigins",
                column: "TimestampGpsOrigemUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PositionCorrectionShadowOrigins");
        }
    }
}
