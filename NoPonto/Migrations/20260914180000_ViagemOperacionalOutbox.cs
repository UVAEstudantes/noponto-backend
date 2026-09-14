using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NoPonto.Migrations;

[DbContext(typeof(TransporteDbContext))]
[Migration("20260914180000_ViagemOperacionalOutbox")]
public sealed partial class ViagemOperacionalOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_HistoricoPassagens_ItinerarioId", "HistoricoPassagens");
        migrationBuilder.AddColumn<Guid>("ViagemId", "HistoricoPassagens", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>("ParadaItinerarioId", "HistoricoPassagens", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>("SentidoId", "HistoricoPassagens", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("TimestampPassagem", "HistoricoPassagens", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AlterColumn<double>("DistanciaParadaMetros", "HistoricoPassagens", type: "double precision",
            nullable: true, oldClrType: typeof(double), oldType: "double precision");
        migrationBuilder.CreateIndex("IX_HistoricoPassagens_ViagemId_ParadaItinerarioId", "HistoricoPassagens",
            ["ViagemId", "ParadaItinerarioId"], unique: true,
            filter: "\"ViagemId\" IS NOT NULL AND \"ParadaItinerarioId\" IS NOT NULL");
        foreach (var column in new[] { "ViagemId", "ParadaItinerarioId", "SentidoId", "ItinerarioId", "Ordem", "CodigoLinha" })
            migrationBuilder.CreateIndex($"IX_HistoricoPassagens_{column}_TimestampPassagem", "HistoricoPassagens", [column, "TimestampPassagem"]);
        migrationBuilder.AddForeignKey("FK_HistoricoPassagens_ParadasItinerario_ParadaItinerarioId", "HistoricoPassagens",
            "ParadaItinerarioId", "ParadasItinerario", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey("FK_HistoricoPassagens_Sentidos_SentidoId", "HistoricoPassagens",
            "SentidoId", "Sentidos", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.CreateTable("EventosViagem", columns: table => new {
            EventId = table.Column<string>(type: "text", nullable: false),
            Tipo = table.Column<string>(type: "text", nullable: false),
            Payload = table.Column<string>(type: "jsonb", nullable: false),
            TimestampEvento = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_EventosViagem", e => e.EventId));
        migrationBuilder.CreateIndex("IX_EventosViagem_TimestampEvento", "EventosViagem", "TimestampEvento");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN IF EXISTS (SELECT 1 FROM "HistoricoPassagens" WHERE "DistanciaParadaMetros" IS NULL)
                THEN RAISE EXCEPTION 'Downgrade inseguro: há passagens sem distância'; END IF; END $$;
            """);
        migrationBuilder.DropTable("EventosViagem");
        migrationBuilder.DropForeignKey("FK_HistoricoPassagens_ParadasItinerario_ParadaItinerarioId", "HistoricoPassagens");
        migrationBuilder.DropForeignKey("FK_HistoricoPassagens_Sentidos_SentidoId", "HistoricoPassagens");
        migrationBuilder.DropIndex("IX_HistoricoPassagens_ViagemId_ParadaItinerarioId", "HistoricoPassagens");
        foreach (var column in new[] { "ViagemId", "ParadaItinerarioId", "SentidoId", "ItinerarioId", "Ordem", "CodigoLinha" })
            migrationBuilder.DropIndex($"IX_HistoricoPassagens_{column}_TimestampPassagem", "HistoricoPassagens");
        migrationBuilder.CreateIndex("IX_HistoricoPassagens_ItinerarioId", "HistoricoPassagens", "ItinerarioId");
        foreach (var column in new[] { "ViagemId", "ParadaItinerarioId", "SentidoId", "TimestampPassagem" })
            migrationBuilder.DropColumn(column, "HistoricoPassagens");
        // Downgrade com novas passagens sem distância é inseguro: falha em vez de inventar dados.
        migrationBuilder.AlterColumn<double>("DistanciaParadaMetros", "HistoricoPassagens", type: "double precision",
            nullable: false, oldClrType: typeof(double), oldType: "double precision", oldNullable: true);
    }
}
