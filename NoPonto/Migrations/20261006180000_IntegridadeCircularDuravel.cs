using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NoPonto.Migrations;

[DbContext(typeof(TransporteDbContext))]
[Migration("20261006180000_IntegridadeCircularDuravel")]
public sealed class IntegridadeCircularDuravel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "ViagensOperacionais" ADD COLUMN "IntegridadeCircular" jsonb NULL;
        ALTER TABLE "ViagensOperacionais" ADD CONSTRAINT "CK_ViagensOperacionais_IntegridadeCircular"
            CHECK ("IntegridadeCircular" IS NULL OR jsonb_typeof("IntegridadeCircular") = 'object');
        """);

    // Retirar esta extensão perde proteção de identidade; não usar Down como rollback operacional.
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new InvalidOperationException("Rollback deve preservar a integridade circular e usar binário compatível; remoção automática bloqueada.");
}
