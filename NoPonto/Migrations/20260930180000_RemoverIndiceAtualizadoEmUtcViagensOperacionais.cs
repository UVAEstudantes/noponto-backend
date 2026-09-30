using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NoPonto.Migrations;

[DbContext(typeof(TransporteDbContext))]
[Migration("20260930180000_RemoverIndiceAtualizadoEmUtcViagensOperacionais")]
public sealed class RemoverIndiceAtualizadoEmUtcViagensOperacionais : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS "IX_ViagensOperacionais_AtualizadoEmUtc";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE INDEX IF NOT EXISTS "IX_ViagensOperacionais_AtualizadoEmUtc"
                ON "ViagensOperacionais" ("AtualizadoEmUtc");
            """);
    }
}
