using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NoPonto.Migrations;

[DbContext(typeof(TransporteDbContext))]
[Migration("20261116180000_RadarProgramacoes")]
public sealed class RadarProgramacoes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE "RadarProgramacoes" (
            "Id" uuid PRIMARY KEY,
            "InstalacaoId" uuid NOT NULL,
            "Nome" varchar(80) NOT NULL,
            "Configuracao" jsonb NOT NULL,
            "Fuso" varchar(100) NOT NULL,
            "HoraLocal" time NOT NULL,
            "DataLocal" date NULL,
            "Dias" integer[] NOT NULL,
            "Estado" varchar(12) NOT NULL DEFAULT 'ativa',
            "ProximaExecucaoUtc" timestamptz NULL,
            "UltimaOcorrenciaUtc" timestamptz NULL,
            "UltimoResultado" varchar(100) NULL,
            "Revisao" bigint NOT NULL DEFAULT 1,
            CONSTRAINT "CK_RadarProgramacoes_Estado" CHECK ("Estado" IN ('ativa','pausada','concluida','erro')),
            CONSTRAINT "CK_RadarProgramacoes_Configuracao" CHECK (jsonb_typeof("Configuracao") = 'object'),
            CONSTRAINT "CK_RadarProgramacoes_Recorrencia" CHECK (
                ("DataLocal" IS NOT NULL AND cardinality("Dias") = 0) OR
                ("DataLocal" IS NULL AND cardinality("Dias") BETWEEN 1 AND 7 AND "Dias" <@ ARRAY[0,1,2,3,4,5,6]))
        );
        CREATE INDEX "IX_RadarProgramacoes_InstalacaoId" ON "RadarProgramacoes" ("InstalacaoId");
        CREATE INDEX "IX_RadarProgramacoes_Vencimentos" ON "RadarProgramacoes" ("ProximaExecucaoUtc", "Id")
            WHERE "Estado" = 'ativa';
        CREATE TABLE "RadarProgramacaoExecucoes" (
            "ProgramacaoId" uuid NOT NULL REFERENCES "RadarProgramacoes" ("Id") ON DELETE CASCADE,
            "OcorrenciaUtc" timestamptz NOT NULL,
            "SessaoId" uuid NOT NULL UNIQUE,
            "Resultado" varchar(100) NOT NULL,
            PRIMARY KEY ("ProgramacaoId", "OcorrenciaUtc")
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new InvalidOperationException("Preservar programações e ocorrências; rollback somente do código.");
}
