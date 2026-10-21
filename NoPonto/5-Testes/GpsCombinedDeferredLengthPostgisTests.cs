using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

/// <summary>Only run against the owned disposable PostGIS laboratory. No timing assertion.</summary>
public sealed class GpsCombinedDeferredLengthPostgisTests(PostgisGpsFixture db)
    : IClassFixture<PostgisGpsFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Combined_CalculaComprimentoSomenteAposRanking_SemMudarDtoOuOrdem(bool semVencedor)
    {
        var repository = new GpsPadraoRepository(db.DataSource, NullLogger<GpsPadraoRepository>.Instance);
        var latitude = semVencedor ? 0 : -22.9;
        var longitude = semVencedor ? 0 : -43.2;
        var oracle = await repository.BuscarEnriquecimentoAsync("GPS23", latitude, longitude, 90, 250);

        await using var setup = db.DataSource.CreateCommand($$"""
            INSERT INTO "PadroesOperacionais" VALUES
              ('46000000-0000-0000-0000-000000000046','{{db.Sentido1}}','46000000-0000-0000-0000-000000000046');
            INSERT INTO "PadroesVersoes" VALUES
              ('46000000-0000-0000-0000-000000000046','46000000-0000-0000-0000-000000000046',
               ST_GeomFromText('LINESTRING(-80 40,-79 40)',4326),'LINEAR');
            CREATE TABLE "LengthCalls" (calls integer NOT NULL,winner_calls integer NOT NULL);
            INSERT INTO "LengthCalls" VALUES(0,0);
            -- Test-only counter in this random schema. The real formula delegates to PostGIS.
            -- Count the whole rejected geometries, not substring distances to the next stop.
            CREATE FUNCTION "{{db.Schema}}".st_length(geography) RETURNS double precision
            LANGUAGE plpgsql VOLATILE AS $body$
            BEGIN
              IF ST_AsEWKB($1::geometry) IN (
                ST_AsEWKB(ST_GeomFromText('LINESTRING(-80 40,-79 40)',4326)),
                ST_AsEWKB(ST_GeomFromText('LINESTRING(-43.19 -22.9001,-43.21 -22.9001)',4326))) THEN
                UPDATE "{{db.Schema}}"."LengthCalls" SET calls=calls+1;
              END IF;
              IF ST_AsEWKB($1::geometry)=ST_AsEWKB(ST_GeomFromText('LINESTRING(-43.21 -22.9,-43.19 -22.9)',4326)) THEN
                UPDATE "{{db.Schema}}"."LengthCalls" SET winner_calls=winner_calls+1;
              END IF;
              RETURN public.st_length($1,true);
            END $body$;
            """);
        await setup.ExecuteNonQueryAsync();
        try
        {
            EntradaMatchingCombinadoLote[] inputs =
            [
                new("a-first", "GPS23", null, latitude, longitude, 90, 250, null),
                new("z-last", "GPS23", null, latitude, longitude, 90, 250, null)
            ];
            var result = await new GpsMatchingCombinadoSetBasedCandidate(db.DataSource).BuscarAsync(inputs);
            Assert.Equal(inputs.Select(x => x.InputId), result.Resultados.Select(x => x.InputId));
            Assert.Equal(1, result.Metricas.CombinedSetBasedCommands);
            Assert.Equal(0, result.Metricas.MatchingFallbackCommandsPostgres);
            foreach (var item in result.Resultados)
            {
                GpsMatchingDifferentialHarness.CompareSearch(
                    oracle is null ? ResultadoBuscaPadrao.NotEligible() : ResultadoBuscaPadrao.Found(oracle),
                    item.Resultado.Global, item.InputId);
                Assert.Equal(StatusBuscaPadrao.NotEligible, item.Resultado.Anterior.Status);
            }
            await using var count = db.DataSource.CreateCommand("SELECT calls FROM \"LengthCalls\"");
            Assert.Equal(0, (int)(await count.ExecuteScalarAsync())!);
            await using var winnerCount = db.DataSource.CreateCommand("SELECT winner_calls FROM \"LengthCalls\"");
            Assert.Equal(semVencedor ? 0 : 1, (int)(await winnerCount.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var cleanup = db.DataSource.CreateCommand($$"""
                DROP FUNCTION "{{db.Schema}}".st_length(geography);
                DROP TABLE "LengthCalls";
                DELETE FROM "PadroesVersoes" WHERE "Id"='46000000-0000-0000-0000-000000000046';
                DELETE FROM "PadroesOperacionais" WHERE "Id"='46000000-0000-0000-0000-000000000046';
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
