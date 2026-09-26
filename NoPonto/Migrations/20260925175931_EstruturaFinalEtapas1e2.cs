using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NoPonto.Migrations
{
    /// <inheritdoc />
    public partial class EstruturaFinalEtapas1e2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PadroesVersoes_Distancia",
                table: "PadroesVersoes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OcorrenciasPadroes_Distancia",
                table: "OcorrenciasParadasPadroes");

            migrationBuilder.RenameColumn(
                name: "DistanciaMetros",
                table: "PadroesVersoes",
                newName: "ComprimentoMetros");

            migrationBuilder.RenameColumn(
                name: "CriadaEmUtc",
                table: "PadroesVersoes",
                newName: "CriadoEmUtc");

            migrationBuilder.RenameColumn(
                name: "PublicadaEmUtc",
                table: "PadroesVersoes",
                newName: "PublicadoEmUtc");

            migrationBuilder.AddColumn<Guid>(
                name: "OcorrenciaParadaPadraoId",
                table: "TelemetriasVeiculoMl",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PadraoVersaoId",
                table: "TelemetriasVeiculoMl",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Volta",
                table: "TelemetriasVeiculoMl",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Confianca",
                table: "SentidosIdentidadesExternas",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Justificativa",
                table: "SentidosIdentidadesExternas",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OcorrenciaParadaPadraoId",
                table: "PositionCorrectionShadowOrigins",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PadraoVersaoId",
                table: "PositionCorrectionShadowOrigins",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Volta",
                table: "PositionCorrectionShadowOrigins",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Confianca",
                table: "ParadasIdentidadesExternas",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Justificativa",
                table: "ParadasIdentidadesExternas",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChaveCanonica",
                table: "Paradas",
                type: "character varying(240)",
                maxLength: 240,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ModalId",
                table: "Paradas",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParadaPaiId",
                table: "Paradas",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Plataforma",
                table: "Paradas",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TipoLocal",
                table: "Paradas",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "PARADA");

            migrationBuilder.AddColumn<string>(
                name: "HashEstrutural",
                table: "PadroesVersoes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Topologia",
                table: "PadroesVersoes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "LINEAR");

            migrationBuilder.AddColumn<double>(
                name: "Confianca",
                table: "PadroesIdentidadesExternas",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Justificativa",
                table: "PadroesIdentidadesExternas",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "DistanciaAcumuladaMetros",
                table: "OcorrenciasParadasPadroes",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "double precision",
                oldNullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DistanciaDaLinhaMetros",
                table: "OcorrenciasParadasPadroes",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "SourceShapeDistTraveledMetros",
                table: "OcorrenciasParadasPadroes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Confianca",
                table: "LinhasIdentidadesExternas",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Justificativa",
                table: "LinhasIdentidadesExternas",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OcorrenciaParadaPadraoId",
                table: "HistoricoPassagens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PadraoVersaoId",
                table: "HistoricoPassagens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Volta",
                table: "HistoricoPassagens",
                type: "integer",
                nullable: true);

            // Compatibilidade aditiva com candidatas V2 já existentes. Os derivados são
            // recalculados da geometria/parada; o valor antigo fica apenas como proveniência.
            migrationBuilder.Sql("""
                UPDATE "PadroesVersoes"
                SET "Topologia" = CASE
                    WHEN COALESCE(("Relatorio"->>'Loop')::boolean, false) OR ST_IsClosed("Geometria")
                    THEN 'CIRCULAR' ELSE 'LINEAR' END;

                UPDATE "OcorrenciasParadasPadroes" o
                SET "SourceShapeDistTraveledMetros" = o."DistanciaAcumuladaMetros",
                    "DistanciaAcumuladaMetros" = o."PosicaoTracado" * v."ComprimentoMetros",
                    "DistanciaDaLinhaMetros" = ST_Distance(p."Localizacao"::geography, v."Geometria"::geography)
                FROM "PadroesVersoes" v, "Paradas" p
                WHERE v."Id" = o."PadraoVersaoId" AND p."Id" = o."ParadaId";

                UPDATE "PadroesVersoes" v
                SET "HashEstrutural" = md5(
                    encode(ST_AsEWKB(v."Geometria"), 'hex') || '|' || v."Topologia" || '|' ||
                    COALESCE((SELECT string_agg(
                        o."Ordem"::text || ':' || o."ParadaId"::text || ':' ||
                        round(o."PosicaoTracado"::numeric, 6)::text || ':' ||
                        round(o."DistanciaAcumuladaMetros"::numeric, 6)::text || ':' ||
                        round(o."DistanciaDaLinhaMetros"::numeric, 6)::text, ';' ORDER BY o."Ordem")
                    FROM "OcorrenciasParadasPadroes" o WHERE o."PadraoVersaoId" = v."Id"), '')
                ) || md5('ESTRUTURA_FINAL_V1|' ||
                    encode(ST_AsEWKB(v."Geometria"), 'hex') || '|' || v."Topologia" || '|' ||
                    COALESCE((SELECT string_agg(o."Ordem"::text || ':' || o."ParadaId"::text,
                        ';' ORDER BY o."Ordem") FROM "OcorrenciasParadasPadroes" o
                        WHERE o."PadraoVersaoId" = v."Id"), ''));
                """);

            migrationBuilder.AlterColumn<string>(
                name: "HashEstrutural", table: "PadroesVersoes", type: "character varying(64)",
                maxLength: 64, nullable: false, oldClrType: typeof(string),
                oldType: "character varying(64)", oldMaxLength: 64, oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelemetriasVeiculoMl_OcorrenciaParadaPadraoId",
                table: "TelemetriasVeiculoMl",
                column: "OcorrenciaParadaPadraoId");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetriasVeiculoMl_PadraoVersaoId",
                table: "TelemetriasVeiculoMl",
                column: "PadraoVersaoId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SentidosIdentidadesExternas_Confianca",
                table: "SentidosIdentidadesExternas",
                sql: "\"Confianca\" IS NULL OR (\"Confianca\" >= 0 AND \"Confianca\" <= 1)");

            migrationBuilder.CreateIndex(
                name: "IX_PositionCorrectionShadowOrigins_OcorrenciaParadaPadraoId",
                table: "PositionCorrectionShadowOrigins",
                column: "OcorrenciaParadaPadraoId");

            migrationBuilder.CreateIndex(
                name: "IX_PositionCorrectionShadowOrigins_PadraoVersaoId",
                table: "PositionCorrectionShadowOrigins",
                column: "PadraoVersaoId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ParadasIdentidadesExternas_Confianca",
                table: "ParadasIdentidadesExternas",
                sql: "\"Confianca\" IS NULL OR (\"Confianca\" >= 0 AND \"Confianca\" <= 1)");

            migrationBuilder.CreateIndex(
                name: "IX_Paradas_ChaveCanonica",
                table: "Paradas",
                column: "ChaveCanonica",
                unique: true,
                filter: "\"ChaveCanonica\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Paradas_ModalId",
                table: "Paradas",
                column: "ModalId");

            migrationBuilder.CreateIndex(
                name: "IX_Paradas_ParadaPaiId",
                table: "Paradas",
                column: "ParadaPaiId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Paradas_TipoLocal",
                table: "Paradas",
                sql: "\"TipoLocal\" IN ('PARADA','PLATAFORMA','ESTACAO')");

            migrationBuilder.CreateIndex(
                name: "IX_PadroesVersoes_PadraoOperacionalId_HashEstrutural",
                table: "PadroesVersoes",
                columns: new[] { "PadraoOperacionalId", "HashEstrutural" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PadroesVersoes_Comprimento",
                table: "PadroesVersoes",
                sql: "\"ComprimentoMetros\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PadroesVersoes_Topologia",
                table: "PadroesVersoes",
                sql: "\"Topologia\" IN ('LINEAR','CIRCULAR')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PadroesIdentidadesExternas_Confianca",
                table: "PadroesIdentidadesExternas",
                sql: "\"Confianca\" IS NULL OR (\"Confianca\" >= 0 AND \"Confianca\" <= 1)");

            migrationBuilder.CreateIndex(
                name: "IX_OcorrenciasParadasPadroes_PadraoVersaoId_PosicaoTracado",
                table: "OcorrenciasParadasPadroes",
                columns: new[] { "PadraoVersaoId", "PosicaoTracado" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OcorrenciasPadroes_Distancias",
                table: "OcorrenciasParadasPadroes",
                sql: "\"DistanciaAcumuladaMetros\" >= 0 AND \"DistanciaDaLinhaMetros\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LinhasIdentidadesExternas_Confianca",
                table: "LinhasIdentidadesExternas",
                sql: "\"Confianca\" IS NULL OR (\"Confianca\" >= 0 AND \"Confianca\" <= 1)");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_OcorrenciaParadaPadraoId",
                table: "HistoricoPassagens",
                column: "OcorrenciaParadaPadraoId");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoPassagens_PadraoVersaoId",
                table: "HistoricoPassagens",
                column: "PadraoVersaoId");

            migrationBuilder.AddForeignKey(
                name: "FK_HistoricoPassagens_OcorrenciasParadasPadroes_OcorrenciaPara~",
                table: "HistoricoPassagens",
                column: "OcorrenciaParadaPadraoId",
                principalTable: "OcorrenciasParadasPadroes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HistoricoPassagens_PadroesVersoes_PadraoVersaoId",
                table: "HistoricoPassagens",
                column: "PadraoVersaoId",
                principalTable: "PadroesVersoes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Paradas_Modais_ModalId",
                table: "Paradas",
                column: "ModalId",
                principalTable: "Modais",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Paradas_Paradas_ParadaPaiId",
                table: "Paradas",
                column: "ParadaPaiId",
                principalTable: "Paradas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PositionCorrectionShadowOrigins_OcorrenciasParadasPadroes_O~",
                table: "PositionCorrectionShadowOrigins",
                column: "OcorrenciaParadaPadraoId",
                principalTable: "OcorrenciasParadasPadroes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PositionCorrectionShadowOrigins_PadroesVersoes_PadraoVersao~",
                table: "PositionCorrectionShadowOrigins",
                column: "PadraoVersaoId",
                principalTable: "PadroesVersoes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TelemetriasVeiculoMl_OcorrenciasParadasPadroes_OcorrenciaPa~",
                table: "TelemetriasVeiculoMl",
                column: "OcorrenciaParadaPadraoId",
                principalTable: "OcorrenciasParadasPadroes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TelemetriasVeiculoMl_PadroesVersoes_PadraoVersaoId",
                table: "TelemetriasVeiculoMl",
                column: "PadraoVersaoId",
                principalTable: "PadroesVersoes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "BloquearMutacaoVersaoPublicada"() RETURNS trigger AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "PadroesOperacionais" p
                               WHERE p."VersaoAtualId" = OLD."Id") THEN
                        IF TG_OP = 'UPDATE' AND OLD."PublicadoEmUtc" IS NULL
                           AND NEW."PublicadoEmUtc" IS NOT NULL
                           AND NEW."PadraoOperacionalId" = OLD."PadraoOperacionalId"
                           AND NEW."Numero" = OLD."Numero"
                           AND NEW."Geometria" = OLD."Geometria"
                           AND NEW."Topologia" = OLD."Topologia"
                           AND NEW."ComprimentoMetros" = OLD."ComprimentoMetros"
                           AND NEW."HashEstrutural" = OLD."HashEstrutural"
                           AND NEW."MetodoConstrucao" = OLD."MetodoConstrucao"
                           AND NEW."Confianca" = OLD."Confianca"
                           AND NEW."AlgoritmoVersao" = OLD."AlgoritmoVersao"
                           AND NEW."ResultadoValidacao" = OLD."ResultadoValidacao"
                           AND NEW."Relatorio" = OLD."Relatorio"
                           AND NEW."CriadoEmUtc" = OLD."CriadoEmUtc" THEN
                            RETURN NEW;
                        END IF;
                        RAISE EXCEPTION 'Versão estrutural publicada é imutável';
                    END IF;
                    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER "TR_PadroesVersoes_Imutavel"
                    BEFORE UPDATE OR DELETE ON "PadroesVersoes"
                    FOR EACH ROW EXECUTE FUNCTION "BloquearMutacaoVersaoPublicada"();

                CREATE OR REPLACE FUNCTION "BloquearMutacaoOcorrenciaPublicada"() RETURNS trigger AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "PadroesOperacionais" p
                               WHERE p."VersaoAtualId" = OLD."PadraoVersaoId") THEN
                        RAISE EXCEPTION 'Ocorrências de versão publicada são imutáveis';
                    END IF;
                    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER "TR_OcorrenciasPadroes_Imutavel"
                    BEFORE UPDATE OR DELETE ON "OcorrenciasParadasPadroes"
                    FOR EACH ROW EXECUTE FUNCTION "BloquearMutacaoOcorrenciaPublicada"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_OcorrenciasPadroes_Imutavel" ON "OcorrenciasParadasPadroes";
                DROP FUNCTION IF EXISTS "BloquearMutacaoOcorrenciaPublicada"();
                DROP TRIGGER IF EXISTS "TR_PadroesVersoes_Imutavel" ON "PadroesVersoes";
                DROP FUNCTION IF EXISTS "BloquearMutacaoVersaoPublicada"();
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_HistoricoPassagens_OcorrenciasParadasPadroes_OcorrenciaPara~",
                table: "HistoricoPassagens");

            migrationBuilder.DropForeignKey(
                name: "FK_HistoricoPassagens_PadroesVersoes_PadraoVersaoId",
                table: "HistoricoPassagens");

            migrationBuilder.DropForeignKey(
                name: "FK_Paradas_Modais_ModalId",
                table: "Paradas");

            migrationBuilder.DropForeignKey(
                name: "FK_Paradas_Paradas_ParadaPaiId",
                table: "Paradas");

            migrationBuilder.DropForeignKey(
                name: "FK_PositionCorrectionShadowOrigins_OcorrenciasParadasPadroes_O~",
                table: "PositionCorrectionShadowOrigins");

            migrationBuilder.DropForeignKey(
                name: "FK_PositionCorrectionShadowOrigins_PadroesVersoes_PadraoVersao~",
                table: "PositionCorrectionShadowOrigins");

            migrationBuilder.DropForeignKey(
                name: "FK_TelemetriasVeiculoMl_OcorrenciasParadasPadroes_OcorrenciaPa~",
                table: "TelemetriasVeiculoMl");

            migrationBuilder.DropForeignKey(
                name: "FK_TelemetriasVeiculoMl_PadroesVersoes_PadraoVersaoId",
                table: "TelemetriasVeiculoMl");

            migrationBuilder.DropIndex(
                name: "IX_TelemetriasVeiculoMl_OcorrenciaParadaPadraoId",
                table: "TelemetriasVeiculoMl");

            migrationBuilder.DropIndex(
                name: "IX_TelemetriasVeiculoMl_PadraoVersaoId",
                table: "TelemetriasVeiculoMl");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SentidosIdentidadesExternas_Confianca",
                table: "SentidosIdentidadesExternas");

            migrationBuilder.DropIndex(
                name: "IX_PositionCorrectionShadowOrigins_OcorrenciaParadaPadraoId",
                table: "PositionCorrectionShadowOrigins");

            migrationBuilder.DropIndex(
                name: "IX_PositionCorrectionShadowOrigins_PadraoVersaoId",
                table: "PositionCorrectionShadowOrigins");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ParadasIdentidadesExternas_Confianca",
                table: "ParadasIdentidadesExternas");

            migrationBuilder.DropIndex(
                name: "IX_Paradas_ChaveCanonica",
                table: "Paradas");

            migrationBuilder.DropIndex(
                name: "IX_Paradas_ModalId",
                table: "Paradas");

            migrationBuilder.DropIndex(
                name: "IX_Paradas_ParadaPaiId",
                table: "Paradas");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Paradas_TipoLocal",
                table: "Paradas");

            migrationBuilder.DropIndex(
                name: "IX_PadroesVersoes_PadraoOperacionalId_HashEstrutural",
                table: "PadroesVersoes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PadroesVersoes_Comprimento",
                table: "PadroesVersoes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PadroesVersoes_Topologia",
                table: "PadroesVersoes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PadroesIdentidadesExternas_Confianca",
                table: "PadroesIdentidadesExternas");

            migrationBuilder.DropIndex(
                name: "IX_OcorrenciasParadasPadroes_PadraoVersaoId_PosicaoTracado",
                table: "OcorrenciasParadasPadroes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OcorrenciasPadroes_Distancias",
                table: "OcorrenciasParadasPadroes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LinhasIdentidadesExternas_Confianca",
                table: "LinhasIdentidadesExternas");

            migrationBuilder.DropIndex(
                name: "IX_HistoricoPassagens_OcorrenciaParadaPadraoId",
                table: "HistoricoPassagens");

            migrationBuilder.DropIndex(
                name: "IX_HistoricoPassagens_PadraoVersaoId",
                table: "HistoricoPassagens");

            migrationBuilder.DropColumn(
                name: "OcorrenciaParadaPadraoId",
                table: "TelemetriasVeiculoMl");

            migrationBuilder.DropColumn(
                name: "PadraoVersaoId",
                table: "TelemetriasVeiculoMl");

            migrationBuilder.DropColumn(
                name: "Volta",
                table: "TelemetriasVeiculoMl");

            migrationBuilder.DropColumn(
                name: "Confianca",
                table: "SentidosIdentidadesExternas");

            migrationBuilder.DropColumn(
                name: "Justificativa",
                table: "SentidosIdentidadesExternas");

            migrationBuilder.DropColumn(
                name: "OcorrenciaParadaPadraoId",
                table: "PositionCorrectionShadowOrigins");

            migrationBuilder.DropColumn(
                name: "PadraoVersaoId",
                table: "PositionCorrectionShadowOrigins");

            migrationBuilder.DropColumn(
                name: "Volta",
                table: "PositionCorrectionShadowOrigins");

            migrationBuilder.DropColumn(
                name: "Confianca",
                table: "ParadasIdentidadesExternas");

            migrationBuilder.DropColumn(
                name: "Justificativa",
                table: "ParadasIdentidadesExternas");

            migrationBuilder.DropColumn(
                name: "ChaveCanonica",
                table: "Paradas");

            migrationBuilder.DropColumn(
                name: "ModalId",
                table: "Paradas");

            migrationBuilder.DropColumn(
                name: "ParadaPaiId",
                table: "Paradas");

            migrationBuilder.DropColumn(
                name: "Plataforma",
                table: "Paradas");

            migrationBuilder.DropColumn(
                name: "TipoLocal",
                table: "Paradas");

            migrationBuilder.DropColumn(
                name: "HashEstrutural",
                table: "PadroesVersoes");

            migrationBuilder.DropColumn(
                name: "Topologia",
                table: "PadroesVersoes");

            migrationBuilder.DropColumn(
                name: "Confianca",
                table: "PadroesIdentidadesExternas");

            migrationBuilder.DropColumn(
                name: "Justificativa",
                table: "PadroesIdentidadesExternas");

            migrationBuilder.DropColumn(
                name: "DistanciaDaLinhaMetros",
                table: "OcorrenciasParadasPadroes");

            migrationBuilder.DropColumn(
                name: "SourceShapeDistTraveledMetros",
                table: "OcorrenciasParadasPadroes");

            migrationBuilder.DropColumn(
                name: "Confianca",
                table: "LinhasIdentidadesExternas");

            migrationBuilder.DropColumn(
                name: "Justificativa",
                table: "LinhasIdentidadesExternas");

            migrationBuilder.DropColumn(
                name: "OcorrenciaParadaPadraoId",
                table: "HistoricoPassagens");

            migrationBuilder.DropColumn(
                name: "PadraoVersaoId",
                table: "HistoricoPassagens");

            migrationBuilder.DropColumn(
                name: "Volta",
                table: "HistoricoPassagens");

            migrationBuilder.RenameColumn(
                name: "CriadoEmUtc",
                table: "PadroesVersoes",
                newName: "CriadaEmUtc");

            migrationBuilder.RenameColumn(
                name: "PublicadoEmUtc",
                table: "PadroesVersoes",
                newName: "PublicadaEmUtc");

            migrationBuilder.RenameColumn(
                name: "ComprimentoMetros",
                table: "PadroesVersoes",
                newName: "DistanciaMetros");

            migrationBuilder.AlterColumn<double>(
                name: "DistanciaAcumuladaMetros",
                table: "OcorrenciasParadasPadroes",
                type: "double precision",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PadroesVersoes_Distancia",
                table: "PadroesVersoes",
                sql: "\"DistanciaMetros\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OcorrenciasPadroes_Distancia",
                table: "OcorrenciasParadasPadroes",
                sql: "\"DistanciaAcumuladaMetros\" IS NULL OR \"DistanciaAcumuladaMetros\" >= 0");
        }
    }
}
