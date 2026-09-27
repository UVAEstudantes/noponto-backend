using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class EtaV2Foundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrevisoesEtaV2",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrdemVeiculo = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ViagemId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimestampGps = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TimestampPrevisao = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LinhaId = table.Column<Guid>(type: "uuid", nullable: false),
                    SentidoId = table.Column<Guid>(type: "uuid", nullable: false),
                    PadraoOperacionalId = table.Column<Guid>(type: "uuid", nullable: false),
                    PadraoVersaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    OcorrenciaParadaPadraoId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrdemOcorrencia = table.Column<int>(type: "integer", nullable: false),
                    Volta = table.Column<int>(type: "integer", nullable: false),
                    PosicaoNaRota = table.Column<double>(type: "double precision", nullable: false),
                    DistanciaRestanteRotaMetros = table.Column<double>(type: "double precision", nullable: false),
                    VelocidadeAtualKmh = table.Column<double>(type: "double precision", nullable: false),
                    Bearing = table.Column<double>(type: "double precision", nullable: true),
                    Modal = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Provedor = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    EtaPrevistoSegundos = table.Column<double>(type: "double precision", nullable: true),
                    Preditor = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    VersaoPreditor = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MotivoSemPrevisao = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    TimestampPassagemReal = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EtaRealSegundos = table.Column<double>(type: "double precision", nullable: true),
                    ErroSegundos = table.Column<double>(type: "double precision", nullable: true),
                    ErroAbsolutoSegundos = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrevisoesEtaV2", x => x.Id);
                    table.CheckConstraint("CK_PrevisoesEtaV2_Contexto", "\"OrdemOcorrencia\" > 0 AND \"Volta\" >= 0 AND \"PosicaoNaRota\" >= 0 AND \"PosicaoNaRota\" <= 1 AND \"DistanciaRestanteRotaMetros\" >= 0");
                    table.CheckConstraint("CK_PrevisoesEtaV2_GroundTruth", "(\"Status\" = 'REALIZADA' AND \"TimestampPassagemReal\" IS NOT NULL AND \"EtaRealSegundos\" IS NOT NULL) OR (\"Status\" <> 'REALIZADA' AND \"TimestampPassagemReal\" IS NULL AND \"EtaRealSegundos\" IS NULL AND \"ErroSegundos\" IS NULL AND \"ErroAbsolutoSegundos\" IS NULL)");
                    table.CheckConstraint("CK_PrevisoesEtaV2_Predicao", "(\"EtaPrevistoSegundos\" IS NULL) = (\"MotivoSemPrevisao\" IS NOT NULL) AND (\"EtaPrevistoSegundos\" IS NULL OR \"EtaPrevistoSegundos\" >= 0)");
                    table.CheckConstraint("CK_PrevisoesEtaV2_Status", "\"Status\" IN ('PENDENTE','REALIZADA','EXPIRADA','INVALIDADA')");
                    table.ForeignKey(
                        name: "FK_PrevisoesEtaV2_Linhas_LinhaId",
                        column: x => x.LinhaId,
                        principalTable: "Linhas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrevisoesEtaV2_OcorrenciasParadasPadroes_OcorrenciaParadaPa~",
                        column: x => x.OcorrenciaParadaPadraoId,
                        principalTable: "OcorrenciasParadasPadroes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrevisoesEtaV2_PadroesOperacionais_PadraoOperacionalId",
                        column: x => x.PadraoOperacionalId,
                        principalTable: "PadroesOperacionais",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrevisoesEtaV2_PadroesVersoes_PadraoVersaoId",
                        column: x => x.PadraoVersaoId,
                        principalTable: "PadroesVersoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrevisoesEtaV2_Sentidos_SentidoId",
                        column: x => x.SentidoId,
                        principalTable: "Sentidos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrevisoesEtaV2_LinhaId_TimestampPrevisao",
                table: "PrevisoesEtaV2",
                columns: new[] { "LinhaId", "TimestampPrevisao" });

            migrationBuilder.CreateIndex(
                name: "IX_PrevisoesEtaV2_OcorrenciaParadaPadraoId",
                table: "PrevisoesEtaV2",
                column: "OcorrenciaParadaPadraoId");

            migrationBuilder.CreateIndex(
                name: "IX_PrevisoesEtaV2_OrdemVeiculo_ViagemId_OcorrenciaParadaPadrao~",
                table: "PrevisoesEtaV2",
                columns: new[] { "OrdemVeiculo", "ViagemId", "OcorrenciaParadaPadraoId", "Volta", "TimestampPrevisao" });

            migrationBuilder.CreateIndex(
                name: "IX_PrevisoesEtaV2_OrdemVeiculo_ViagemId_PadraoVersaoId_Ocorren~",
                table: "PrevisoesEtaV2",
                columns: new[] { "OrdemVeiculo", "ViagemId", "PadraoVersaoId", "OcorrenciaParadaPadraoId", "Volta", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PrevisoesEtaV2_PadraoOperacionalId",
                table: "PrevisoesEtaV2",
                column: "PadraoOperacionalId");

            migrationBuilder.CreateIndex(
                name: "IX_PrevisoesEtaV2_PadraoVersaoId",
                table: "PrevisoesEtaV2",
                column: "PadraoVersaoId");

            migrationBuilder.CreateIndex(
                name: "IX_PrevisoesEtaV2_SentidoId",
                table: "PrevisoesEtaV2",
                column: "SentidoId");

            migrationBuilder.CreateIndex(
                name: "IX_PrevisoesEtaV2_Status_Preditor_VersaoPreditor_TimestampPrev~",
                table: "PrevisoesEtaV2",
                columns: new[] { "Status", "Preditor", "VersaoPreditor", "TimestampPrevisao" });

            migrationBuilder.CreateIndex(
                name: "IX_PrevisoesEtaV2_TimestampPrevisao",
                table: "PrevisoesEtaV2",
                column: "TimestampPrevisao");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrevisoesEtaV2");
        }
    }
}
