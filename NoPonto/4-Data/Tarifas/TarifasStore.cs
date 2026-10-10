using Microsoft.EntityFrameworkCore;

namespace NoPonto.Data.Tarifas;

/// <summary>Upserts atômicos, protegidos pelos índices parciais reais do PostgreSQL.</summary>
public sealed class TarifasStore(TransporteDbContext db)
{
    public Task<int> SalvarAsync(Guid? modalId, Guid? linhaId, decimal valor, string fonte, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        if (linhaId is not null)
            return db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Tarifas" ("Id", "ModalId", "LinhaId", "Valor", "Fonte", "CriadoEmUtc", "AtualizadoEmUtc")
                VALUES ({id}, NULL, {linhaId}, {valor}, {fonte}, {now}, {now})
                ON CONFLICT ("LinhaId") WHERE "LinhaId" IS NOT NULL DO UPDATE
                SET "Valor" = EXCLUDED."Valor", "Fonte" = EXCLUDED."Fonte", "AtualizadoEmUtc" = EXCLUDED."AtualizadoEmUtc"
                WHERE (EXCLUDED."Fonte" = 'MANUAL' OR "Tarifas"."Fonte" = 'ARCGIS_SPPO')
                  AND ("Tarifas"."Valor" <> EXCLUDED."Valor" OR "Tarifas"."Fonte" <> EXCLUDED."Fonte")
                """, ct);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Tarifas" ("Id", "ModalId", "LinhaId", "Valor", "Fonte", "CriadoEmUtc", "AtualizadoEmUtc")
            VALUES ({id}, {modalId}, NULL, {valor}, {fonte}, {now}, {now})
            ON CONFLICT ("ModalId") WHERE "ModalId" IS NOT NULL DO UPDATE
            SET "Valor" = EXCLUDED."Valor", "Fonte" = EXCLUDED."Fonte", "AtualizadoEmUtc" = EXCLUDED."AtualizadoEmUtc"
            WHERE "Tarifas"."Valor" <> EXCLUDED."Valor" OR "Tarifas"."Fonte" <> EXCLUDED."Fonte"
            """, ct);
    }

    public Task<int> VincularAsync(Guid? modalId, Guid? linhaId, Guid formaId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "FormasPagamentoVinculos" ("Id", "ModalId", "LinhaId", "FormaPagamentoId")
            VALUES ({Guid.NewGuid()}, {modalId}, {linhaId}, {formaId}) ON CONFLICT DO NOTHING
            """, ct);
}
