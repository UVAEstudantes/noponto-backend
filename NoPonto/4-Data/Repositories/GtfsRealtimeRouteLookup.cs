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
            SELECT DISTINCT lie."ExternalId", l."Id", l."Codigo", lower(l."TipoRota") = 'brt'
            FROM "LinhasIdentidadesExternas" lie
            JOIN "FontesEstruturais" f ON f."Id" = lie."FonteEstruturalId"
            JOIN "Linhas" l ON l."Id" = lie."LinhaId"
            WHERE f."Codigo" = 'DATARIO_GTFS' AND lie."Tipo" = 'ROUTE_ID'
              AND lie."ExternalId" = ANY(@routes)
            """, connection, tx);
        command.Parameters.AddWithValue("routes", routeIds);
        var result = new List<GtfsRouteMapping>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                result.Add(new(reader.GetString(0), reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3)));
        await tx.CommitAsync(ct);
        return result.AsReadOnly();
    }
}
