using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NoPonto.Migrations;

[DbContext(typeof(TransporteDbContext))]
[Migration("20260924190000_ViagemDuravelPostgres")]
public sealed class ViagemDuravelPostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "ViagensOperacionais" (
                "OrdemVeiculo" text PRIMARY KEY,
                "Estado" jsonb NOT NULL,
                "Versao" bigint NOT NULL CHECK ("Versao" > 0),
                "AtualizadoEmUtc" timestamp with time zone NOT NULL
            );
            CREATE INDEX "IX_ViagensOperacionais_AtualizadoEmUtc"
                ON "ViagensOperacionais" ("AtualizadoEmUtc");

            CREATE TABLE "OutboxViagens" (
                "EventId" text PRIMARY KEY,
                "Tipo" text NOT NULL,
                "Payload" jsonb NOT NULL,
                "CriadoEmUtc" timestamp with time zone NOT NULL,
                "ProcessadoEmUtc" timestamp with time zone NULL,
                "Tentativas" integer NOT NULL DEFAULT 0 CHECK ("Tentativas" >= 0),
                "UltimoErro" text NULL,
                "ProximaTentativaEmUtc" timestamp with time zone NULL,
                "BloqueadoAteUtc" timestamp with time zone NULL,
                "BloqueadoPor" text NULL
            );
            CREATE INDEX "IX_OutboxViagens_Pendentes"
                ON "OutboxViagens" ("CriadoEmUtc", "EventId")
                WHERE "ProcessadoEmUtc" IS NULL;
            CREATE INDEX "IX_OutboxViagens_ProcessadoEmUtc"
                ON "OutboxViagens" ("ProcessadoEmUtc")
                WHERE "ProcessadoEmUtc" IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("OutboxViagens");
        migrationBuilder.DropTable("ViagensOperacionais");
    }
}
