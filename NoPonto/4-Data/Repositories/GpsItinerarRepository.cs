using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NoPonto.Application.GPS;

namespace NoPonto.Data.Repositories;

/// <summary>
/// Implementação de IGpsItinerarioRepository.
///
/// Usa NpgsqlDataSource injetado diretamente em vez de pegar a conexão
/// do DbContext. Isso garante que cada chamada paralela abre sua própria
/// conexão do pool do Npgsql, eliminando o NpgsqlOperationInProgressException
/// que ocorria quando múltiplos veículos eram enriquecidos em paralelo.
/// </summary>
public sealed class GpsItinerarioRepository : IGpsItinerarioRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TransporteDbContext _db;
    private readonly ILogger<GpsItinerarioRepository> _logger;

    public GpsItinerarioRepository(
        NpgsqlDataSource dataSource,
        TransporteDbContext db,
        ILogger<GpsItinerarioRepository> logger)
    {
        _dataSource = dataSource;
        _db         = db;
        _logger     = logger;
    }

    public async Task<EnriquecimentoRotaDto?> BuscarEnriquecimentoAsync(
        string codigoLinha,
        double latitude,
        double longitude,
        double bearing,
        double distanciaMaximaMetros,
        CancellationToken cancellationToken = default)
    {
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
            // Abre conexão própria do pool — não compartilha com nenhuma outra query
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd  = conn.CreateCommand();

            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("lat",      latitude);
            cmd.Parameters.AddWithValue("lon",      longitude);
            cmd.Parameters.AddWithValue("codigo",   codigoLinha);
            cmd.Parameters.AddWithValue("bearing",  bearing);
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
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd  = conn.CreateCommand();

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
                "Falha ao buscar geometria GeoJSON do itinerário {id}", itinerarioId);
            return null;
        }
    }
}