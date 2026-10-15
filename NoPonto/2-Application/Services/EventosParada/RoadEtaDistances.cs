using Npgsql;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Services.EventosParada;

public sealed record RoadEtaDistanceRequest(PosicaoVeiculoDto Position, Guid TargetId, Guid StopId, int TargetOrder);
public interface IRoadEtaDistances
{
    Task<IReadOnlyList<double?>> ResolveAsync(IReadOnlyList<RoadEtaDistanceRequest> requests, CancellationToken ct);
}

/// <summary>Bounded READ ONLY batches on versioned geometry. Same prefix-length
/// subtraction as the GPS matching; no fraction × total length approximation.</summary>
public sealed class RoadEtaDistances(NpgsqlDataSource source) : IRoadEtaDistances
{
    internal const string Sql = """
        SELECT x.n, ST_Length(ST_LineSubstring(v."Geometria",0,t."PosicaoTracado")::geography)
          - ST_Length(ST_LineSubstring(v."Geometria",0,x.pos)::geography) distance
        FROM unnest(@targets::uuid[],@stops::uuid[],@orders::int[],@versions::uuid[],@nexts::uuid[],
          @patterns::uuid[],@directions::uuid[],@lines::uuid[],@codes::text[],
          @positions::double precision[],@anchors::double precision[],@topologies::text[]) WITH ORDINALITY
          x(target,stop,ord,version,next,pattern,direction,line,code,pos,anchor,topology,n)
        JOIN "OcorrenciasParadasPadroes" t ON t."Id"=x.target AND t."PadraoVersaoId"=x.version
          AND t."ParadaId"=x.stop AND t."Ordem"=x.ord
        JOIN "OcorrenciasParadasPadroes" a ON a."Id"=x.next AND a."PadraoVersaoId"=x.version
        JOIN "PadroesVersoes" v ON v."Id"=x.version AND v."PadraoOperacionalId"=x.pattern
        JOIN "PadroesOperacionais" p ON p."Id"=v."PadraoOperacionalId" AND p."SentidoId"=x.direction
        JOIN "Sentidos" s ON s."Id"=p."SentidoId" AND s."LinhaId"=x.line
        JOIN "Linhas" l ON l."Id"=s."LinhaId" AND l."Codigo"=x.code
        WHERE x.pos>=0 AND x.pos<a."PosicaoTracado" AND a."PosicaoTracado"<=t."PosicaoTracado"
          AND t."PosicaoTracado"<=1 AND a."Ordem">0 AND t."Ordem">=a."Ordem"
          AND v."Topologia" IN ('LINEAR','CIRCULAR') AND v."Topologia"=x.topology
          AND NOT EXISTS (SELECT 1 FROM "OcorrenciasParadasPadroes" o
            WHERE o."PadraoVersaoId"=v."Id" AND o."PosicaoTracado">x.pos
              AND (o."PosicaoTracado",o."Ordem") < (a."PosicaoTracado",a."Ordem"))
          AND NOT EXISTS (SELECT 1 FROM "OcorrenciasParadasPadroes" o
            WHERE o."PadraoVersaoId"=v."Id" AND o."Ordem" BETWEEN a."Ordem" AND t."Ordem"
              AND (o."PosicaoTracado"<a."PosicaoTracado" OR o."PosicaoTracado">t."PosicaoTracado"))
          AND NOT EXISTS (SELECT 1 FROM (
            SELECT o."PosicaoTracado" fraction, lag(o."PosicaoTracado") OVER (ORDER BY o."Ordem") previous
            FROM "OcorrenciasParadasPadroes" o WHERE o."PadraoVersaoId"=v."Id"
              AND o."Ordem" BETWEEN a."Ordem" AND t."Ordem") sequence
            WHERE fraction<previous)
          AND abs(ST_Length(ST_LineSubstring(v."Geometria",0,a."PosicaoTracado")::geography)
            - ST_Length(ST_LineSubstring(v."Geometria",0,x.pos)::geography)-x.anchor)<=0.01
        ORDER BY x.n
        """;

    public async Task<IReadOnlyList<double?>> ResolveAsync(IReadOnlyList<RoadEtaDistanceRequest> requests, CancellationToken ct)
    {
        if (requests.Count is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(requests));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        ct = deadline.Token;
        var result = new double?[requests.Count];
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var settings = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='500ms'; SET LOCAL lock_timeout='100ms';", conn, tx))
            await settings.ExecuteNonQueryAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn, tx) { CommandTimeout = 1 };
        cmd.Parameters.AddWithValue("targets", requests.Select(x=>x.TargetId).ToArray());
        cmd.Parameters.AddWithValue("stops", requests.Select(x=>x.StopId).ToArray());
        cmd.Parameters.AddWithValue("orders", requests.Select(x=>x.TargetOrder).ToArray());
        cmd.Parameters.AddWithValue("versions", requests.Select(x=>x.Position.PadraoVersaoId!.Value).ToArray());
        cmd.Parameters.AddWithValue("nexts", requests.Select(x=>x.Position.ProximaOcorrenciaParadaPadraoId!.Value).ToArray());
        cmd.Parameters.AddWithValue("patterns", requests.Select(x=>x.Position.PadraoOperacionalId!.Value).ToArray());
        cmd.Parameters.AddWithValue("directions", requests.Select(x=>x.Position.SentidoId!.Value).ToArray());
        cmd.Parameters.AddWithValue("lines", requests.Select(x=>x.Position.LinhaId!.Value).ToArray());
        cmd.Parameters.AddWithValue("codes", requests.Select(x=>x.Position.CodigoLinha).ToArray());
        cmd.Parameters.AddWithValue("positions", requests.Select(x=>x.Position.PosicaoNaRota!.Value).ToArray());
        cmd.Parameters.AddWithValue("anchors", requests.Select(x=>x.Position.DistanciaRestanteRotaMetros!.Value).ToArray());
        cmd.Parameters.AddWithValue("topologies", requests.Select(x=>x.Position.TopologiaPadrao!).ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            var metres = reader.GetDouble(1);
            if (double.IsFinite(metres) && metres > 0) result[checked((int)reader.GetInt64(0))-1] = metres;
        }
        return result;
    }
}
