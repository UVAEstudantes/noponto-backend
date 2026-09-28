using System.Diagnostics;
using System.Text.Json;
using NoPonto.Application.GPS;
using Npgsql;
using NpgsqlTypes;

namespace NoPonto.Tests;

/// <summary>
/// Candidato exclusivamente experimental/test-only. Não é registrado em DI e não
/// substitui nenhuma consulta do runtime.
/// </summary>
internal sealed class GpsMatchingGlobalSetBasedCandidate(NpgsqlDataSource dataSource)
{
    internal const string Sql = """
        WITH entradas AS MATERIALIZED (
            SELECT * FROM jsonb_to_recordset(@inputs::jsonb) AS x(
                input_id text, codigo text, lat double precision, lon double precision,
                bearing double precision, dist_max double precision)
        ),
        codigos_requeridos AS MATERIALIZED (
            SELECT DISTINCT codigo FROM entradas
        ),
        contexto_estrutural AS MATERIALIZED (
            SELECT l."Codigo" AS codigo, pv."Id", po."Id" AS padrao_operacional_id,
                po."SentidoId" AS sentido_id, s."LinhaId" AS linha_id,
                pv."Topologia" AS topologia, pv."Geometria",
                pv."Geometria"::geography AS geometria_geography,
                ST_Length(pv."Geometria"::geography) AS comprimento_metros
            FROM codigos_requeridos cr
            JOIN "Linhas" l ON l."Codigo" = cr.codigo
            JOIN "Sentidos" s ON s."LinhaId" = l."Id"
            JOIN "PadroesOperacionais" po ON po."SentidoId" = s."Id"
            JOIN "PadroesVersoes" pv ON pv."Id" = po."VersaoAtualId"
        ),
        entradas_com_ponto AS MATERIALIZED (
            SELECT e.*,
                ST_SetSRID(ST_MakePoint(e.lon,e.lat),4326) AS ponto_geom,
                ST_SetSRID(ST_MakePoint(e.lon,e.lat),4326)::geography AS ponto_geography
            FROM entradas e
        ),
        candidatos_espaciais AS (
            SELECT e.input_id, e.bearing, e.dist_max, e.ponto_geom, e.ponto_geography,
                c.*,
                ST_Distance(e.ponto_geography,c.geometria_geography) AS distancia_rota_metros,
                ST_LineLocatePoint(c."Geometria",e.ponto_geom) AS posicao_na_rota
            FROM entradas_com_ponto e
            JOIN contexto_estrutural c ON c.codigo=e.codigo
            WHERE ST_Distance(e.ponto_geography,c.geometria_geography) <= e.dist_max
        ),
        candidatos_com_bearing AS (
            SELECT c.*, degrees(ST_Azimuth(
                ST_LineInterpolatePoint(c."Geometria",GREATEST(0.0,c.posicao_na_rota-0.025))::geography,
                ST_LineInterpolatePoint(c."Geometria",LEAST(1.0,c.posicao_na_rota+0.025))::geography
            )) AS bearing_local
            FROM candidatos_espaciais c
        ),
        candidatos_com_diferenca AS (
            SELECT c.*,
                ABS(MOD((c.bearing_local-c.bearing+540.0)::numeric,360.0)-180.0) AS diff_bearing
            FROM candidatos_com_bearing c
        ),
        candidatos_rankeados AS (
            SELECT c.*,
                (c.diff_bearing/80.0)+(c.distancia_rota_metros/c.dist_max) AS score,
                ROW_NUMBER() OVER (PARTITION BY c.input_id ORDER BY
                    (c.diff_bearing/80.0)+(c.distancia_rota_metros/c.dist_max), c."Id") AS ranking
            FROM candidatos_com_diferenca c
            WHERE c.diff_bearing < 80
        ),
        vencedores AS MATERIALIZED (
            SELECT c.*, ST_LineInterpolatePoint(c."Geometria",c.posicao_na_rota) AS ponto_rota
            FROM candidatos_rankeados c WHERE c.ranking=1
        )
        SELECT e.input_id,
            v."Id" AS padrao_versao_id, v.padrao_operacional_id,
            v.sentido_id, v.linha_id, v.topologia, v.posicao_na_rota,
            v.comprimento_metros, v.distancia_rota_metros, v.bearing_local,
            ST_Y(v.ponto_rota) AS lat_rota, ST_X(v.ponto_rota) AS lon_rota,
            pp.parada_nome, pp.ocorrencia_id, pp.parada_id, pp.parada_ordem,
            pp.parada_distancia_acumulada, pp.parada_distancia_linha,
            pp.distancia_parada_metros, pp.distancia_restante_rota_metros
        FROM entradas_com_ponto e
        LEFT JOIN vencedores v ON v.input_id=e.input_id
        LEFT JOIN LATERAL (
            SELECT p."Nome" AS parada_nome, op."Id" AS ocorrencia_id,
                op."ParadaId" AS parada_id, op."Ordem" AS parada_ordem,
                op."DistanciaAcumuladaMetros" AS parada_distancia_acumulada,
                op."DistanciaDaLinhaMetros" AS parada_distancia_linha,
                ST_Distance(e.ponto_geography,p."Localizacao"::geography) AS distancia_parada_metros,
                GREATEST(0.0,CASE
                    WHEN op."PosicaoTracado">v.posicao_na_rota THEN
                        ST_Length(ST_LineSubstring(v."Geometria",0,op."PosicaoTracado")::geography)
                        - ST_Length(ST_LineSubstring(v."Geometria",0,v.posicao_na_rota)::geography)
                    ELSE v.comprimento_metros
                        - ST_Length(ST_LineSubstring(v."Geometria",0,v.posicao_na_rota)::geography)
                        + ST_Length(ST_LineSubstring(v."Geometria",0,op."PosicaoTracado")::geography)
                END) AS distancia_restante_rota_metros
            FROM "OcorrenciasParadasPadroes" op
            JOIN "Paradas" p ON p."Id"=op."ParadaId"
            WHERE op."PadraoVersaoId"=v."Id"
              AND (op."PosicaoTracado">v.posicao_na_rota OR v.topologia='CIRCULAR')
            ORDER BY CASE WHEN op."PosicaoTracado">v.posicao_na_rota THEN 0 ELSE 1 END,
                op."PosicaoTracado",op."Ordem"
            LIMIT 1
        ) pp ON v."Id" IS NOT NULL
        ORDER BY e.input_id DESC
        """;

    internal async Task<ResultadoMatchingLote<ResultadoMatchingGlobalLote>> BuscarAsync(
        IReadOnlyList<EntradaMatchingGlobalLote> entradas, int tamanhoChunk=100,
        CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(entradas);
        if (tamanhoChunk<=0) throw new ArgumentOutOfRangeException(nameof(tamanhoChunk));
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entradas)
        {
            if (string.IsNullOrWhiteSpace(e.InputId)) throw new ArgumentException("InputId e obrigatorio.",nameof(entradas));
            if (!ids.Add(e.InputId)) throw new ArgumentException($"InputId duplicado no lote: {e.InputId}",nameof(entradas));
        }

        var resultados=entradas.ToDictionary(x=>x.InputId,_=>ResultadoBuscaPadrao.NotEligible(),StringComparer.Ordinal);
        var comandos=new List<MetricaComandoMatchingLote>();
        var validas=entradas.Where(Valida).ToArray();
        foreach (var chunk in validas.Chunk(tamanhoChunk))
        {
            var started=Stopwatch.GetTimestamp();
            var json=Serialize(chunk);
            await using var conn=await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd=conn.CreateCommand();
            cmd.CommandText=Sql;
            cmd.Parameters.AddWithValue("inputs",NpgsqlDbType.Jsonb,json);
            await using var reader=await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id=reader.GetString(reader.GetOrdinal("input_id"));
                resultados[id]=reader.IsDBNull(reader.GetOrdinal("padrao_versao_id"))
                    ? ResultadoBuscaPadrao.NotEligible()
                    : ResultadoBuscaPadrao.Found(Read(reader));
            }
            comandos.Add(new(TipoBatchMatching.GlobalSimples,OrigemComandoMatchingLote.Batch,
                chunk.Length,Stopwatch.GetElapsedTime(started)));
        }
        return new(entradas.Select(x=>new ResultadoMatchingGlobalLote(x.InputId,resultados[x.InputId])).ToArray(),
            new(entradas.Count,comandos));
    }

    internal static string Serialize(IEnumerable<EntradaMatchingGlobalLote> inputs) =>
        JsonSerializer.Serialize(inputs.Select(x=>new { input_id=x.InputId,codigo=x.CodigoLinha,
            lat=x.Latitude,lon=x.Longitude,bearing=x.Bearing!.Value,dist_max=x.DistanciaMaximaMetros }));

    private static bool Valida(EntradaMatchingGlobalLote x) => x.Bearing.HasValue
        && double.IsFinite(x.Latitude) && x.Latitude is >= -90 and <= 90
        && double.IsFinite(x.Longitude) && x.Longitude is >= -180 and <= 180
        && double.IsFinite(x.Bearing.Value) && double.IsFinite(x.DistanciaMaximaMetros);

    private static EnriquecimentoRotaDto Read(NpgsqlDataReader r) => new()
    {
        PadraoVersaoId=r.GetGuid(r.GetOrdinal("padrao_versao_id")),
        PadraoOperacionalId=r.GetGuid(r.GetOrdinal("padrao_operacional_id")),
        SentidoId=r.GetGuid(r.GetOrdinal("sentido_id")), LinhaId=r.GetGuid(r.GetOrdinal("linha_id")),
        Topologia=r.GetString(r.GetOrdinal("topologia")), PosicaoNaRota=r.GetDouble(r.GetOrdinal("posicao_na_rota")),
        ComprimentoRotaMetros=r.GetDouble(r.GetOrdinal("comprimento_metros")),
        DistanciaARotaMetros=r.GetDouble(r.GetOrdinal("distancia_rota_metros")),
        BearingLocal=NullableDouble(r,"bearing_local"), LatitudeProjetada=NullableDouble(r,"lat_rota"),
        LongitudeProjetada=NullableDouble(r,"lon_rota"), ProximaParadaNome=NullableString(r,"parada_nome"),
        ProximaOcorrenciaParadaPadraoId=NullableGuid(r,"ocorrencia_id"), ProximaParadaId=NullableGuid(r,"parada_id"),
        ProximaParadaOrdem=NullableInt(r,"parada_ordem"),
        ProximaParadaDistanciaAcumuladaMetros=NullableDouble(r,"parada_distancia_acumulada"),
        ProximaParadaDistanciaDaLinhaMetros=NullableDouble(r,"parada_distancia_linha"),
        DistanciaProximaParadaMetros=NullableDouble(r,"distancia_parada_metros"),
        DistanciaRestanteRotaMetros=NullableDouble(r,"distancia_restante_rota_metros"),
    };
    private static double? NullableDouble(NpgsqlDataReader r,string n) { var i=r.GetOrdinal(n); return r.IsDBNull(i)?null:r.GetDouble(i); }
    private static string? NullableString(NpgsqlDataReader r,string n) { var i=r.GetOrdinal(n); return r.IsDBNull(i)?null:r.GetString(i); }
    private static Guid? NullableGuid(NpgsqlDataReader r,string n) { var i=r.GetOrdinal(n); return r.IsDBNull(i)?null:r.GetGuid(i); }
    private static int? NullableInt(NpgsqlDataReader r,string n) { var i=r.GetOrdinal(n); return r.IsDBNull(i)?null:r.GetInt32(i); }
}
