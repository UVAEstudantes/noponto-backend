using Microsoft.Extensions.Logging;
using Npgsql;
using NoPonto.Application.GPS;

namespace NoPonto.Data.Repositories;

public sealed partial class GpsPadraoRepository : IGpsPadraoRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<GpsPadraoRepository> _logger;

    public GpsPadraoRepository(
        NpgsqlDataSource dataSource,
        ILogger<GpsPadraoRepository> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public Task<EnriquecimentoRotaDto?> BuscarEnriquecimentoAsync(
        string codigoLinha, double latitude, double longitude, double bearing,
        double distanciaMaximaMetros, CancellationToken cancellationToken = default)
        => BuscarMatchingAsync(codigoLinha, latitude, longitude, bearing,
            distanciaMaximaMetros, null, null, cancellationToken);

    public async Task<ResultadoBuscaPadrao> BuscarEnriquecimentoDoPadraoAsync(
        string codigoLinha, Guid padraoVersaoId, double latitude, double longitude, double bearing,
        double distanciaMaximaMetros, CancellationToken cancellationToken = default,
        FaixaProjecao? faixa = null)
    {
        // Faixa degenerada produziria POINT em ST_LineSubstring, não LineString.
        if (faixa is { } limites && !limites.Valida)
            return ResultadoBuscaPadrao.InfrastructureFailure();
        try
        {
            var rota = await BuscarMatchingAsync(codigoLinha, latitude, longitude, bearing,
                distanciaMaximaMetros, padraoVersaoId, faixa, cancellationToken);
            return rota is null ? ResultadoBuscaPadrao.NotEligible() : ResultadoBuscaPadrao.Found(rota);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return ResultadoBuscaPadrao.InfrastructureFailure(); }
    }

    public async Task<ResultadoMatchingCombinado> BuscarMatchingCombinadoAsync(
        string codigoLinha,
        Guid? padraoVersaoAnteriorId,
        double latitude,
        double longitude,
        double bearing,
        double distanciaMaximaMetros,
        FaixaProjecao? faixa,
        SolicitacaoProjecaoOperacional? projecaoOperacional = null,
        CancellationToken cancellationToken = default)
    {
        if ((faixa is { } faixaInformada && !faixaInformada.Valida)
            || (projecaoOperacional is { } operacionalInformada && !operacionalInformada.Valida))
            return FalhaCombinada(projecaoOperacional is not null);

        const string sql = """
            WITH veiculo AS (
                SELECT
                    ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography AS ponto,
                    ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)            AS ponto_geom
            ),
            rotas_global AS (
                SELECT i."Id", po."Id" AS padrao_operacional_id,
                    po."SentidoId" AS sentido_id, s."LinhaId" AS linha_id,
                    i."Topologia" AS topologia, i."Geometria"
                FROM "PadroesVersoes" i
                JOIN "PadroesOperacionais" po ON po."VersaoAtualId" = i."Id"
                JOIN "Sentidos" s ON s."Id" = po."SentidoId"
                JOIN "Linhas"   l ON l."Id" = s."LinhaId"
                CROSS JOIN veiculo v
                WHERE l."Codigo" = @codigo
            ),
            candidatos_global AS (
                SELECT r."Id", r.padrao_operacional_id, r.sentido_id, r.linha_id, r.topologia, r."Geometria",
                    ST_Length(r."Geometria"::geography) AS comprimento_metros,
                    ST_Distance(v.ponto, r."Geometria"::geography) AS distancia_rota_metros,
                    ST_LineLocatePoint(r."Geometria", v.ponto_geom) AS posicao_na_rota
                FROM rotas_global r
                CROSS JOIN veiculo v
                WHERE ST_Distance(v.ponto, r."Geometria"::geography) <= @dist_max
            ),
            bearing_global AS (
                SELECT c.*,
                    degrees(ST_Azimuth(
                        ST_LineInterpolatePoint(
                            c."Geometria", GREATEST(0.0, c.posicao_na_rota - 0.025)
                        )::geography,
                        ST_LineInterpolatePoint(
                            c."Geometria", LEAST(1.0, c.posicao_na_rota + 0.025)
                        )::geography
                    )) AS bearing_local
                FROM candidatos_global c
            ),
            diff_global AS (
                SELECT bg.*,
                    ABS(MOD((bg.bearing_local - @bearing + 540.0)::numeric, 360.0) - 180.0) AS diff_bearing
                FROM bearing_global bg
            ),
            score_global AS (
                SELECT dg.*,
                    (dg.diff_bearing / 80.0) + (dg.distancia_rota_metros / @dist_max) AS score
                FROM diff_global dg
                WHERE dg.diff_bearing < 80
            ),
            global_escolhido AS (
                SELECT sg.*,
                    ST_LineInterpolatePoint(sg."Geometria", sg.posicao_na_rota) AS ponto_rota
                FROM score_global sg
                ORDER BY sg.score ASC, sg."Id" ASC
                LIMIT 1
            ),
            proxima_parada_global AS (
                SELECT p."Nome" AS parada_nome, pi."Id" AS ocorrencia_id,
                    pi."ParadaId" AS parada_id, pi."Ordem" AS parada_ordem,
                    pi."DistanciaAcumuladaMetros" AS parada_distancia_acumulada,
                    pi."DistanciaDaLinhaMetros" AS parada_distancia_linha,
                    ST_Distance(v.ponto, p."Localizacao"::geography) AS distancia_parada_metros
                FROM "OcorrenciasParadasPadroes" pi
                JOIN "Paradas" p ON p."Id" = pi."ParadaId"
                JOIN global_escolhido ge ON ge."Id" = pi."PadraoVersaoId"
                CROSS JOIN veiculo v
                WHERE pi."PosicaoTracado" > ge.posicao_na_rota
                ORDER BY pi."PosicaoTracado" ASC, pi."Ordem" ASC
                LIMIT 1
            ),
            rota_anterior AS (
                SELECT i."Id", po."Id" AS padrao_operacional_id,
                    po."SentidoId" AS sentido_id, s."LinhaId" AS linha_id,
                    i."Topologia" AS topologia, i."Geometria"
                FROM "PadroesVersoes" i
                JOIN "PadroesOperacionais" po ON po."VersaoAtualId" = i."Id"
                JOIN "Sentidos" s ON s."Id" = po."SentidoId"
                JOIN "Linhas"   l ON l."Id" = s."LinhaId"
                CROSS JOIN veiculo v
                WHERE @usar_anterior AND l."Codigo" = @codigo
                  AND i."Id" = @padrao_versao_id
            ),
            geometria_anterior AS (
                SELECT r.*,
                    ST_LineSubstring(r."Geometria", @fracao_min, @fracao_max) AS geometria_projecao
                FROM rota_anterior r
            ),
            candidatos_anterior AS (
                SELECT r."Id", r.padrao_operacional_id, r.sentido_id, r.linha_id, r.topologia, r."Geometria",
                    ST_Length(r."Geometria"::geography) AS comprimento_metros,
                    ST_Distance(v.ponto, r.geometria_projecao::geography) AS distancia_rota_metros,
                    @fracao_min + ST_LineLocatePoint(r.geometria_projecao, v.ponto_geom)
                        * (@fracao_max - @fracao_min) AS posicao_na_rota
                FROM geometria_anterior r
                CROSS JOIN veiculo v
                WHERE ST_Distance(v.ponto, r.geometria_projecao::geography) <= @dist_max
            ),
            bearing_anterior AS (
                SELECT c.*,
                    degrees(ST_Azimuth(
                        ST_LineInterpolatePoint(
                            c."Geometria", GREATEST(0.0, c.posicao_na_rota - 0.025)
                        )::geography,
                        ST_LineInterpolatePoint(
                            c."Geometria", LEAST(1.0, c.posicao_na_rota + 0.025)
                        )::geography
                    )) AS bearing_local
                FROM candidatos_anterior c
            ),
            diff_anterior AS (
                SELECT ba.*,
                    ABS(MOD((ba.bearing_local - @bearing + 540.0)::numeric, 360.0) - 180.0) AS diff_bearing
                FROM bearing_anterior ba
            ),
            score_anterior AS (
                SELECT da.*,
                    (da.diff_bearing / 80.0) + (da.distancia_rota_metros / @dist_max) AS score
                FROM diff_anterior da
                WHERE da.diff_bearing < 80
            ),
            anterior_escolhido AS (
                SELECT sa.*,
                    ST_LineInterpolatePoint(sa."Geometria", sa.posicao_na_rota) AS ponto_rota
                FROM score_anterior sa
                ORDER BY sa.score ASC
                LIMIT 1
            ),
            proxima_parada_anterior AS (
                SELECT p."Nome" AS parada_nome, pi."Id" AS ocorrencia_id,
                    pi."ParadaId" AS parada_id, pi."Ordem" AS parada_ordem,
                    pi."DistanciaAcumuladaMetros" AS parada_distancia_acumulada,
                    pi."DistanciaDaLinhaMetros" AS parada_distancia_linha,
                    ST_Distance(v.ponto, p."Localizacao"::geography) AS distancia_parada_metros
                FROM "OcorrenciasParadasPadroes" pi
                JOIN "Paradas" p ON p."Id" = pi."ParadaId"
                JOIN anterior_escolhido ae ON ae."Id" = pi."PadraoVersaoId"
                CROSS JOIN veiculo v
                WHERE pi."PosicaoTracado" > ae.posicao_na_rota
                ORDER BY pi."PosicaoTracado" ASC, pi."Ordem" ASC
                LIMIT 1
            ),
            rota_operacional AS (
                SELECT i."Id", i."Geometria",
                    ST_Length(i."Geometria"::geography) AS comprimento_metros
                FROM "PadroesVersoes" i
                JOIN "PadroesOperacionais" po ON po."VersaoAtualId" = i."Id"
                WHERE @usar_operacional
                  AND i."Id" = @padrao_operacional
                  AND EXISTS (SELECT 1 FROM global_escolhido ge
                      WHERE ge."Id" <> @padrao_operacional)
            ),
            geometria_operacional AS (
                SELECT ro.*,
                    GREATEST(0.0, @posicao_operacional_anterior
                        - (@orcamento_operacional_metros / ro.comprimento_metros)) AS fracao_min,
                    LEAST(1.0, @posicao_operacional_anterior
                        + (@orcamento_operacional_metros / ro.comprimento_metros)) AS fracao_max
                FROM rota_operacional ro
                WHERE ro.comprimento_metros > 0
            ),
            projecao_operacional AS (
                SELECT go.*,
                    ST_Distance(v.ponto,
                        ST_LineSubstring(go."Geometria", go.fracao_min, go.fracao_max)::geography)
                        AS distancia_rota_metros,
                    go.fracao_min + ST_LineLocatePoint(
                        ST_LineSubstring(go."Geometria", go.fracao_min, go.fracao_max), v.ponto_geom)
                        * (go.fracao_max - go.fracao_min) AS posicao_na_rota
                FROM geometria_operacional go
                CROSS JOIN veiculo v
                WHERE go.fracao_min < go.fracao_max
                  AND ST_DWithin(
                      ST_LineSubstring(go."Geometria", go.fracao_min, go.fracao_max)::geography,
                      v.ponto, @dist_max)
            ),
            operacional_elegivel AS (
                SELECT * FROM projecao_operacional
                WHERE posicao_na_rota >= @posicao_operacional_anterior
            )
            SELECT
                'GLOBAL'::text AS ramo,
                ge."Id" AS padrao_versao_id,
                ge.padrao_operacional_id, ge.sentido_id, ge.linha_id, ge.topologia,
                ge.posicao_na_rota,
                ge.comprimento_metros,
                ge.distancia_rota_metros,
                ge.bearing_local,
                ST_Y(ge.ponto_rota) AS lat_rota,
                ST_X(ge.ponto_rota) AS lon_rota,
                ppg.parada_nome, ppg.ocorrencia_id, ppg.parada_id, ppg.parada_ordem,
                ppg.parada_distancia_acumulada, ppg.parada_distancia_linha,
                ppg.distancia_parada_metros
            FROM global_escolhido ge
            LEFT JOIN proxima_parada_global ppg ON true
            UNION ALL
            SELECT
                'ANTERIOR'::text AS ramo,
                ae."Id" AS padrao_versao_id,
                ae.padrao_operacional_id, ae.sentido_id, ae.linha_id, ae.topologia,
                ae.posicao_na_rota,
                ae.comprimento_metros,
                ae.distancia_rota_metros,
                ae.bearing_local,
                ST_Y(ae.ponto_rota) AS lat_rota,
                ST_X(ae.ponto_rota) AS lon_rota,
                ppa.parada_nome, ppa.ocorrencia_id, ppa.parada_id, ppa.parada_ordem,
                ppa.parada_distancia_acumulada, ppa.parada_distancia_linha,
                ppa.distancia_parada_metros
            FROM anterior_escolhido ae
            LEFT JOIN proxima_parada_anterior ppa ON true
            UNION ALL
            SELECT
                'OPERACIONAL'::text AS ramo,
                oe."Id" AS padrao_versao_id,
                NULL::uuid AS padrao_operacional_id, NULL::uuid AS sentido_id,
                NULL::uuid AS linha_id, NULL::text AS topologia,
                oe.posicao_na_rota,
                oe.comprimento_metros,
                oe.distancia_rota_metros,
                NULL::double precision AS bearing_local,
                NULL::double precision AS lat_rota,
                NULL::double precision AS lon_rota,
                NULL::text AS parada_nome, NULL::uuid AS ocorrencia_id,
                NULL::uuid AS parada_id, NULL::integer AS parada_ordem,
                NULL::double precision AS parada_distancia_acumulada,
                NULL::double precision AS parada_distancia_linha,
                NULL::double precision AS distancia_parada_metros
            FROM operacional_elegivel oe
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("usar_anterior", padraoVersaoAnteriorId.HasValue && faixa.HasValue);
            cmd.Parameters.AddWithValue("padrao_versao_id", padraoVersaoAnteriorId ?? Guid.Empty);
            cmd.Parameters.AddWithValue("lat", latitude);
            cmd.Parameters.AddWithValue("lon", longitude);
            cmd.Parameters.AddWithValue("codigo", codigoLinha);
            cmd.Parameters.AddWithValue("bearing", bearing);
            cmd.Parameters.AddWithValue("dist_max", distanciaMaximaMetros);
            cmd.Parameters.AddWithValue("fracao_min", faixa?.Min ?? 0.0);
            cmd.Parameters.AddWithValue("fracao_max", faixa?.Max ?? 1.0);
            cmd.Parameters.AddWithValue("usar_operacional", projecaoOperacional.HasValue);
            cmd.Parameters.AddWithValue("padrao_operacional",
                projecaoOperacional?.PadraoVersaoId ?? Guid.Empty);
            cmd.Parameters.AddWithValue("posicao_operacional_anterior",
                projecaoOperacional?.PosicaoAnterior ?? 0.0);
            cmd.Parameters.AddWithValue("orcamento_operacional_metros",
                projecaoOperacional?.OrcamentoMetros ?? 1.0);

            ResultadoBuscaPadrao global = ResultadoBuscaPadrao.NotEligible();
            ResultadoBuscaPadrao anterior = ResultadoBuscaPadrao.NotEligible();
            var operacional = projecaoOperacional.HasValue
                ? ResultadoProjecaoOperacional.Inelegivel()
                : ResultadoProjecaoOperacional.NaoSolicitada();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                switch (reader.GetString(reader.GetOrdinal("ramo")))
                {
                    case "GLOBAL": global = ResultadoBuscaPadrao.Found(LerRota(reader)); break;
                    case "ANTERIOR": anterior = ResultadoBuscaPadrao.Found(LerRota(reader)); break;
                    case "OPERACIONAL":
                        operacional = ResultadoProjecaoOperacional.Encontrada(new(
                            reader.GetGuid(reader.GetOrdinal("padrao_versao_id")),
                            reader.GetDouble(reader.GetOrdinal("posicao_na_rota")),
                            reader.GetDouble(reader.GetOrdinal("distancia_rota_metros")),
                            reader.GetDouble(reader.GetOrdinal("comprimento_metros"))));
                        break;
                    default: throw new InvalidOperationException("Ramo inesperado no matching combinado.");
                }
            }

            return new ResultadoMatchingCombinado(global, anterior, operacional);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha no matching combinado experimental para linha {linha} em ({lat},{lon})",
                codigoLinha, latitude, longitude);
            return FalhaCombinada(projecaoOperacional is not null);
        }
    }

    private static ResultadoMatchingCombinado FalhaCombinada(bool operacionalSolicitada = false) => new(
        ResultadoBuscaPadrao.InfrastructureFailure(),
        ResultadoBuscaPadrao.InfrastructureFailure(),
        operacionalSolicitada ? ResultadoProjecaoOperacional.Falha() : null);

    private static EnriquecimentoRotaDto LerRota(NpgsqlDataReader reader) => new()
    {
        PadraoVersaoId = reader.GetGuid(reader.GetOrdinal("padrao_versao_id")),
        PadraoOperacionalId = reader.GetGuid(reader.GetOrdinal("padrao_operacional_id")),
        SentidoId = reader.GetGuid(reader.GetOrdinal("sentido_id")),
        LinhaId = reader.GetGuid(reader.GetOrdinal("linha_id")),
        Topologia = reader.GetString(reader.GetOrdinal("topologia")),
        PosicaoNaRota = reader.GetDouble(reader.GetOrdinal("posicao_na_rota")),
        ComprimentoRotaMetros = reader.GetDouble(reader.GetOrdinal("comprimento_metros")),
        DistanciaARotaMetros = reader.GetDouble(reader.GetOrdinal("distancia_rota_metros")),
        LatitudeProjetada = reader.IsDBNull(reader.GetOrdinal("lat_rota"))
            ? null : reader.GetDouble(reader.GetOrdinal("lat_rota")),
        LongitudeProjetada = reader.IsDBNull(reader.GetOrdinal("lon_rota"))
            ? null : reader.GetDouble(reader.GetOrdinal("lon_rota")),
        BearingLocal = reader.IsDBNull(reader.GetOrdinal("bearing_local"))
            ? null : reader.GetDouble(reader.GetOrdinal("bearing_local")),
        ProximaParadaNome = reader.IsDBNull(reader.GetOrdinal("parada_nome"))
            ? null : reader.GetString(reader.GetOrdinal("parada_nome")),
        ProximaOcorrenciaParadaPadraoId = reader.IsDBNull(reader.GetOrdinal("ocorrencia_id"))
            ? null : reader.GetGuid(reader.GetOrdinal("ocorrencia_id")),
        ProximaParadaId = reader.IsDBNull(reader.GetOrdinal("parada_id"))
            ? null : reader.GetGuid(reader.GetOrdinal("parada_id")),
        ProximaParadaOrdem = reader.IsDBNull(reader.GetOrdinal("parada_ordem"))
            ? null : reader.GetInt32(reader.GetOrdinal("parada_ordem")),
        ProximaParadaDistanciaAcumuladaMetros = reader.IsDBNull(reader.GetOrdinal("parada_distancia_acumulada"))
            ? null : reader.GetDouble(reader.GetOrdinal("parada_distancia_acumulada")),
        ProximaParadaDistanciaDaLinhaMetros = reader.IsDBNull(reader.GetOrdinal("parada_distancia_linha"))
            ? null : reader.GetDouble(reader.GetOrdinal("parada_distancia_linha")),
        DistanciaProximaParadaMetros = reader.IsDBNull(reader.GetOrdinal("distancia_parada_metros"))
            ? null : reader.GetDouble(reader.GetOrdinal("distancia_parada_metros")),
    };

    // Núcleo único: a busca global mantém seus filtros/score/ORDER BY/LIMIT.
    private async Task<EnriquecimentoRotaDto?> BuscarMatchingAsync(
        string codigoLinha,
        double latitude,
        double longitude,
        double bearing,
        double distanciaMaximaMetros,
        Guid? padraoVersaoId,
        FaixaProjecao? faixa,
        CancellationToken cancellationToken = default,
        bool propagarFalhaGlobalParaDiagnostico = false)
    {
        const string sql = """
            WITH veiculo AS (
                SELECT
                    ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography AS ponto,
                    ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)            AS ponto_geom
            ),
            rotas AS (
                SELECT
                    i."Id", po."Id" AS padrao_operacional_id,
                    po."SentidoId" AS sentido_id, s."LinhaId" AS linha_id,
                    i."Topologia" AS topologia, i."Geometria"
                FROM "PadroesVersoes" i
                JOIN "PadroesOperacionais" po ON po."VersaoAtualId" = i."Id"
                JOIN "Sentidos" s ON s."Id" = po."SentidoId"
                JOIN "Linhas"   l ON l."Id" = s."LinhaId"
                CROSS JOIN veiculo v
                WHERE l."Codigo" = @codigo
                  /*FILTRO_PADRAO*/
            ),
            geometrias_projecao AS (
                SELECT r.*,
                    CASE WHEN @usar_faixa
                        THEN ST_LineSubstring(r."Geometria", @fracao_min, @fracao_max)
                        ELSE r."Geometria"
                    END AS geometria_projecao
                FROM rotas r
            ),
            candidatos AS (
                SELECT r."Id", r.padrao_operacional_id, r.sentido_id, r.linha_id, r.topologia, r."Geometria",
                    ST_Length(r."Geometria"::geography) AS comprimento_metros,
                    ST_Distance(v.ponto, r.geometria_projecao::geography) AS distancia_rota_metros,
                    CASE WHEN @usar_faixa THEN
                        @fracao_min + ST_LineLocatePoint(r.geometria_projecao, v.ponto_geom)
                            * (@fracao_max - @fracao_min)
                        ELSE ST_LineLocatePoint(r.geometria_projecao, v.ponto_geom)
                    END AS posicao_na_rota
                FROM geometrias_projecao r
                CROSS JOIN veiculo v
                WHERE ST_Distance(v.ponto, r.geometria_projecao::geography) <= @dist_max
            ),
            com_bearing_local AS (
                SELECT
                    c.*,
                    degrees(ST_Azimuth(
                        ST_LineInterpolatePoint(
                            c."Geometria",
                            GREATEST(0.0, c.posicao_na_rota - 0.025)
                        )::geography,
                        ST_LineInterpolatePoint(
                            c."Geometria",
                            LEAST(1.0, c.posicao_na_rota + 0.025)
                        )::geography
                    )) AS bearing_local
                FROM candidatos c
            ),
            com_diff_bearing AS (
                SELECT
                    cb.*,
                    ABS(MOD((cb.bearing_local - @bearing + 540.0)::numeric, 360.0) - 180.0) AS diff_bearing
                FROM com_bearing_local cb
            ),
            com_score AS (
                -- Score combinado normalizado: pesa bearing e distância igualmente
                -- em relação aos próprios limites (80° e @dist_max), em vez de
                -- decidir por bearing puro primeiro. Reduz o risco de uma rua
                -- paralela mais distante vencer só por ter bearing marginalmente
                -- melhor que um candidato bem mais próximo.
                SELECT
                    cd.*,
                    (cd.diff_bearing / 80.0) + (cd.distancia_rota_metros / @dist_max) AS score
                FROM com_diff_bearing cd
                WHERE cd.diff_bearing < 80
            ),
            padrao_escolhido AS (
                SELECT
                    cs.*,
                    ST_LineInterpolatePoint(cs."Geometria", cs.posicao_na_rota) AS ponto_rota
                FROM com_score cs
                ORDER BY cs.score ASC /*DESEMPATE_GLOBAL*/
                LIMIT 1
            ),
            proxima_parada AS (
                SELECT
                    p."Nome"                                                         AS parada_nome,
                    pi."Id"                                                          AS ocorrencia_id,
                    pi."ParadaId"                                                    AS parada_id,
                    pi."Ordem"                                                       AS parada_ordem,
                    pi."DistanciaAcumuladaMetros"                                   AS parada_distancia_acumulada,
                    pi."DistanciaDaLinhaMetros"                                     AS parada_distancia_linha,
                    ST_Distance(v.ponto, p."Localizacao"::geography)                AS distancia_parada_metros
                FROM "OcorrenciasParadasPadroes" pi
                JOIN "Paradas"           p  ON p."Id"  = pi."ParadaId"
                JOIN padrao_escolhido ie ON ie."Id" = pi."PadraoVersaoId"
                CROSS JOIN veiculo v
                WHERE pi."PosicaoTracado" > ie.posicao_na_rota
                ORDER BY pi."PosicaoTracado" ASC, pi."Ordem" ASC
                LIMIT 1
            )
            SELECT
                ie."Id"                   AS padrao_versao_id,
                ie.padrao_operacional_id,
                ie.sentido_id,
                ie.linha_id,
                ie.topologia,
                ie.posicao_na_rota,
                ie.comprimento_metros,
                ie.distancia_rota_metros,
                ie.bearing_local,
                ST_Y(ie.ponto_rota)        AS lat_rota,
                ST_X(ie.ponto_rota)        AS lon_rota,
                pp.parada_nome, pp.ocorrencia_id, pp.parada_id, pp.parada_ordem,
                pp.parada_distancia_acumulada, pp.parada_distancia_linha,
                pp.distancia_parada_metros
            FROM padrao_escolhido ie
            LEFT JOIN proxima_parada pp ON true
            LIMIT 1
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = sql.Replace("/*FILTRO_PADRAO*/",
                padraoVersaoId.HasValue ? "AND i.\"Id\" = @padrao_versao_id" : "")
                .Replace("/*DESEMPATE_GLOBAL*/",
                    padraoVersaoId.HasValue ? "" : ", cs.\"Id\" ASC");
            if (padraoVersaoId.HasValue) cmd.Parameters.AddWithValue("padrao_versao_id", padraoVersaoId.Value);
            cmd.Parameters.AddWithValue("lat", latitude);
            cmd.Parameters.AddWithValue("lon", longitude);
            cmd.Parameters.AddWithValue("codigo", codigoLinha);
            cmd.Parameters.AddWithValue("bearing", bearing);
            cmd.Parameters.AddWithValue("dist_max", distanciaMaximaMetros);
            cmd.Parameters.AddWithValue("usar_faixa", faixa.HasValue);
            cmd.Parameters.AddWithValue("fracao_min", faixa?.Min ?? 0.0);
            cmd.Parameters.AddWithValue("fracao_max", faixa?.Max ?? 1.0);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
                return null;

            if (reader.IsDBNull(reader.GetOrdinal("padrao_versao_id")))
                return null;

            return new EnriquecimentoRotaDto
            {
                PadraoOperacionalId = reader.GetGuid(reader.GetOrdinal("padrao_operacional_id")),
                PadraoVersaoId = reader.GetGuid(reader.GetOrdinal("padrao_versao_id")),
                SentidoId = reader.GetGuid(reader.GetOrdinal("sentido_id")),
                LinhaId = reader.GetGuid(reader.GetOrdinal("linha_id")),
                Topologia = reader.GetString(reader.GetOrdinal("topologia")),
                PosicaoNaRota = reader.GetDouble(reader.GetOrdinal("posicao_na_rota")),
                ComprimentoRotaMetros = reader.GetDouble(reader.GetOrdinal("comprimento_metros")),
                DistanciaARotaMetros = reader.GetDouble(reader.GetOrdinal("distancia_rota_metros")),
                LatitudeProjetada = reader.IsDBNull(reader.GetOrdinal("lat_rota"))
                                                    ? null
                                                    : reader.GetDouble(reader.GetOrdinal("lat_rota")),
                LongitudeProjetada = reader.IsDBNull(reader.GetOrdinal("lon_rota"))
                                                    ? null
                                                    : reader.GetDouble(reader.GetOrdinal("lon_rota")),
                BearingLocal = reader.IsDBNull(reader.GetOrdinal("bearing_local"))
                                                    ? null
                                                    : reader.GetDouble(reader.GetOrdinal("bearing_local")),
                ProximaParadaNome = reader.IsDBNull(reader.GetOrdinal("parada_nome"))
                                                    ? null
                                                    : reader.GetString(reader.GetOrdinal("parada_nome")),
                ProximaOcorrenciaParadaPadraoId = reader.IsDBNull(reader.GetOrdinal("ocorrencia_id"))
                                                    ? null : reader.GetGuid(reader.GetOrdinal("ocorrencia_id")),
                ProximaParadaId = reader.IsDBNull(reader.GetOrdinal("parada_id"))
                                                    ? null : reader.GetGuid(reader.GetOrdinal("parada_id")),
                ProximaParadaOrdem = reader.IsDBNull(reader.GetOrdinal("parada_ordem"))
                                                    ? null : reader.GetInt32(reader.GetOrdinal("parada_ordem")),
                ProximaParadaDistanciaAcumuladaMetros = reader.IsDBNull(reader.GetOrdinal("parada_distancia_acumulada"))
                                                    ? null : reader.GetDouble(reader.GetOrdinal("parada_distancia_acumulada")),
                ProximaParadaDistanciaDaLinhaMetros = reader.IsDBNull(reader.GetOrdinal("parada_distancia_linha"))
                                                    ? null : reader.GetDouble(reader.GetOrdinal("parada_distancia_linha")),
                DistanciaProximaParadaMetros = reader.IsDBNull(reader.GetOrdinal("distancia_parada_metros"))
                                                    ? null
                                                    : reader.GetDouble(reader.GetOrdinal("distancia_parada_metros")),
            };
        }
        catch (Exception ex)
        {
            if (!propagarFalhaGlobalParaDiagnostico)
                _logger.LogWarning(ex,
                    "Falha ao enriquecer rota para linha {linha} em ({lat},{lon})",
                    codigoLinha, latitude, longitude);
            // A operação direcionada nunca confunde erro com ausência de matching.
            if (padraoVersaoId.HasValue || propagarFalhaGlobalParaDiagnostico) throw;
            return null;
        }
    }

    public async Task<string?> BuscarGeometriaGeoJsonAsync(
        Guid padraoVersaoId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ST_AsGeoJSON("Geometria") AS geojson
            FROM "PadroesVersoes" v
            WHERE v."Id" = @id
              AND EXISTS (SELECT 1 FROM "PadroesOperacionais" p
                  WHERE p."VersaoAtualId" = v."Id")
            LIMIT 1
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("id", padraoVersaoId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return reader.IsDBNull(0) ? null : reader.GetString(0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao buscar geometria GeoJSON do padrao {id}", padraoVersaoId);
            return null;
        }
    }
}
