using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class EstruturaTransporteV21 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FontesEstruturais",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Codigo = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Nome = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FontesEstruturais", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportacoesEstruturais",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FonteEstruturalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    IniciadaEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcluidaEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VersaoFonte = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ConteudoHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RawUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    AlgoritmoVersao = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Relatorio = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportacoesEstruturais", x => x.Id);
                    table.CheckConstraint("CK_ImportacoesEstruturais_Status", "\"Status\" IN ('EM_PROCESSAMENTO','CONCLUIDA','FALHOU')");
                    table.ForeignKey(
                        name: "FK_ImportacoesEstruturais_FontesEstruturais_FonteEstruturalId",
                        column: x => x.FonteEstruturalId,
                        principalTable: "FontesEstruturais",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LinhasIdentidadesExternas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinhaId = table.Column<Guid>(type: "uuid", nullable: false),
                    FonteEstruturalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    OrigemMapeamento = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinhasIdentidadesExternas", x => x.Id);
                    table.CheckConstraint("CK_LinhasIdentidadesExternas_OrigemMapeamento", "\"OrigemMapeamento\" IN ('FONTE','MANUAL')");
                    table.ForeignKey(
                        name: "FK_LinhasIdentidadesExternas_FontesEstruturais_FonteEstrutural~",
                        column: x => x.FonteEstruturalId,
                        principalTable: "FontesEstruturais",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LinhasIdentidadesExternas_Linhas_LinhaId",
                        column: x => x.LinhaId,
                        principalTable: "Linhas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ParadasIdentidadesExternas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParadaId = table.Column<Guid>(type: "uuid", nullable: false),
                    FonteEstruturalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    OrigemMapeamento = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParadasIdentidadesExternas", x => x.Id);
                    table.CheckConstraint("CK_ParadasIdentidadesExternas_OrigemMapeamento", "\"OrigemMapeamento\" IN ('FONTE','MANUAL')");
                    table.ForeignKey(
                        name: "FK_ParadasIdentidadesExternas_FontesEstruturais_FonteEstrutura~",
                        column: x => x.FonteEstruturalId,
                        principalTable: "FontesEstruturais",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParadasIdentidadesExternas_Paradas_ParadaId",
                        column: x => x.ParadaId,
                        principalTable: "Paradas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SentidosIdentidadesExternas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SentidoId = table.Column<Guid>(type: "uuid", nullable: false),
                    FonteEstruturalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    OrigemMapeamento = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentidosIdentidadesExternas", x => x.Id);
                    table.CheckConstraint("CK_SentidosIdentidadesExternas_OrigemMapeamento", "\"OrigemMapeamento\" IN ('FONTE','MANUAL')");
                    table.ForeignKey(
                        name: "FK_SentidosIdentidadesExternas_FontesEstruturais_FonteEstrutur~",
                        column: x => x.FonteEstruturalId,
                        principalTable: "FontesEstruturais",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SentidosIdentidadesExternas_Sentidos_SentidoId",
                        column: x => x.SentidoId,
                        principalTable: "Sentidos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OcorrenciasParadasPadroes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PadraoVersaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParadaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordem = table.Column<int>(type: "integer", nullable: false),
                    SourceSequence = table.Column<int>(type: "integer", nullable: true),
                    PosicaoTracado = table.Column<double>(type: "double precision", nullable: false),
                    DistanciaAcumuladaMetros = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OcorrenciasParadasPadroes", x => x.Id);
                    table.CheckConstraint("CK_OcorrenciasPadroes_Distancia", "\"DistanciaAcumuladaMetros\" IS NULL OR \"DistanciaAcumuladaMetros\" >= 0");
                    table.CheckConstraint("CK_OcorrenciasPadroes_Ordem", "\"Ordem\" > 0");
                    table.CheckConstraint("CK_OcorrenciasPadroes_Posicao", "\"PosicaoTracado\" >= 0 AND \"PosicaoTracado\" <= 1");
                    table.CheckConstraint("CK_OcorrenciasPadroes_SourceSequence", "\"SourceSequence\" IS NULL OR \"SourceSequence\" >= 0");
                    table.ForeignKey(
                        name: "FK_OcorrenciasParadasPadroes_Paradas_ParadaId",
                        column: x => x.ParadaId,
                        principalTable: "Paradas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OverridesOcorrenciasPadroes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PadraoOperacionalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParadaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Acao = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OrdemDesejada = table.Column<int>(type: "integer", nullable: true),
                    Justificativa = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CriadoPor = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RevalidadoEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ObsoletoEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OverridesOcorrenciasPadroes", x => x.Id);
                    table.CheckConstraint("CK_OverridesOcorrencias_AcaoOrdem", "(\"Acao\" = 'EXCLUIR' AND \"OrdemDesejada\" IS NULL) OR (\"Acao\" IN ('INCLUIR','MOVER') AND \"OrdemDesejada\" > 0)");
                    table.ForeignKey(
                        name: "FK_OverridesOcorrenciasPadroes_Paradas_ParadaId",
                        column: x => x.ParadaId,
                        principalTable: "Paradas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PadroesIdentidadesExternas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PadraoOperacionalId = table.Column<Guid>(type: "uuid", nullable: false),
                    FonteEstruturalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    OrigemMapeamento = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PadroesIdentidadesExternas", x => x.Id);
                    table.CheckConstraint("CK_PadroesIdentidadesExternas_OrigemMapeamento", "\"OrigemMapeamento\" IN ('FONTE','MANUAL')");
                    table.ForeignKey(
                        name: "FK_PadroesIdentidadesExternas_FontesEstruturais_FonteEstrutura~",
                        column: x => x.FonteEstruturalId,
                        principalTable: "FontesEstruturais",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PadroesOperacionais",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SentidoId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersaoAtualId = table.Column<Guid>(type: "uuid", nullable: true),
                    Chave = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TipoServico = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NomePublico = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PadroesOperacionais", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PadroesOperacionais_Sentidos_SentidoId",
                        column: x => x.SentidoId,
                        principalTable: "Sentidos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PadroesVersoes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PadraoOperacionalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Numero = table.Column<int>(type: "integer", nullable: false),
                    Geometria = table.Column<LineString>(type: "geometry(LineString,4326)", nullable: false),
                    DistanciaMetros = table.Column<double>(type: "double precision", nullable: false),
                    MetodoConstrucao = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Confianca = table.Column<double>(type: "double precision", nullable: false),
                    AlgoritmoVersao = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ResultadoValidacao = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Relatorio = table.Column<string>(type: "jsonb", nullable: false),
                    CriadaEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublicadaEmUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PadroesVersoes", x => x.Id);
                    table.UniqueConstraint("AK_PadroesVersoes_PadraoOperacionalId_Id", x => new { x.PadraoOperacionalId, x.Id });
                    table.CheckConstraint("CK_PadroesVersoes_Confianca", "\"Confianca\" >= 0 AND \"Confianca\" <= 1");
                    table.CheckConstraint("CK_PadroesVersoes_Distancia", "\"DistanciaMetros\" >= 0");
                    table.CheckConstraint("CK_PadroesVersoes_Numero", "\"Numero\" > 0");
                    table.ForeignKey(
                        name: "FK_PadroesVersoes_PadroesOperacionais_PadraoOperacionalId",
                        column: x => x.PadraoOperacionalId,
                        principalTable: "PadroesOperacionais",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PadroesVersoesImportacoes",
                columns: table => new
                {
                    PadraoVersaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportacaoEstruturalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Papel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PadroesVersoesImportacoes", x => new { x.PadraoVersaoId, x.ImportacaoEstruturalId, x.Papel });
                    table.CheckConstraint("CK_PadroesVersoesImportacoes_Papel", "\"Papel\" IN ('MEMBERSHIP','GEOMETRIA','PARADAS','METADADOS')");
                    table.ForeignKey(
                        name: "FK_PadroesVersoesImportacoes_ImportacoesEstruturais_Importacao~",
                        column: x => x.ImportacaoEstruturalId,
                        principalTable: "ImportacoesEstruturais",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PadroesVersoesImportacoes_PadroesVersoes_PadraoVersaoId",
                        column: x => x.PadraoVersaoId,
                        principalTable: "PadroesVersoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FontesEstruturais_Codigo",
                table: "FontesEstruturais",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportacoesEstruturais_FonteEstruturalId_ConteudoHash_Algor~",
                table: "ImportacoesEstruturais",
                columns: new[] { "FonteEstruturalId", "ConteudoHash", "AlgoritmoVersao" },
                unique: true,
                filter: "\"Status\" = 'CONCLUIDA'");

            migrationBuilder.CreateIndex(
                name: "IX_ImportacoesEstruturais_FonteEstruturalId_IniciadaEmUtc",
                table: "ImportacoesEstruturais",
                columns: new[] { "FonteEstruturalId", "IniciadaEmUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LinhasIdentidadesExternas_FonteEstruturalId_Tipo_ExternalId",
                table: "LinhasIdentidadesExternas",
                columns: new[] { "FonteEstruturalId", "Tipo", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LinhasIdentidadesExternas_LinhaId",
                table: "LinhasIdentidadesExternas",
                column: "LinhaId");

            migrationBuilder.CreateIndex(
                name: "IX_OcorrenciasParadasPadroes_PadraoVersaoId_Ordem",
                table: "OcorrenciasParadasPadroes",
                columns: new[] { "PadraoVersaoId", "Ordem" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OcorrenciasParadasPadroes_ParadaId",
                table: "OcorrenciasParadasPadroes",
                column: "ParadaId");

            migrationBuilder.CreateIndex(
                name: "IX_OverridesOcorrenciasPadroes_PadraoOperacionalId_Ativo",
                table: "OverridesOcorrenciasPadroes",
                columns: new[] { "PadraoOperacionalId", "Ativo" });

            migrationBuilder.CreateIndex(
                name: "IX_OverridesOcorrenciasPadroes_ParadaId",
                table: "OverridesOcorrenciasPadroes",
                column: "ParadaId");

            migrationBuilder.CreateIndex(
                name: "IX_PadroesIdentidadesExternas_FonteEstruturalId_Tipo_ExternalId",
                table: "PadroesIdentidadesExternas",
                columns: new[] { "FonteEstruturalId", "Tipo", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PadroesIdentidadesExternas_PadraoOperacionalId",
                table: "PadroesIdentidadesExternas",
                column: "PadraoOperacionalId");

            migrationBuilder.CreateIndex(
                name: "IX_PadroesOperacionais_Id_VersaoAtualId",
                table: "PadroesOperacionais",
                columns: new[] { "Id", "VersaoAtualId" });

            migrationBuilder.CreateIndex(
                name: "IX_PadroesOperacionais_SentidoId_Chave",
                table: "PadroesOperacionais",
                columns: new[] { "SentidoId", "Chave" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PadroesVersoes_Geometria",
                table: "PadroesVersoes",
                column: "Geometria")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_PadroesVersoes_PadraoOperacionalId_Numero",
                table: "PadroesVersoes",
                columns: new[] { "PadraoOperacionalId", "Numero" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PadroesVersoesImportacoes_ImportacaoEstruturalId",
                table: "PadroesVersoesImportacoes",
                column: "ImportacaoEstruturalId");

            migrationBuilder.CreateIndex(
                name: "IX_ParadasIdentidadesExternas_FonteEstruturalId_Tipo_ExternalId",
                table: "ParadasIdentidadesExternas",
                columns: new[] { "FonteEstruturalId", "Tipo", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParadasIdentidadesExternas_ParadaId",
                table: "ParadasIdentidadesExternas",
                column: "ParadaId");

            migrationBuilder.CreateIndex(
                name: "IX_SentidosIdentidadesExternas_FonteEstruturalId_Tipo_External~",
                table: "SentidosIdentidadesExternas",
                columns: new[] { "FonteEstruturalId", "Tipo", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentidosIdentidadesExternas_SentidoId",
                table: "SentidosIdentidadesExternas",
                column: "SentidoId");

            migrationBuilder.AddForeignKey(
                name: "FK_OcorrenciasParadasPadroes_PadroesVersoes_PadraoVersaoId",
                table: "OcorrenciasParadasPadroes",
                column: "PadraoVersaoId",
                principalTable: "PadroesVersoes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_OverridesOcorrenciasPadroes_PadroesOperacionais_PadraoOpera~",
                table: "OverridesOcorrenciasPadroes",
                column: "PadraoOperacionalId",
                principalTable: "PadroesOperacionais",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PadroesIdentidadesExternas_PadroesOperacionais_PadraoOperac~",
                table: "PadroesIdentidadesExternas",
                column: "PadraoOperacionalId",
                principalTable: "PadroesOperacionais",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PadroesOperacionais_PadroesVersoes_Id_VersaoAtualId",
                table: "PadroesOperacionais",
                columns: new[] { "Id", "VersaoAtualId" },
                principalTable: "PadroesVersoes",
                principalColumns: new[] { "PadraoOperacionalId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PadroesOperacionais_PadroesVersoes_Id_VersaoAtualId",
                table: "PadroesOperacionais");

            migrationBuilder.DropTable(
                name: "LinhasIdentidadesExternas");

            migrationBuilder.DropTable(
                name: "OcorrenciasParadasPadroes");

            migrationBuilder.DropTable(
                name: "OverridesOcorrenciasPadroes");

            migrationBuilder.DropTable(
                name: "PadroesIdentidadesExternas");

            migrationBuilder.DropTable(
                name: "PadroesVersoesImportacoes");

            migrationBuilder.DropTable(
                name: "ParadasIdentidadesExternas");

            migrationBuilder.DropTable(
                name: "SentidosIdentidadesExternas");

            migrationBuilder.DropTable(
                name: "ImportacoesEstruturais");

            migrationBuilder.DropTable(
                name: "FontesEstruturais");

            migrationBuilder.DropTable(
                name: "PadroesVersoes");

            migrationBuilder.DropTable(
                name: "PadroesOperacionais");
        }
    }
}
