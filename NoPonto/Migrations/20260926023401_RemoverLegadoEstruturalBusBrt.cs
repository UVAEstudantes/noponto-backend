using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class RemoverLegadoEstruturalBusBrt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ambiente de desenvolvimento: contratos v1 e snapshots antigos não são
            // convertidos artificialmente para identidades V2.
            migrationBuilder.Sql("""
                DELETE FROM "OutboxViagens"
                WHERE "Payload"->>'schema_version' IS DISTINCT FROM '2';
                DELETE FROM "EventosViagem"
                WHERE "Payload"->>'schema_version' IS DISTINCT FROM '2';
                DELETE FROM "HistoricoPassagens"
                WHERE "PadraoVersaoId" IS NULL OR "OcorrenciaParadaPadraoId" IS NULL OR "Volta" IS NULL;
                TRUNCATE TABLE "PositionCorrectionShadowOrigins";
                TRUNCATE TABLE "ViagensOperacionais";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_HistoricoPassagens_Itinerarios_ItinerarioId",
                table: "HistoricoPassagens");

            migrationBuilder.DropForeignKey(
                name: "FK_HistoricoPassagens_ParadasItinerario_ParadaItinerarioId",
                table: "HistoricoPassagens");

            migrationBuilder.DropIndex(
                name: "IX_HistoricoPassagens_CodigoLinha_ItinerarioId_TimestampGps",
                table: "HistoricoPassagens");

            migrationBuilder.DropIndex(
                name: "IX_HistoricoPassagens_ItinerarioId_TimestampPassagem",
                table: "HistoricoPassagens");

            migrationBuilder.DropIndex(
                name: "IX_HistoricoPassagens_PadraoVersaoId",
                table: "HistoricoPassagens");

            migrationBuilder.DropIndex(
                name: "IX_HistoricoPassagens_ParadaItinerarioId_TimestampPassagem",
                table: "HistoricoPassagens");

            migrationBuilder.DropIndex(
                name: "IX_HistoricoPassagens_ViagemId_ParadaItinerarioId",
                table: "HistoricoPassagens");

            migrationBuilder.DropColumn(
                name: "ItinerarioId",
                table: "TelemetriasVeiculoMl");

            migrationBuilder.DropColumn(
                name: "ItinerarioId",
                table: "PositionCorrectionShadowOrigins");

            migrationBuilder.DropColumn(
                name: "ItinerarioId",
                table: "HistoricoPassagens");

            migrationBuilder.DropColumn(
                name: "ParadaItinerarioId",
                table: "HistoricoPassagens");

            migrationBuilder.DropTable(
                name: "ParadasItinerario");

            migrationBuilder.DropTable(
                name: "Itinerarios");

            migrationBuilder.RenameColumn(
                name: "ProximaParadaItinerarioId",
                table: "TelemetriasVeiculoMl",
                newName: "ProximaOcorrenciaParadaPadraoId");

            migrationBuilder.AlterColumn<Guid>(
                name: "PadraoVersaoId",
                table: "PositionCorrectionShadowOrigins",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_CodigoLinha_PadraoVersaoId_TimestampGps",
                table: "HistoricoPassagens",
                columns: new[] { "CodigoLinha", "PadraoVersaoId", "TimestampGps" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_PadraoVersaoId_TimestampPassagem",
                table: "HistoricoPassagens",
                columns: new[] { "PadraoVersaoId", "TimestampPassagem" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HistoricoPassagens_CodigoLinha_PadraoVersaoId_TimestampGps",
                table: "HistoricoPassagens");

            migrationBuilder.DropIndex(
                name: "IX_HistoricoPassagens_PadraoVersaoId_TimestampPassagem",
                table: "HistoricoPassagens");

            migrationBuilder.RenameColumn(
                name: "ProximaOcorrenciaParadaPadraoId",
                table: "TelemetriasVeiculoMl",
                newName: "ProximaParadaItinerarioId");

            migrationBuilder.AddColumn<Guid>(
                name: "ItinerarioId",
                table: "TelemetriasVeiculoMl",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PadraoVersaoId",
                table: "PositionCorrectionShadowOrigins",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ItinerarioId",
                table: "PositionCorrectionShadowOrigins",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ItinerarioId",
                table: "HistoricoPassagens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParadaItinerarioId",
                table: "HistoricoPassagens",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Itinerarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SentidoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DistanciaMetros = table.Column<double>(type: "double precision", nullable: false),
                    Geometria = table.Column<LineString>(type: "geometry(LineString,4326)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Itinerarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Itinerarios_Sentidos_SentidoId",
                        column: x => x.SentidoId,
                        principalTable: "Sentidos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ParadasItinerario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItinerarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParadaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DistanciaMetros = table.Column<double>(type: "double precision", nullable: false),
                    Fonte = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "SPATIAL_LEGACY"),
                    ImportacaoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Ordem = table.Column<int>(type: "integer", nullable: false),
                    PosicaoLinha = table.Column<double>(type: "double precision", nullable: false),
                    SourceShapeDistTraveledMetros = table.Column<double>(type: "double precision", nullable: true),
                    SourceStopSequence = table.Column<int>(type: "integer", nullable: true),
                    SubstituidaPorImportacaoId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParadasItinerario", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParadasItinerario_Itinerarios_ItinerarioId",
                        column: x => x.ItinerarioId,
                        principalTable: "Itinerarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ParadasItinerario_Paradas_ParadaId",
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
                name: "IX_HistoricoPassagens_ItinerarioId_TimestampPassagem",
                table: "HistoricoPassagens",
                columns: new[] { "ItinerarioId", "TimestampPassagem" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_PadraoVersaoId",
                table: "HistoricoPassagens",
                column: "PadraoVersaoId");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_ParadaItinerarioId_TimestampPassagem",
                table: "HistoricoPassagens",
                columns: new[] { "ParadaItinerarioId", "TimestampPassagem" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_ViagemId_ParadaItinerarioId",
                table: "HistoricoPassagens",
                columns: new[] { "ViagemId", "ParadaItinerarioId" },
                unique: true,
                filter: "\"ViagemId\" IS NOT NULL AND \"ParadaItinerarioId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Itinerarios_Geometria",
                table: "Itinerarios",
                column: "Geometria")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_Itinerarios_SentidoId",
                table: "Itinerarios",
                column: "SentidoId");

            migrationBuilder.CreateIndex(
                name: "IX_ParadasItinerario_ImportacaoId",
                table: "ParadasItinerario",
                column: "ImportacaoId");

            migrationBuilder.CreateIndex(
                name: "IX_ParadasItinerario_ItinerarioId",
                table: "ParadasItinerario",
                column: "ItinerarioId");

            migrationBuilder.CreateIndex(
                name: "IX_ParadasItinerario_ItinerarioId_Ordem",
                table: "ParadasItinerario",
                columns: new[] { "ItinerarioId", "Ordem" },
                unique: true,
                filter: "\"Ativo\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_ParadasItinerario_ParadaId",
                table: "ParadasItinerario",
                column: "ParadaId");

            migrationBuilder.CreateIndex(
                name: "IX_ParadasItinerario_SubstituidaPorImportacaoId",
                table: "ParadasItinerario",
                column: "SubstituidaPorImportacaoId");

            migrationBuilder.AddForeignKey(
                name: "FK_HistoricoPassagens_Itinerarios_ItinerarioId",
                table: "HistoricoPassagens",
                column: "ItinerarioId",
                principalTable: "Itinerarios",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_HistoricoPassagens_ParadasItinerario_ParadaItinerarioId",
                table: "HistoricoPassagens",
                column: "ParadaItinerarioId",
                principalTable: "ParadasItinerario",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
