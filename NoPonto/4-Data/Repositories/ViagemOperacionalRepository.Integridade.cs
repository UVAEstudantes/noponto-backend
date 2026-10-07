using NoPonto.Application.GPS;
using Npgsql;

namespace NoPonto.Data.Repositories;

public sealed partial class ViagemOperacionalRepository
{
    internal async Task<ProvaGeometricaCircular?> BuscarProvaCircularAsync(NpgsqlConnection connection,
        ViagemOperacionalState anterior, EstruturaViagem estrutura, PosicaoVeiculoDto gps, CancellationToken ct)
    {
        if (anterior.Integridade?.Ancora is not { } a || !gps.MatchingOperacionalPlausivel
            || estrutura.PadraoVersaoId != anterior.Observada.PadraoVersaoId
            || gps.PosicaoNaRota is not { } p || p >= a.Posicao
            || gps.Bearing is not { } bearing || !double.IsFinite(bearing)) return null;
        // Mesma janela de tangente (.025) e tolerância angular (80°) do matching produtivo.
        // Além disso exige geometria fechada simples e trecho dirigido dentro do corredor
        // físico observado. Topologia declarada sobre uma linha aberta nunca prova wrap.
        const string sql = """
            WITH v AS (
                SELECT "Geometria" AS g FROM "PadroesVersoes"
                WHERE "Id"=@versao AND "Topologia"='CIRCULAR'
            ), pontos AS (
                SELECT g, ST_SetSRID(ST_MakePoint(@lon0,@lat0),4326) AS a,
                    ST_SetSRID(ST_MakePoint(@lon1,@lat1),4326) AS b,
                    ST_LineSubstring(g,@p0,1) AS fim, ST_LineSubstring(g,0,@p1) AS inicio
                FROM v
            ) SELECT ST_IsClosed(g) AND ST_IsSimple(g),
                ST_DWithin(ST_LineInterpolatePoint(g,@p0)::geography,a::geography,@tol)
                AND ST_DWithin(ST_LineInterpolatePoint(g,@p1)::geography,b::geography,@tol)
                AND ST_CoveredBy(ST_Collect(fim,inicio),ST_Buffer(ST_MakeLine(a,b)::geography,@tol)::geometry)
                AND abs(mod((degrees(ST_Azimuth(
                    ST_LineInterpolatePoint(g,greatest(0,@p0-0.025))::geography,
                    ST_LineInterpolatePoint(g,least(1,@p0+0.025))::geography))-@bearing0+540)::numeric,360)-180)<80
                AND abs(mod((degrees(ST_Azimuth(
                    ST_LineInterpolatePoint(g,greatest(0,@p1-0.025))::geography,
                    ST_LineInterpolatePoint(g,least(1,@p1+0.025))::geography))-@bearing1+540)::numeric,360)-180)<80,
                ST_Length(fim::geography)+ST_Length(inicio::geography),
                ST_Length(ST_LineSubstring(g,@p1,@p0)::geography), ST_Length(g::geography)
            FROM pontos
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("versao",estrutura.PadraoVersaoId);
        command.Parameters.AddWithValue("p0",a.Posicao);
        command.Parameters.AddWithValue("p1",p);
        command.Parameters.AddWithValue("lat0",a.Latitude);
        command.Parameters.AddWithValue("lon0",a.Longitude);
        command.Parameters.AddWithValue("lat1",gps.Latitude);
        command.Parameters.AddWithValue("lon1",gps.Longitude);
        command.Parameters.AddWithValue("bearing0",a.Bearing);
        command.Parameters.AddWithValue("bearing1",bearing);
        command.Parameters.AddWithValue("tol",options.Value.ToleranciaProjecaoMetros);
        await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetBoolean(0),
            !reader.IsDBNull(1) && reader.GetBoolean(1),reader.GetDouble(2),reader.GetDouble(3),reader.GetDouble(4)) : null;
    }
}
