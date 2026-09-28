using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.DTOs;
using NoPonto.Application.GPS;
using NoPonto.Data.Interfaces;
using NoPonto.Data.Repositories;
using Npgsql;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

/// <summary>Runner opt-in: lê somente caches efêmeros, anonimiza em memória e usa PostGIS descartável.</summary>
public sealed class GpsMatchingRepresentativeValidationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "RepresentativeMatching")]
    public async Task CatalogoReal_CorpusRedisAnonimizado_OldENewSaoEquivalentes()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MATCHING_REPRESENTATIVE_ENABLED"), "true",
                StringComparison.OrdinalIgnoreCase)) return;
        var pg = Required("POSTGIS_TEST_CONNECTION");
        var redisEndpoint = Required("MATCHING_REPRESENTATIVE_REDIS");
        var waitSeconds = int.TryParse(Environment.GetEnvironmentVariable("MATCHING_REPRESENTATIVE_WAIT_SECONDS"),
            out var configured) ? configured : 20;
        var maxInputs = int.TryParse(Environment.GetEnvironmentVariable("MATCHING_REPRESENTATIVE_MAX_INPUTS"),
            out var configuredMax) ? configuredMax : 3000;
        Assert.InRange(waitSeconds, 5, 60);
        Assert.InRange(maxInputs, 100, 3000);

        await using var source = NpgsqlDataSource.Create(pg);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { redisEndpoint }, AbortOnConnectFail = true, AllowAdmin = true,
            ConnectTimeout = 5_000, SyncTimeout = 10_000
        });
        var first = await SnapshotAsync(redis);
        await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
        var second = await SnapshotAsync(redis);
        var corpus = await BuildCorpusAsync(redis, first, second);
        var overlap = second.Keys.Count(first.ContainsKey);
        var withBearing = second.Values.Count(x => x.Bearing.HasValue);
        var withPreviousRoute = second.Count(x => first.TryGetValue(x.Key, out var p)
            && p.PadraoVersaoId.HasValue && p.PosicaoNaRota.HasValue
            && p.ComprimentoRotaMetros is > 0);
        var timestampAdvanced = second.Count(x => first.TryGetValue(x.Key, out var p)
            && x.Value.TimestampGps > p.TimestampGps);
        output.WriteLine(JsonSerializer.Serialize(new { kind="capture-gate", first=first.Count,
            second=second.Count, overlap, withBearing, withPreviousRoute, timestampAdvanced,
            global=corpus.Global.Length, combined=corpus.Combined.Length }));
        Assert.True(corpus.Global.Length >= 100,
            $"Corpus global insuficiente: {corpus.Global.Length}.");

        var repository = new GpsPadraoRepository(source, NullLogger<GpsPadraoRepository>.Instance);
        var global = new GpsMatchingGlobalSetBasedCandidate(source);
        var combined = new GpsMatchingCombinadoSetBasedCandidate(source);
        var harness = new GpsMatchingDifferentialHarness(
            (x, ct) => repository.BuscarGlobaisEmLoteAsync(x, 100, ct),
            (x, ct) => global.BuscarAsync(x, 100, ct),
            (x, ct) => repository.BuscarCombinadosEmLoteAsync(x, 100, ct),
            (x, ct) => combined.BuscarAsync(x, 100, ct),
            (x, ct) => repository.BuscarDirecionadosEmLoteAsync(x, 100, ct),
            (x, ct) => repository.BuscarDirecionadosEmLoteAsync(x, 100, ct));

        Assert.True(corpus.Combined.Length >= 1000,
            $"Corpus combinado fiel insuficiente para o gate de 1000: {corpus.Combined.Length}.");
        await harness.CompareCombinedAsync(corpus.Combined);
        output.WriteLine(JsonSerializer.Serialize(new { kind="combined-differential",
            inputs=corpus.Combined.Length, chunks=(int)Math.Ceiling(corpus.Combined.Length/100d), divergences=0 }));
        output.WriteLine(JsonSerializer.Serialize(await BenchmarkCombinedAsync(
            repository, combined, corpus.Combined.Take(1000).ToArray())));

        if (corpus.Combined.Length >= 100)
        {
            var concentrated = corpus.Combined.GroupBy(x => x.CodigoLinha)
                .OrderByDescending(x => x.Count()).SelectMany(x => x)
                .Take(Math.Min(maxInputs, corpus.Combined.Length)).ToArray();
            var dispersed = corpus.Combined.GroupBy(x => x.CodigoLinha)
                .OrderByDescending(x => x.Count()).Select(x => x.First())
                .Concat(corpus.Combined).DistinctBy(x => x.InputId)
                .Take(Math.Min(maxInputs, corpus.Combined.Length)).ToArray();
            var natural100=corpus.Combined.Take(100).ToArray();
            var concentrated100=concentrated.Take(100).ToArray();
            var dispersed100=dispersed.Take(100).ToArray();
            output.WriteLine(JsonSerializer.Serialize(ChunkStats("natural",natural100)));
            output.WriteLine(JsonSerializer.Serialize(ChunkStats("concentrated",concentrated100)));
            output.WriteLine(JsonSerializer.Serialize(ChunkStats("dispersed",dispersed100)));
            await ExplainCombinedAsync(pg,natural100);
            await ExplainCombinedAsync(pg,concentrated100);
            await ExplainCombinedAsync(pg,dispersed100);
        }
        else output.WriteLine("{\"kind\":\"combined-skipped\",\"reason\":\"no-temporally-advanced-inputs\"}");
        output.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "corpus", rawFirst = first.Count, rawSecond = second.Count,
            global = corpus.Global.Length, combined = corpus.Combined.Length,
            distinctCodes = corpus.Global.Select(x => x.CodigoLinha).Distinct().Count(),
            distinctPreviousVersions = corpus.Combined.Where(x => x.PadraoVersaoAnteriorId.HasValue)
                .Select(x => x.PadraoVersaoAnteriorId).Distinct().Count(),
            distinctOperationalVersions = corpus.Combined.Where(x => x.ProjecaoOperacional.HasValue)
                .Select(x => x.ProjecaoOperacional!.Value.PadraoVersaoId).Distinct().Count(),
            withPrevious = corpus.Combined.Count(x => x.PadraoVersaoAnteriorId.HasValue),
            withOperational = corpus.Combined.Count(x => x.ProjecaoOperacional.HasValue)
        }));
    }

    private static async Task<Dictionary<string, PosicaoVeiculoDto>> SnapshotAsync(IConnectionMultiplexer redis)
    {
        var server = redis.GetServers().Single(x => x.IsConnected);
        var keys = server.Keys(pattern: "veiculo:*:ativo", pageSize: 1000).ToArray();
        var db = redis.GetDatabase();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        var result = new Dictionary<string, PosicaoVeiculoDto>(StringComparer.Ordinal);
        foreach (var chunk in keys.Chunk(250))
        {
            var values = await Task.WhenAll(chunk.Select(k => db.HashGetAsync(k, "data")));
            foreach (var value in values)
            {
                if (!value.HasValue) continue;
                var item = JsonSerializer.Deserialize<PosicaoVeiculoDto>((byte[])value!, options);
                if (item is not null && !string.IsNullOrWhiteSpace(item.Ordem)) result[item.Ordem] = item;
            }
        }
        return result;
    }

    private static async Task<Corpus> BuildCorpusAsync(IConnectionMultiplexer redis,
        IReadOnlyDictionary<string, PosicaoVeiculoDto> first,
        IReadOnlyDictionary<string, PosicaoVeiculoDto> second)
    {
        var global = new List<EntradaMatchingGlobalLote>();
        var combined = new List<EntradaMatchingCombinadoLote>();
        var db = redis.GetDatabase();
        var index = 0;
        foreach (var pair in second.OrderBy(x => x.Value.CodigoLinha, StringComparer.Ordinal))
        {
            var now = pair.Value;
            if (now.Bearing is not { } bearing || !double.IsFinite(bearing)
                || !double.IsFinite(now.Latitude) || !double.IsFinite(now.Longitude)
                || string.IsNullOrWhiteSpace(now.CodigoLinha)) continue;
            var id = $"input-{++index:D6}";
            global.Add(new(id, now.CodigoLinha, now.Latitude, now.Longitude, bearing, 250));
            if (!first.TryGetValue(pair.Key, out var previous)
                || previous.PadraoVersaoId is not { } previousVersion
                || previous.PosicaoNaRota is not { } previousPosition
                || previous.ComprimentoRotaMetros is not { } length || length <= 0) continue;
            var seconds = (now.TimestampGps - previous.TimestampGps).TotalSeconds;
            if (seconds <= 0) continue;
            var delta = ((90d * 2d / 3.6d) * seconds + 50d) / length;
            var range = new FaixaProjecao(Math.Max(0, previousPosition - delta),
                Math.Min(1, previousPosition + delta));
            SolicitacaoProjecaoOperacional? operational = null;
            var values = await db.HashGetAsync($"veiculo:{pair.Key}:viagem",
                ["PadraoVersaoId", "PosicaoNaRotaConfirmada", "TimestampUltimaAtualizacao",
                    "PadraoOperacionalId", "SentidoId", "LinhaId"]);
            if (values.All(x => x.HasValue)
                && Guid.TryParseExact(values[0]!, "N", out var opVersion)
                && double.TryParse(values[1]!, NumberStyles.Float, CultureInfo.InvariantCulture, out var opPosition)
                && long.TryParse(values[2]!, out var ticks)
                && Guid.TryParseExact(values[3]!, "N", out var pattern)
                && Guid.TryParseExact(values[4]!, "N", out var direction)
                && Guid.TryParseExact(values[5]!, "N", out var line))
            {
                var elapsed = (now.TimestampGps - new DateTimeOffset(ticks, TimeSpan.Zero)).TotalSeconds;
                var budget = elapsed > 0 ? (90d * 2d / 3.6d) * elapsed + 50d : 0;
                if (budget > 0) operational = new(opVersion, opPosition, budget, pattern, direction, line);
            }
            combined.Add(new(id, now.CodigoLinha, previousVersion, now.Latitude, now.Longitude,
                bearing, 250, range.Valida ? range : null, operational));
        }
        return new(global.ToArray(), combined.ToArray());
    }

    private static async Task ExplainCombinedAsync(string connectionString, EntradaMatchingCombinadoLote[] inputs)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { NoResetOnClose = true,
            ApplicationName = "noponto-combined-representative-explain" };
        const string setup = """
            LOAD 'auto_explain';
            SET auto_explain.log_min_duration = 0;
            SET auto_explain.log_analyze = on;
            SET auto_explain.log_buffers = on;
            SET auto_explain.log_timing = on;
            SET auto_explain.log_nested_statements = on;
            """;
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        dataSourceBuilder.UsePhysicalConnectionInitializer(conn =>
        {
            using var command = conn.CreateCommand(); command.CommandText = setup; command.ExecuteNonQuery();
        }, async conn =>
        {
            await using var command = conn.CreateCommand(); command.CommandText = setup;
            await command.ExecuteNonQueryAsync();
        });
        await using var source = dataSourceBuilder.Build();
        var old = new GpsPadraoRepository(source, NullLogger<GpsPadraoRepository>.Instance);
        var newer = new GpsMatchingCombinadoSetBasedCandidate(source);
        _ = await old.BuscarCombinadosEmLoteAsync(inputs, 100);
        _ = await newer.BuscarAsync(inputs, 100);
    }

    private static async Task<object> BenchmarkCombinedAsync(GpsPadraoRepository old,
        GpsMatchingCombinadoSetBasedCandidate newer, EntradaMatchingCombinadoLote[] inputs,
        string distribution = "natural")
    {
        _ = await old.BuscarCombinadosEmLoteAsync(inputs, 100);
        _ = await newer.BuscarAsync(inputs, 100);
        var a = new List<double>(); var b = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var t = Stopwatch.GetTimestamp(); _ = await old.BuscarCombinadosEmLoteAsync(inputs, 100);
            a.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            t = Stopwatch.GetTimestamp(); _ = await newer.BuscarAsync(inputs, 100);
            b.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }
        a.Sort(); b.Sort();
        return new { kind = "combined", distribution, inputs = inputs.Length,
            distinctCodes = inputs.Select(x => x.CodigoLinha).Distinct().Count(),
            distinctPrevious = inputs.Select(x => x.PadraoVersaoAnteriorId).Distinct().Count(),
            distinctOperational = inputs.Where(x => x.ProjecaoOperacional.HasValue)
                .Select(x => x.ProjecaoOperacional!.Value.PadraoVersaoId).Distinct().Count(),
            commands=(int)Math.Ceiling(inputs.Length/100d), chunkSizes=inputs.Chunk(100).Select(x=>x.Length).ToArray(),
            oldSamples=a, newSamples=b, oldMedian = a[2], newMedian = b[2], oldP95 = a[^1], newP95 = b[^1],
            oldMsPerInput=a[2]/inputs.Length,newMsPerInput=b[2]/inputs.Length,speedup = a[2] / b[2] };
    }

    private static object ChunkStats(string distribution,EntradaMatchingCombinadoLote[] inputs)=>new
    {
        kind="explain-chunk",distribution,inputs=inputs.Length,
        distinctCodes=inputs.Select(x=>x.CodigoLinha).Distinct().Count(),
        distinctPrevious=inputs.Select(x=>x.PadraoVersaoAnteriorId).Where(x=>x.HasValue).Distinct().Count(),
        distinctOperational=inputs.Where(x=>x.ProjecaoOperacional.HasValue)
            .Select(x=>x.ProjecaoOperacional!.Value.PadraoVersaoId).Distinct().Count()
    };

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"{name} não configurada.");

    private sealed record Corpus(EntradaMatchingGlobalLote[] Global, EntradaMatchingCombinadoLote[] Combined);
}
