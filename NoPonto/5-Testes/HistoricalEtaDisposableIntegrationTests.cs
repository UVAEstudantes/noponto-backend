using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Npgsql;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

// EXCLUDED from offline CI. Requires two freshly created, explicitly guarded containers.
// No localhost defaults, existing services or operational databases are allowed.
public sealed class HistoricalEtaDisposableIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GuardedPostgisAndRedis_ResolveSameDistance_RetainPhysical_ExportUnavailableModel()
    {
        var connection=Environment.GetEnvironmentVariable("NOPONTO_SHADOW_FIXTURE_POSTGRES")??throw new InvalidOperationException("Explicit disposable PostgreSQL required");
        var redisEndpoint=Environment.GetEnvironmentVariable("NOPONTO_SHADOW_FIXTURE_REDIS")??throw new InvalidOperationException("Explicit disposable Redis required");
        var guard=Environment.GetEnvironmentVariable("NOPONTO_SHADOW_FIXTURE_GUARD")??throw new InvalidOperationException("Fixture guard required");
        Assert.True(Guid.TryParseExact(guard,"N",out _));
        var settings=new NpgsqlConnectionStringBuilder(connection);Assert.Equal("127.0.0.1",settings.Host);Assert.Equal("noponto_shadow_fixture",settings.Database);
        Assert.StartsWith("127.0.0.1:",redisEndpoint);Assert.DoesNotContain(",",redisEndpoint);
        await using var source=NpgsqlDataSource.Create(connection);
        await using var conn=await source.OpenConnectionAsync();
        Assert.Equal("noponto_shadow_fixture",await new NpgsqlCommand("SELECT current_database()",conn).ExecuteScalarAsync());
        Assert.Equal(guard,await new NpgsqlCommand("SELECT token FROM noponto_shadow_fixture_guard",conn).ExecuteScalarAsync());
        using var redis=await ConnectionMultiplexer.ConnectAsync(redisEndpoint);
        Assert.Equal(guard,(string?)await redis.GetDatabase().StringGetAsync("fixture:guard"));
        var(g,trip)=HistoricalEtaRuntimeTests.Fixture();var target=trip.ProximaOcorrenciaOperacional!;
        var sql="""
            CREATE TABLE IF NOT EXISTS "Linhas" ("Id" uuid PRIMARY KEY,"Codigo" text NOT NULL);
            CREATE TABLE IF NOT EXISTS "Sentidos" ("Id" uuid PRIMARY KEY,"LinhaId" uuid NOT NULL);
            CREATE TABLE IF NOT EXISTS "PadroesOperacionais" ("Id" uuid PRIMARY KEY,"SentidoId" uuid NOT NULL);
            CREATE TABLE IF NOT EXISTS "PadroesVersoes" ("Id" uuid PRIMARY KEY,"PadraoOperacionalId" uuid NOT NULL,"Topologia" text,"Geometria" geometry(LineString,4326));
            CREATE TABLE IF NOT EXISTS "OcorrenciasParadasPadroes" ("Id" uuid PRIMARY KEY,"PadraoVersaoId" uuid,"ParadaId" uuid,"PosicaoTracado" double precision);
            INSERT INTO "Linhas" VALUES (@line,@code);
            INSERT INTO "Sentidos" VALUES (@direction,@line);
            INSERT INTO "PadroesOperacionais" VALUES (@pattern,@direction);
            INSERT INTO "PadroesVersoes" VALUES (@version,@pattern,'LINEAR',ST_GeomFromText('LINESTRING(-43.2 -22.9,-43.199 -22.9,-43.18 -22.89)',4326));
            INSERT INTO "OcorrenciasParadasPadroes" VALUES (@target,@version,@stop,.3);
            """;
        await using(var cmd=new NpgsqlCommand(sql,conn)) {
            cmd.Parameters.AddWithValue("line",g.LinhaId!.Value);cmd.Parameters.AddWithValue("code",g.CodigoLinha);
            cmd.Parameters.AddWithValue("direction",g.SentidoId!.Value);cmd.Parameters.AddWithValue("pattern",g.PadraoOperacionalId!.Value);
            cmd.Parameters.AddWithValue("version",g.PadraoVersaoId!.Value);cmd.Parameters.AddWithValue("target",target.Id);cmd.Parameters.AddWithValue("stop",target.ParadaId);
            await cmd.ExecuteNonQueryAsync();
        }
        double length,distance;
        await using(var cmd=new NpgsqlCommand("SELECT ST_Length(\"Geometria\"::geography),ST_Length(ST_LineSubstring(\"Geometria\",0,.3)::geography)-ST_Length(ST_LineSubstring(\"Geometria\",0,.2)::geography) FROM \"PadroesVersoes\"",conn)) {
            await using var reader=await cmd.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());length=reader.GetDouble(0);distance=reader.GetDouble(1);
        }
        g=g with{ComprimentoRotaMetros=length,DistanciaRestanteRotaMetros=distance,EtaProximaParadaSegundos=distance/10};
        using var cache=new RedisCache(Options.Create(new RedisCacheOptions{Configuration=redisEndpoint,InstanceName="shadow-fixture:"}));
        await cache.SetStringAsync(GpsPollingService.ChaveLinha(g.CodigoLinha),g.Ordem);
        await cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo(g.Ordem),JsonSerializer.Serialize(g));
        await cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente(g.Ordem),JsonSerializer.Serialize(g));
        var runtime=new VeiculosLinhaRuntimeReader(cache);
        var active=Assert.Single((await runtime.ListarAsync([g.CodigoLinha])).Posicoes);
        await cache.RemoveAsync(GpsPollingService.ChaveVeiculoAtivo(g.Ordem));
        var recent=Assert.Single((await runtime.ListarAsync([g.CodigoLinha])).Posicoes);
        Assert.Equal(StatusVeiculo.SemSinal,recent.Status);Assert.Equal(36d,PhysicalEta.CapturedSpeed(recent,HistoricalEtaRuntimeTests.T.AddSeconds(45),new(){Enabled=true,MaxAgeSeconds=90})!.Value,9);
        Assert.Null(HistoricalEtaAdapter.Capture(active,trip,HistoricalEtaRuntimeTests.T.AddSeconds(1))); // Redis is not causal source proof.
        var observation=HistoricalEtaAdapter.Capture(g,trip,HistoricalEtaRuntimeTests.T.AddSeconds(1));Assert.NotNull(observation);
        var geometry=new HistoricalEtaGeometryQuery(source);
        var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;var heap=GC.GetTotalMemory(true);var sw=Stopwatch.StartNew();
        var resolved=Assert.Single(await geometry.ResolveAsync([observation],default));Assert.NotNull(resolved);
        Assert.InRange(Math.Abs(resolved.Distance-distance),0,.00001);Assert.NotNull(HistoricalEtaAdapter.Payload(observation,resolved));
        var directory=Path.Combine(Path.GetTempPath(),"shadow-db-fixture-"+guard);
        try {
            using var export=new HistoricalEtaOfflineExport(Options.Create(new HistoricalEtaOfflineExportOptions{Enabled=true,Fixture=true,DirectoryPath=directory}),Options.Create(new HistoricalEtaShadowOptions{Enabled=true}),new HistoricalEtaRuntimeTests.Clock());
            var http=new HistoricalEtaRuntimeTests.HttpFactory();
            using var shadow=new HistoricalEtaShadow(Options.Create(new HistoricalEtaShadowOptions{Enabled=true}),geometry,http,new HistoricalEtaRuntimeTests.Clock(),offlineExport:export);
            await export.StartAsync(default);await shadow.ProcessAsync([observation],default);await export.StopAsync(default);
            Assert.Equal("model_unavailable",Assert.Single(shadow.Results).Status);Assert.Equal(0,http.Calls);
            Assert.Equal(JsonSerializer.Serialize(g),JsonSerializer.Serialize(active));
            var row=Directory.GetFiles(directory,"*.jsonl").SelectMany(File.ReadLines).Select(s=>JsonSerializer.Deserialize<JsonElement>(s)).Single(r=>r.TryGetProperty("status",out _));
            Assert.Equal("fixture",row.GetProperty("data_kind").GetString());Assert.Equal(distance/10,row.GetProperty("public_eta").GetDouble());
            Assert.Equal(JsonValueKind.Null,row.GetProperty("ml_eta").ValueKind);
        } finally {if(Directory.Exists(directory)){foreach(var file in Directory.GetFiles(directory))File.Delete(file);Directory.Delete(directory);}}
        output.WriteLine($"disposable_resolve_export_ms={sw.Elapsed.TotalMilliseconds:F3}; process_cpu_ms={(process.TotalProcessorTime-cpu).TotalMilliseconds:F3}; retained_heap_delta={GC.GetTotalMemory(true)-heap}");
    }
}
