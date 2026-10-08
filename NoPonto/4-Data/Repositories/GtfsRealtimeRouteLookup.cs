using System.Data;
using NoPonto.Application.GPS;
using Npgsql;

namespace NoPonto.Data.Repositories;

// Reuses exactly the DATARIO_GTFS/ROUTE_ID identity created by the static GTFS importer.
// One selective read-only query per snapshot; never an individual query per vehicle.
public sealed class GtfsRealtimeRouteLookup(NpgsqlDataSource dataSource) : IGtfsRealtimeRouteLookup
{
    public async Task<IReadOnlyList<GtfsRouteMapping>> FindAsync(string[] routeIds, CancellationToken ct)
    {
        if (routeIds.Length == 0) return [];
        if (routeIds.Length > 10_000) throw new ArgumentOutOfRangeException(nameof(routeIds));
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using (var guard = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='2s';", connection, tx))
            await guard.ExecuteNonQueryAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT lie."ExternalId", l."Id", l."Codigo",
              origins.keys = ARRAY[lie."ExternalId" || ':BRT']::text[] AS brt,
              cardinality(agencies.keys) = 1 AND agencies.keys[1] = ANY(ARRAY[
                lie."ExternalId" || ':20001', lie."ExternalId" || ':22002',
                lie."ExternalId" || ':22003', lie."ExternalId" || ':22004',
                lie."ExternalId" || ':22005'])
                AND cardinality(origins.keys) = 1
                AND origins.keys[1] = ANY(ARRAY[lie."ExternalId" || ':BUS', lie."ExternalId" || ':BRT'])
                AND (origins.keys[1] <> lie."ExternalId" || ':BRT'
                  OR agencies.keys[1] = lie."ExternalId" || ':20001') AS modal_validated,
              ARRAY(SELECT DISTINCT suffix FROM (
                SELECT substring(sie."ExternalId" FROM length(lie."ExternalId") + 2) AS suffix
                FROM "SentidosIdentidadesExternas" sie
                JOIN "Sentidos" s ON s."Id" = sie."SentidoId"
                WHERE sie."FonteEstruturalId" = f."Id" AND sie."Tipo" = 'GTFS_DIRECTION'
                  AND s."LinhaId" = l."Id"
                  AND sie."ExternalId" IN (lie."ExternalId" || ':0', lie."ExternalId" || ':1')
              ) d ORDER BY suffix) AS directions
            FROM "LinhasIdentidadesExternas" lie
            JOIN "FontesEstruturais" f ON f."Id" = lie."FonteEstruturalId"
            JOIN "Linhas" l ON l."Id" = lie."LinhaId"
            CROSS JOIN LATERAL (
              SELECT COALESCE(array_agg(DISTINCT a."ExternalId"::text ORDER BY a."ExternalId"::text), ARRAY[]::text[]) AS keys
              FROM "LinhasIdentidadesExternas" a
              WHERE a."FonteEstruturalId" = f."Id" AND a."LinhaId" = l."Id"
                AND a."Tipo" = 'ROUTE_AGENCY'
                AND starts_with(a."ExternalId", lie."ExternalId" || ':')
            ) agencies
            CROSS JOIN LATERAL (
              SELECT COALESCE(array_agg(DISTINCT a."ExternalId"::text ORDER BY a."ExternalId"::text), ARRAY[]::text[]) AS keys
              FROM "LinhasIdentidadesExternas" a
              WHERE a."FonteEstruturalId" = f."Id" AND a."LinhaId" = l."Id"
                AND a."Tipo" = 'ROUTE_ORIGIN_V1'
                AND starts_with(a."ExternalId", lie."ExternalId" || ':')
            ) origins
            WHERE f."Codigo" = 'DATARIO_GTFS' AND lie."Tipo" = 'ROUTE_ID'
              AND lie."ExternalId" = ANY(@routes)
            """, connection, tx);
        command.Parameters.AddWithValue("routes", routeIds);
        var result = new List<GtfsRouteMapping>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                result.Add(new(reader.GetString(0), reader.GetGuid(1), reader.GetString(2),
                    reader.GetBoolean(3), reader.GetBoolean(4), reader.GetFieldValue<string[]>(5)));
        await tx.CommitAsync(ct);
        return result.AsReadOnly();
    }
}
