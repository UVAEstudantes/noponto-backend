using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NoPonto.Application.GPS;

namespace NoPonto.Data.Repositories;

/// <summary>
/// Implementação de <see cref="IGpsItinerarioRepository"/> usando NpgsqlCommand
/// direto (sem passar pelo pipeline do EF Core) para evitar o wrap em subquery
/// que causa conflito de case nos aliases PostgreSQL.
///
/// Estratégia de seleção do itinerário (ida vs. volta):
///   Para cada linha há dois itinerários. Usamos o bearing do veículo para
///   desambiguar: selecionamos o itinerário cuja direção (ST_Azimuth entre
///   início e fim da LineString) é mais próxima do bearing do veículo.
///   Confirmamos com ST_Distance para descartar rotas muito distantes.
/// </summary>
public sealed class GpsItinerarioRepository : IGpsItinerarioRepository
{
    private readonly TransporteDbContext _db;
    private readonly ILogger<GpsItinerarioRepository> _logger;

    public GpsItinerarioRepository(TransporteDbContext db, ILogger<GpsItinerarioRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<EnriquecimentoRotaDto?> BuscarEnriquecimentoAsync(
        string codigoLinha,
        double latitude,
        double longitude,
        double bearing,
        double distanciaMaximaMetros,
        CancellationToken cancellationToken = default)
    {
        /*
         * A query faz o seguinte em uma única round-trip:
         *
         * 1. Busca os itinerários da linha via JOIN Sentidos → Linhas.
         * 2. Para cada itinerário calcula:
         *    - ST_Distance(geography): distância do veículo à rota em metros.
         *    - ST_Azimuth: bearing da rota (início → fim).
         *    - ST_LineLocatePoint: posição na rota (0.0 → 1.0).
         *    - ST_Length(geography): comprimento total em metros.
         * 3. Filtra rotas além de distanciaMaximaMetros.
         * 4. Ordena por alinhamento de bearing (menor diferença angular),
         *    depois por distância — escolhe o itinerário mais plausível.
         * 5. LEFT JOIN com a próxima parada à frente do veículo.
         */
        const string sql = """
            WITH veiculo AS (
                SELECT ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography AS ponto,
                       ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)            AS ponto_geom
            ),
            itinerarios_linha AS (
                SELECT
                    i."Id",
                    i."Geometria",
                    ST_Length(i."Geometria"::geography)                        AS comprimento_metros,
                    ST_Distance(v.ponto, i."Geometria"::geography)             AS distancia_rota_metros,
                    ST_LineLocatePoint(i."Geometria", v.ponto_geom)            AS posicao_na_rota,
                    degrees(ST_Azimuth(
                        ST_StartPoint(i."Geometria")::geography,
                        ST_EndPoint(i."Geometria")::geography
                    ))                                                         AS bearing_rota
                FROM "Itinerarios" i
                JOIN "Sentidos"  s ON s."Id"   = i."SentidoId"
                JOIN "Linhas"    l ON l."Id"   = s."LinhaId"
                CROSS JOIN veiculo v
                WHERE l."Codigo" = @codigo
            ),
            itinerario_escolhido AS (
                SELECT *,
                    ABS(MOD((bearing_rota - @bearing + 540.0)::numeric, 360.0) - 180.0) AS diff_bearing
                FROM itinerarios_linha
                WHERE distancia_rota_metros <= @dist_max
                ORDER BY diff_bearing ASC, distancia_rota_metros ASC
                LIMIT 1
            ),
            proxima_parada AS (
                SELECT
                    p."Nome"                                                              AS parada_nome,
                    ST_Distance(v.ponto, p."Localizacao"::geography)                     AS distancia_parada_metros
                FROM "ParadasItinerario" pi
                JOIN "Paradas"           p  ON p."Id"  = pi."ParadaId"
                JOIN itinerario_escolhido ie ON ie."Id" = pi."ItinerarioId"
                CROSS JOIN veiculo v
                WHERE pi."PosicaoLinha" > ie.posicao_na_rota
                ORDER BY pi."PosicaoLinha" ASC
                LIMIT 1
            )
            SELECT
                ie."Id"                    AS itinerario_id,
                ie.posicao_na_rota,
                ie.comprimento_metros,
                ie.distancia_rota_metros,
                pp.parada_nome,
                pp.distancia_parada_metros
            FROM itinerario_escolhido ie
            LEFT JOIN proxima_parada pp ON true
            LIMIT 1
            """;

        try
        {
            var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("lat", latitude);
            cmd.Parameters.AddWithValue("lon", longitude);
            cmd.Parameters.AddWithValue("codigo", codigoLinha);
            cmd.Parameters.AddWithValue("bearing", bearing);
            cmd.Parameters.AddWithValue("dist_max", distanciaMaximaMetros);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            if (reader.IsDBNull(reader.GetOrdinal("itinerario_id")))
                return null;

            return new EnriquecimentoRotaDto
            {
                ItinerarioId              = reader.GetGuid(reader.GetOrdinal("itinerario_id")),
                PosicaoNaRota             = reader.GetDouble(reader.GetOrdinal("posicao_na_rota")),
                ComprimentoRotaMetros     = reader.GetDouble(reader.GetOrdinal("comprimento_metros")),
                DistanciaARotaMetros      = reader.GetDouble(reader.GetOrdinal("distancia_rota_metros")),
                ProximaParadaNome         = reader.IsDBNull(reader.GetOrdinal("parada_nome"))
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
            var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(cancellationToken);

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
            _logger.LogWarning(ex, "Falha ao buscar geometria GeoJSON do itinerário {id}", itinerarioId);
            return null;
        }
    }
}