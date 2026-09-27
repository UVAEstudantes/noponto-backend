using NoPonto.Application.GPS;
using Npgsql;
using NpgsqlTypes;

namespace NoPonto.Data.Repositories;

public sealed class GpsStructuralHintLookup(NpgsqlDataSource dataSource)
    : IGpsStructuralHintLookup
{
    private const string StructuralSource = "DATARIO_GTFS";

    public async Task<GpsStructuralHintCandidates> FindAsync(
        string? routeId, string? directionId, string? shapeId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT DISTINCT lie."LinhaId", l."Codigo"
            FROM "LinhasIdentidadesExternas" lie
            JOIN "FontesEstruturais" f ON f."Id" = lie."FonteEstruturalId"
            JOIN "Linhas" l ON l."Id" = lie."LinhaId"
            WHERE @route_id IS NOT NULL AND f."Codigo" = @fonte
              AND lie."Tipo" = 'ROUTE_ID' AND lie."ExternalId" = @route_id;

            SELECT DISTINCT sie."SentidoId", s."LinhaId"
            FROM "SentidosIdentidadesExternas" sie
            JOIN "FontesEstruturais" f ON f."Id" = sie."FonteEstruturalId"
            JOIN "Sentidos" s ON s."Id" = sie."SentidoId"
            WHERE @route_id IS NOT NULL AND @direction_id IS NOT NULL
              AND f."Codigo" = @fonte AND sie."Tipo" = 'GTFS_DIRECTION'
              AND sie."ExternalId" = concat(@route_id, ':', @direction_id);

            SELECT DISTINCT pie."PadraoOperacionalId", po."VersaoAtualId", po."SentidoId", s."LinhaId"
            FROM "PadroesIdentidadesExternas" pie
            JOIN "FontesEstruturais" f ON f."Id" = pie."FonteEstruturalId"
            JOIN "PadroesOperacionais" po ON po."Id" = pie."PadraoOperacionalId"
            JOIN "Sentidos" s ON s."Id" = po."SentidoId"
            WHERE @shape_id IS NOT NULL AND f."Codigo" = @fonte
              AND pie."Tipo" = 'SHAPE_ID' AND pie."ExternalId" = @shape_id
              AND po."VersaoAtualId" IS NOT NULL;
            """);
        command.Parameters.AddWithValue("fonte", StructuralSource);
        command.Parameters.AddWithValue("route_id", NpgsqlDbType.Text, (object?)routeId ?? DBNull.Value);
        command.Parameters.AddWithValue("direction_id", NpgsqlDbType.Text, (object?)directionId ?? DBNull.Value);
        command.Parameters.AddWithValue("shape_id", NpgsqlDbType.Text, (object?)shapeId ?? DBNull.Value);

        var routes = new List<GpsStructuralLineCandidate>();
        var directions = new List<GpsStructuralDirectionCandidate>();
        var shapes = new List<GpsStructuralPatternCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            routes.Add(new(reader.GetGuid(0), reader.GetString(1)));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            directions.Add(new(reader.GetGuid(0), reader.GetGuid(1)));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            shapes.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3)));
        return new(routes.AsReadOnly(), directions.AsReadOnly(), shapes.AsReadOnly());
    }
}
