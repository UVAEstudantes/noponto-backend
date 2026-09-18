using Microsoft.Extensions.Logging;
using Npgsql;
using NoPonto.Application.GPS;

namespace NoPonto.Data.Repositories;

public sealed partial class GpsItinerarioRepository : IGpsItinerarioRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<GpsItinerarioRepository> _logger;

    public GpsItinerarioRepository(
        NpgsqlDataSource dataSource,
        ILogger<GpsItinerarioRepository> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public Task<EnriquecimentoRotaDto?> BuscarEnriquecimentoAsync(
        string codigoLinha, double latitude, double longitude, double bearing,
        double distanciaMaximaMetros, CancellationToken cancellationToken = default)
        => BuscarMatchingAsync(codigoLinha, latitude, longitude, bearing,
            distanciaMaximaMetros, null, null, cancellationToken);

    public async Task<ResultadoBuscaItinerario> BuscarEnriquecimentoDoItinerarioAsync(
        string codigoLinha, Guid itinerarioId, double latitude, double longitude, double bearing,
        double distanciaMaximaMetros, CancellationToken cancellationToken = default,
        FaixaProjecao? faixa = null)
    {
        // Faixa degenerada produziria POINT em ST_LineSubstring, não LineString.
        if (faixa is { } limites && !limites.Valida)
            return ResultadoBuscaItinerario.InfrastructureFailure();
        try
        {
            var rota = await BuscarMatchingAsync(codigoLinha, latitude, longitude, bearing,
                distanciaMaximaMetros, itinerarioId, faixa, cancellationToken);
            return rota is null ? ResultadoBuscaItinerario.NotEligible() : ResultadoBuscaItinerario.Found(rota);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return ResultadoBuscaItinerario.InfrastructureFailure(); }
    }

    public async Task<ResultadoMatchingCombinado> BuscarMatchingCombinadoAsync(
        string codigoLinha,
        Guid? itinerarioAnteriorId,
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
                SELECT i."Id", i."Geometria"
                FROM "Itinerarios" i
                JOIN "Sentidos" s ON s."Id" = i."SentidoId"
                JOIN "Linhas"   l ON l."Id" = s."LinhaId"
                CROSS JOIN veiculo v
                WHERE l."Codigo" = @codigo
            ),
            candidatos_global AS (
                SELECT r."Id", r."Geometria",
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
                ORDER BY sg.score ASC
                LIMIT 1
            ),
            proxima_parada_global AS (
                SELECT p."Nome" AS parada_nome,
                    ST_Distance(v.ponto, p."Localizacao"::geography) AS distancia_parada_metros
                FROM "ParadasItinerario" pi
                JOIN "Paradas" p ON p."Id" = pi."ParadaId"
                JOIN global_escolhido ge ON ge."Id" = pi."ItinerarioId"
                CROSS JOIN veiculo v
                WHERE pi."PosicaoLinha" > ge.posicao_na_rota
                ORDER BY pi."PosicaoLinha" ASC
                LIMIT 1
            ),
            rota_anterior AS (
                SELECT i."Id", i."Geometria"
                FROM "Itinerarios" i
                JOIN "Sentidos" s ON s."Id" = i."SentidoId"
                JOIN "Linhas"   l ON l."Id" = s."LinhaId"
                CROSS JOIN veiculo v
                WHERE @usar_anterior AND l."Codigo" = @codigo
                  AND i."Id" = @itinerario_id
            ),
            geometria_anterior AS (
                SELECT r.*,
                    ST_LineSubstring(r."Geometria", @fracao_min, @fracao_max) AS geometria_projecao
                FROM rota_anterior r
            ),
            candidatos_anterior AS (
                SELECT r."Id", r."Geometria",
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
                SELECT p."Nome" AS parada_nome,
                    ST_Distance(v.ponto, p."Localizacao"::geography) AS distancia_parada_metros
                FROM "ParadasItinerario" pi
                JOIN "Paradas" p ON p."Id" = pi."ParadaId"
                JOIN anterior_escolhido ae ON ae."Id" = pi."ItinerarioId"
                CROSS JOIN veiculo v
                WHERE pi."PosicaoLinha" > ae.posicao_na_rota
                ORDER BY pi."PosicaoLinha" ASC
                LIMIT 1
            ),
            rota_operacional AS (
                SELECT i."Id", i."Geometria",
                    ST_Length(i."Geometria"::geography) AS comprimento_metros
                FROM "Itinerarios" i
                WHERE @usar_operacional
                  AND i."Id" = @itinerario_operacional
                  AND EXISTS (SELECT 1 FROM global_escolhido ge
                      WHERE ge."Id" <> @itinerario_operacional)
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
                ge."Id" AS itinerario_id,
                ge.posicao_na_rota,
                ge.comprimento_metros,
                ge.distancia_rota_metros,
                ge.bearing_local,
                ST_Y(ge.ponto_rota) AS lat_rota,
                ST_X(ge.ponto_rota) AS lon_rota,
                ppg.parada_nome,
                ppg.distancia_parada_metros
            FROM global_escolhido ge
            LEFT JOIN proxima_parada_global ppg ON true
            UNION ALL
            SELECT
                'ANTERIOR'::text AS ramo,
                ae."Id" AS itinerario_id,
                ae.posicao_na_rota,
                ae.comprimento_metros,
                ae.distancia_rota_metros,
                ae.bearing_local,
                ST_Y(ae.ponto_rota) AS lat_rota,
                ST_X(ae.ponto_rota) AS lon_rota,
                ppa.parada_nome,
                ppa.distancia_parada_metros
            FROM anterior_escolhido ae
            LEFT JOIN proxima_parada_anterior ppa ON true
            UNION ALL
            SELECT
                'OPERACIONAL'::text AS ramo,
                oe."Id" AS itinerario_id,
                oe.posicao_na_rota,
                oe.comprimento_metros,
                oe.distancia_rota_metros,
                NULL::double precision AS bearing_local,
                NULL::double precision AS lat_rota,
                NULL::double precision AS lon_rota,
                NULL::text AS parada_nome,
                NULL::double precision AS distancia_parada_metros
            FROM operacional_elegivel oe
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("usar_anterior", itinerarioAnteriorId.HasValue && faixa.HasValue);
            cmd.Parameters.AddWithValue("itinerario_id", itinerarioAnteriorId ?? Guid.Empty);
            cmd.Parameters.AddWithValue("lat", latitude);
            cmd.Parameters.AddWithValue("lon", longitude);
            cmd.Parameters.AddWithValue("codigo", codigoLinha);
            cmd.Parameters.AddWithValue("bearing", bearing);
            cmd.Parameters.AddWithValue("dist_max", distanciaMaximaMetros);
            cmd.Parameters.AddWithValue("fracao_min", faixa?.Min ?? 0.0);
            cmd.Parameters.AddWithValue("fracao_max", faixa?.Max ?? 1.0);
            cmd.Parameters.AddWithValue("usar_operacional", projecaoOperacional.HasValue);
            cmd.Parameters.AddWithValue("itinerario_operacional",
                projecaoOperacional?.ItinerarioId ?? Guid.Empty);
            cmd.Parameters.AddWithValue("posicao_operacional_anterior",
                projecaoOperacional?.PosicaoAnterior ?? 0.0);
            cmd.Parameters.AddWithValue("orcamento_operacional_metros",
                projecaoOperacional?.OrcamentoMetros ?? 1.0);

            ResultadoBuscaItinerario global = ResultadoBuscaItinerario.NotEligible();
            ResultadoBuscaItinerario anterior = ResultadoBuscaItinerario.NotEligible();
            var operacional = projecaoOperacional.HasValue
                ? ResultadoProjecaoOperacional.Inelegivel()
                : ResultadoProjecaoOperacional.NaoSolicitada();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                switch (reader.GetString(reader.GetOrdinal("ramo")))
                {
                    case "GLOBAL": global = ResultadoBuscaItinerario.Found(LerRota(reader)); break;
                    case "ANTERIOR": anterior = ResultadoBuscaItinerario.Found(LerRota(reader)); break;
                    case "OPERACIONAL":
                        operacional = ResultadoProjecaoOperacional.Encontrada(new(
                            reader.GetGuid(reader.GetOrdinal("itinerario_id")),
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
        ResultadoBuscaItinerario.InfrastructureFailure(),
        ResultadoBuscaItinerario.InfrastructureFailure(),
        operacionalSolicitada ? ResultadoProjecaoOperacional.Falha() : null);

    private static EnriquecimentoRotaDto LerRota(NpgsqlDataReader reader) => new()
    {
        ItinerarioId = reader.GetGuid(reader.GetOrdinal("itinerario_id")),
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
        Guid? itinerarioId,
        FaixaProjecao? faixa,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH veiculo AS (
                SELECT
                    ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography AS ponto,
                    ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)            AS ponto_geom
            ),
            rotas AS (
                SELECT
                    i."Id",
                    i."Geometria"
                FROM "Itinerarios" i
                JOIN "Sentidos" s ON s."Id" = i."SentidoId"
                JOIN "Linhas"   l ON l."Id" = s."LinhaId"
                CROSS JOIN veiculo v
                WHERE l."Codigo" = @codigo
                  /*FILTRO_ITINERARIO*/
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
                SELECT r."Id", r."Geometria",
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
            itinerario_escolhido AS (
                SELECT
                    cs.*,
                    ST_LineInterpolatePoint(cs."Geometria", cs.posicao_na_rota) AS ponto_rota
                FROM com_score cs
                ORDER BY cs.score ASC
                LIMIT 1
            ),
            proxima_parada AS (
                SELECT
                    p."Nome"                                                         AS parada_nome,
                    ST_Distance(v.ponto, p."Localizacao"::geography)                AS distancia_parada_metros
                FROM "ParadasItinerario" pi
                JOIN "Paradas"           p  ON p."Id"  = pi."ParadaId"
                JOIN itinerario_escolhido ie ON ie."Id" = pi."ItinerarioId"
                CROSS JOIN veiculo v
                WHERE pi."PosicaoLinha" > ie.posicao_na_rota
                ORDER BY pi."PosicaoLinha" ASC
                LIMIT 1
            )
            SELECT
                ie."Id"                   AS itinerario_id,
                ie.posicao_na_rota,
                ie.comprimento_metros,
                ie.distancia_rota_metros,
                ie.bearing_local,
                ST_Y(ie.ponto_rota)        AS lat_rota,
                ST_X(ie.ponto_rota)        AS lon_rota,
                pp.parada_nome,
                pp.distancia_parada_metros
            FROM itinerario_escolhido ie
            LEFT JOIN proxima_parada pp ON true
            LIMIT 1
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = sql.Replace("/*FILTRO_ITINERARIO*/",
                itinerarioId.HasValue ? "AND i.\"Id\" = @itinerario_id" : "");
            if (itinerarioId.HasValue) cmd.Parameters.AddWithValue("itinerario_id", itinerarioId.Value);
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

            if (reader.IsDBNull(reader.GetOrdinal("itinerario_id")))
                return null;

            return new EnriquecimentoRotaDto
            {
                ItinerarioId = reader.GetGuid(reader.GetOrdinal("itinerario_id")),
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
                DistanciaProximaParadaMetros = reader.IsDBNull(reader.GetOrdinal("distancia_parada_metros"))
                                                    ? null
                                                    : reader.GetDouble(reader.GetOrdinal("distancia_parada_metros")),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao enriquecer rota para linha {linha} em ({lat},{lon})",
                codigoLinha, latitude, longitude);
            // A operação direcionada nunca confunde erro com ausência de matching.
            if (itinerarioId.HasValue) throw;
            return null;
        }
    }

    public async Task<string?> BuscarGeometriaGeoJsonAsync(
        Guid itinerarioId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ST_AsGeoJSON("Geometria") AS geojson
            FROM "Itinerarios"
            WHERE "Id" = @id
            LIMIT 1
            """;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("id", itinerarioId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return reader.IsDBNull(0) ? null : reader.GetString(0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao buscar geometria GeoJSON do itinerario {id}", itinerarioId);
            return null;
        }
    }
}
