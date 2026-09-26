using NoPonto.Application.GPS;
using NoPonto.Data.Interfaces;
using Npgsql;

namespace NoPonto.Data.Repositories;

/// <summary>Uma leitura SQL consistente; valida a sequência inteira da versão publicada no servidor.
/// Somente baseline, cruzamentos e próxima ocorrência atravessam a conexão.</summary>
public sealed class OcorrenciaParadaRepository(NpgsqlDataSource source) : IOcorrenciaParadaRepository
{
    internal const string Sql = """
        WITH sequencia AS MATERIALIZED (
            SELECT "Id", "PadraoVersaoId", "ParadaId", "Ordem", "PosicaoTracado",
                "DistanciaAcumuladaMetros", "DistanciaDaLinhaMetros",
                lag("Ordem") OVER w AS ordem_anterior,
                lag("PosicaoTracado") OVER w AS posicao_anterior
            FROM "OcorrenciasParadasPadroes" WHERE "PadraoVersaoId" = @versao
            WINDOW w AS (ORDER BY "Ordem", "Id")
        ), validacao AS (
            SELECT CASE
                WHEN EXISTS (SELECT 1 FROM sequencia WHERE "Ordem" <= 0
                    OR "Id" = '00000000-0000-0000-0000-000000000000'::uuid
                    OR NOT ("PosicaoTracado" >= 0 AND "PosicaoTracado" <= 1)
                    OR "Ordem" = ordem_anterior OR "PosicaoTracado" < posicao_anterior) THEN 8
                WHEN NOT @baseline AND @ultima_id <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND NOT EXISTS (SELECT 1 FROM sequencia WHERE "Id" = @ultima_id) THEN 9
                WHEN NOT @baseline AND ((@ultima_ordem = 0) <>
                    (@ultima_id = '00000000-0000-0000-0000-000000000000'::uuid)
                    OR (@ultima_ordem > 0 AND NOT EXISTS (SELECT 1 FROM sequencia
                        WHERE "Id" = @ultima_id AND "Ordem" = @ultima_ordem))) THEN 8
                ELSE 2 END AS status
        ), incorporadas AS (
            SELECT * FROM sequencia WHERE
                (@baseline AND "PosicaoTracado" <= @anterior)
                OR (NOT @baseline AND @atual >= @anterior AND "Ordem" > @ultima_ordem
                    AND "PosicaoTracado" > @anterior AND "PosicaoTracado" <= @atual)
                OR (NOT @baseline AND @circular AND @atual < @anterior
                    AND (("PosicaoTracado" > @anterior) OR ("PosicaoTracado" <= @atual)))
        ), cursor_final AS (
            SELECT CASE WHEN NOT @baseline AND @circular AND @atual < @anterior
                    THEN coalesce(max("Ordem") FILTER (WHERE "PosicaoTracado" <= @atual), 0)
                WHEN @baseline THEN coalesce(max("Ordem"), 0)
                ELSE greatest(@ultima_ordem, coalesce(max("Ordem"), 0)) END AS ordem
            FROM incorporadas
        ), selecionadas AS (
            SELECT 1 AS tipo, s."Id", s."PadraoVersaoId", s."ParadaId", s."Ordem", s."PosicaoTracado",
                s."DistanciaAcumuladaMetros", s."DistanciaDaLinhaMetros"
            FROM sequencia s JOIN cursor_final c ON s."Ordem" = c.ordem
            UNION ALL
            SELECT 2, "Id", "PadraoVersaoId", "ParadaId", "Ordem", "PosicaoTracado",
                "DistanciaAcumuladaMetros", "DistanciaDaLinhaMetros"
            FROM incorporadas WHERE NOT @baseline
            UNION ALL
            (SELECT 3, s."Id", s."PadraoVersaoId", s."ParadaId", s."Ordem", s."PosicaoTracado",
                s."DistanciaAcumuladaMetros", s."DistanciaDaLinhaMetros"
            FROM sequencia s, cursor_final c WHERE s."Ordem" > c.ordem
                OR (@circular AND NOT EXISTS (SELECT 1 FROM sequencia n WHERE n."Ordem" > c.ordem))
            ORDER BY CASE WHEN s."Ordem" > c.ordem THEN 0 ELSE 1 END, s."Ordem" LIMIT 1)
            UNION ALL
            (SELECT 4, s."Id", s."PadraoVersaoId", s."ParadaId", s."Ordem", s."PosicaoTracado",
                s."DistanciaAcumuladaMetros", s."DistanciaDaLinhaMetros"
            FROM sequencia s ORDER BY s."Ordem" DESC LIMIT 1)
        )
        SELECT v.status, s.tipo, s."Id", s."PadraoVersaoId", s."ParadaId", s."Ordem", s."PosicaoTracado",
            s."DistanciaAcumuladaMetros", s."DistanciaDaLinhaMetros"
        FROM validacao v LEFT JOIN selecionadas s ON v.status = 2 ORDER BY s.tipo, s."Ordem"
        """;

    public async Task<TransicaoParadas> BuscarTransicaoAsync(Guid padraoVersaoId, double posicaoAnterior,
        double posicaoAtual, Guid ultimaId, int ultimaOrdem, bool baseline, CancellationToken ct)
        => await BuscarTransicaoV2Async(padraoVersaoId, posicaoAnterior, posicaoAtual,
            ultimaId, ultimaOrdem, baseline, "LINEAR", 0, ct);

    public async Task<TransicaoParadas> BuscarTransicaoV2Async(Guid padraoVersaoId, double posicaoAnterior,
        double posicaoAtual, Guid ultimaId, int ultimaOrdem, bool baseline, string topologia,
        int volta, CancellationToken ct)
    {
        if (padraoVersaoId == Guid.Empty || !double.IsFinite(posicaoAnterior)
            || !double.IsFinite(posicaoAtual) || posicaoAnterior is < 0 or > 1
            || posicaoAtual is < 0 or > 1 || ultimaOrdem < 0)
            return new(ViagemObservadaStatus.InvalidSequence, ultimaId, ultimaOrdem, []);
        await using var connection = await source.OpenConnectionAsync(ct);
        return await BuscarTransicaoNaConexaoAsync(connection, null, padraoVersaoId,
            posicaoAnterior, posicaoAtual, ultimaId, ultimaOrdem, baseline, ct, topologia, volta);
    }

    internal static async Task<TransicaoParadas> BuscarTransicaoNaConexaoAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid padraoVersaoId, double posicaoAnterior,
        double posicaoAtual, Guid ultimaId, int ultimaOrdem, bool baseline, CancellationToken ct,
        string topologia = "LINEAR", int volta = 0)
    {
        if (padraoVersaoId == Guid.Empty || !double.IsFinite(posicaoAnterior)
            || !double.IsFinite(posicaoAtual) || posicaoAnterior is < 0 or > 1
            || posicaoAtual is < 0 or > 1 || ultimaOrdem < 0)
            return new(ViagemObservadaStatus.InvalidSequence, ultimaId, ultimaOrdem, []);
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        var circular = string.Equals(topologia, "CIRCULAR", StringComparison.Ordinal);
        command.Parameters.AddWithValue("versao", padraoVersaoId);
        command.Parameters.AddWithValue("anterior", posicaoAnterior);
        command.Parameters.AddWithValue("atual", posicaoAtual);
        command.Parameters.AddWithValue("ultima_id", ultimaId);
        command.Parameters.AddWithValue("ultima_ordem", ultimaOrdem);
        command.Parameters.AddWithValue("baseline", baseline);
        command.Parameters.AddWithValue("circular", circular);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var crossed = new List<OcorrenciaParada>();
        OcorrenciaParada? next = null;
        OcorrenciaParada? terminal = null;
        var id = baseline ? Guid.Empty : ultimaId;
        var order = baseline ? 0 : ultimaOrdem;
        while (await reader.ReadAsync(ct))
        {
            var status = (ViagemObservadaStatus)reader.GetInt32(0);
            if (status != ViagemObservadaStatus.Updated) return new(status, ultimaId, ultimaOrdem, []);
            if (reader.IsDBNull(1)) continue;
            var occurrence = new OcorrenciaParada(reader.GetGuid(2), reader.GetGuid(3),
                reader.GetGuid(4), reader.GetInt32(5), reader.GetDouble(6),
                reader.GetDouble(7), reader.GetDouble(8));
            switch (reader.GetInt32(1))
            {
                case 1: id = occurrence.Id; order = occurrence.Ordem; break;
                case 2: crossed.Add(occurrence); break;
                case 3: next = occurrence; break;
                case 4: terminal = occurrence; break;
            }
        }
        var houveWrap = !baseline && circular && posicaoAtual < posicaoAnterior;
        if (houveWrap)
        {
            crossed = crossed
                .OrderByDescending(x => x.PosicaoLinha > posicaoAnterior)
                .ThenBy(x => x.Ordem)
                .Select(x => x with { Volta = x.PosicaoLinha > posicaoAnterior ? volta : volta + 1 })
                .ToList();
        }
        else
            crossed = crossed.Select(x => x with { Volta = volta }).ToList();
        return new(ViagemObservadaStatus.Updated, id, order, crossed, next, terminal,
            volta + (houveWrap ? 1 : 0), houveWrap);
    }
}
