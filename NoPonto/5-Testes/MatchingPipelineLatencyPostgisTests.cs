using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class MatchingPipelineLatencyPostgisTests : IClassFixture<PostgisGpsFixture>
{
    private readonly PostgisGpsFixture _db;
    private readonly ITestOutputHelper _output;
    public MatchingPipelineLatencyPostgisTests(PostgisGpsFixture db, ITestOutputHelper output)
    { _db = db; _output = output; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PostGIS")]
    public async Task NextStop_PreservesLinearEndNull_AndCircularWrapDistance(bool circular)
    {
        if (circular)
        {
            await using var seed = _db.DataSource.CreateCommand("""
                WITH p AS (
                  INSERT INTO "Paradas" VALUES (gen_random_uuid(),'Circular stop',
                    (SELECT ST_LineInterpolatePoint("Geometria",.25) FROM "PadroesVersoes" WHERE "Id"=@id))
                  RETURNING "Id"
                ) INSERT INTO "OcorrenciasParadasPadroes"
                  SELECT gen_random_uuid(),"Id",@id,1,.25,100,0 FROM p
                """);
            seed.Parameters.AddWithValue("id", _db.Circular);
            await seed.ExecuteNonQueryAsync();
        }
        var repo = new GpsPadraoRepository(_db.DataSource, NullLogger<GpsPadraoRepository>.Instance);
        var route = circular ? _db.Circular : _db.R1;
        var input = new EntradaMatchingCombinadoLote("edge", circular ? "CIRCULAR" : "GPS23", route,
            circular ? .004 : -22.9, circular ? 0 : -43.191, circular ? 180 : 90,
            250, new(.8, .99));
        var result = Assert.Single((await repo.BuscarCombinadosEmLoteAsync([input], 100,
            MotorCombinadoMatchingLote.SetBased)).Resultados).Resultado;
        Assert.Equal(route, result.Global.Rota!.PadraoVersaoId);
        Assert.Equal(route, result.Anterior.Rota!.PadraoVersaoId);
        if (!circular)
        {
            Assert.Null(result.Global.Rota.ProximaParadaNome);
            Assert.Null(result.Anterior.Rota.ProximaParadaNome);
            Assert.Null(result.Global.Rota.DistanciaRestanteRotaMetros);
            Assert.Null(result.Anterior.Rota.DistanciaRestanteRotaMetros);
            return;
        }
        await using var expected = _db.DataSource.CreateCommand("""
            SELECT ST_Length("Geometria"::geography)
              -ST_Length(ST_LineSubstring("Geometria",0,.9)::geography)
              +ST_Length(ST_LineSubstring("Geometria",0,.25)::geography)
            FROM "PadroesVersoes" WHERE "Id"=@id
            """);
        expected.Parameters.AddWithValue("id", route);
        var distance = (double)(await expected.ExecuteScalarAsync())!;
        Assert.Equal("Circular stop", result.Global.Rota.ProximaParadaNome);
        Assert.InRange(Math.Abs(result.Global.Rota.DistanciaRestanteRotaMetros!.Value-distance), 0, .00001);
        Assert.InRange(Math.Abs(result.Anterior.Rota.DistanciaRestanteRotaMetros!.Value-distance), 0, .00001);
    }

    [Fact]
    [Trait("Category", "PostGIS")]
    public async Task SharedNextStop_ComputesGeographicLengthsOncePerStop_NotPerVehicleBranch()
    {
        // Count real PostGIS length calls, rather than assert a noisy wall-clock limit.
        // The wrapper exists only in this fixture's private schema and delegates unchanged.
        await using (var setup = _db.DataSource.CreateCommand($$"""
            CREATE SEQUENCE "{{_db.Schema}}".length_calls;
            CREATE FUNCTION "{{_db.Schema}}".st_length(geography) RETURNS double precision
            LANGUAGE plpgsql VOLATILE AS $body$
            BEGIN
              PERFORM nextval('"{{_db.Schema}}".length_calls');
              RETURN public.st_length($1);
            END $body$;
            """)) await setup.ExecuteNonQueryAsync();
        try
        {
            var inputs = Enumerable.Range(0, 100).Select(i => new EntradaMatchingCombinadoLote(
                $"length-{i}", "GPS23", _db.R1, -22.9, -43.205 + i * .00001,
                90, 250, new(.1, .9))).ToArray();
            var repo = new GpsPadraoRepository(_db.DataSource, NullLogger<GpsPadraoRepository>.Instance);
            var result = await repo.BuscarCombinadosEmLoteAsync(inputs, 100,
                MotorCombinadoMatchingLote.SetBased);
            Assert.Equal(100, result.Resultados.Count);
            Assert.Equal(0, result.Metricas.MatchingFallbackCommandsPostgres);
            Assert.All(result.Resultados, x =>
            {
                Assert.Equal(_db.R1, x.Resultado.Global.Rota!.PadraoVersaoId);
                Assert.Equal(_db.R1, x.Resultado.Anterior.Rota!.PadraoVersaoId);
                Assert.Equal("Parada controlada", x.Resultado.Global.Rota.ProximaParadaNome);
                Assert.InRange(x.Resultado.Global.Rota.DistanciaRestanteRotaMetros!.Value, 300, 1100);
            });
            await using var count = _db.DataSource.CreateCommand("SELECT last_value FROM length_calls");
            var calls = (long)(await count.ExecuteScalarAsync())!;
            _output.WriteLine($"PostGIS ST_Length calls={calls}; inputs=100; branches=200");
            // Two projections per vehicle still require physical route lengths.
            // Shared stop and route constants must not add two more calls per vehicle.
            Assert.InRange(calls, 200, 210);
        }
        finally
        {
            await using var cleanup = _db.DataSource.CreateCommand($$"""
                DROP FUNCTION "{{_db.Schema}}".st_length(geography);
                DROP SEQUENCE "{{_db.Schema}}".length_calls;
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task DenseRoutes_BoundedCombinedChunks_PreserveEveryInput()
    {
        await using (var command = _db.DataSource.CreateCommand("""
            UPDATE "PadroesVersoes" SET "Geometria"=ST_Segmentize("Geometria",0.00001)
            WHERE "Id" IN (SELECT "Id" FROM "PadroesOperacionais" WHERE "SentidoId"=@sentido)
            """))
        {
            command.Parameters.AddWithValue("sentido", _db.Sentido1);
            await command.ExecuteNonQueryAsync();
        }
        var logger = new PhaseLogger();
        var repository = new GpsPadraoRepository(_db.DataSource, logger);
        var measurements = new List<object>();
        foreach (var identical in new[] { false, true })
        {
            var inputs = Enumerable.Range(0, 300).Select(i => new EntradaMatchingCombinadoLote(
                $"synthetic-{i}", "GPS23", _db.R1, -22.9,
                -43.205 + (identical ? i % 25 : i) * .00001, 90, 250, new(.1, .9))).ToArray();
            for (var iteration = 0; iteration < 4; iteration++)
            {
                var watch = Stopwatch.StartNew();
                var result = await repository.BuscarCombinadosEmLoteAsync(inputs, 100,
                    MotorCombinadoMatchingLote.SetBased);
                watch.Stop();
                Assert.Equal(inputs.Select(x => x.InputId), result.Resultados.Select(x => x.InputId));
                Assert.All(result.Resultados, x =>
                {
                    Assert.Equal(StatusBuscaPadrao.Found, x.Resultado.Global.Status);
                    Assert.Equal(_db.R1, x.Resultado.Global.Rota!.PadraoVersaoId);
                    Assert.Equal(StatusBuscaPadrao.Found, x.Resultado.Anterior.Status);
                    Assert.Equal(_db.R1, x.Resultado.Anterior.Rota!.PadraoVersaoId);
                    Assert.Equal("Parada controlada", x.Resultado.Global.Rota.ProximaParadaNome);
                });
                measurements.Add(new { identical, iteration, milliseconds = watch.Elapsed.TotalMilliseconds,
                    commands = result.Metricas.MatchingBatchCommandsPostgres,
                    fallback = result.Metricas.MatchingFallbackCommandsPostgres,
                    command_ms = result.Metricas.Comandos.Sum(x => x.Duracao.TotalMilliseconds),
                    phases = logger.Last });
            }
        }
        var json = JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true });
        if (Environment.GetEnvironmentVariable("PIPELINE_LATENCY_OUTPUT") is { Length: > 0 } path)
            await File.WriteAllTextAsync(path, json);
        _output.WriteLine(json);
    }

    private sealed class PhaseLogger : ILogger<GpsPadraoRepository>
    {
        internal Dictionary<string, object?>? Last;
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                var fields = values.Where(x => x.Key != "{OriginalFormat}").ToDictionary();
                if (fields.ContainsKey("execute_reader_ms")) Last = fields;
            }
        }
    }
}
