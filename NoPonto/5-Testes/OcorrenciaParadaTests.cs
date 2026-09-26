using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Data.Interfaces;
using NoPonto.Data.Repositories;
using Npgsql;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

/// <summary>SQL PostgreSQL e CAS Redis reais; schema e chave exclusivos, removidos ao terminar.</summary>
public sealed class OcorrenciaParadaTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _schema = $"ocorrencia_{Guid.NewGuid():N}";
    private readonly string _ordem = $"TESTE-OCORRENCIA-{Guid.NewGuid():N}";
    private readonly Guid _itinerary = Guid.NewGuid();
    private readonly DateTimeOffset _t0 = DateTimeOffset.UtcNow.AddMinutes(-2);
    private ConnectionMultiplexer _redis = null!;
    private NpgsqlDataSource _admin = null!, _source = null!;
    private OcorrenciaParadaRepository _sequence = null!;
    private ViagemObservadaRepository _repo = null!;
    private string Key => ViagemObservadaRepository.ChaveVeiculoViagem(_ordem);
    private IDatabase Db => _redis.GetDatabase();

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Defina POSTGIS_TEST_CONNECTION para banco isolado.");
        _admin = NpgsqlDataSource.Create(connection);
        await using var create = _admin.CreateCommand($"""
            CREATE SCHEMA "{_schema}";
            CREATE TABLE "{_schema}"."OcorrenciasParadasPadroes" (
                "Id" uuid PRIMARY KEY, "PadraoVersaoId" uuid NOT NULL, "ParadaId" uuid NOT NULL,
                "Ordem" integer NOT NULL, "PosicaoTracado" double precision NOT NULL,
                "DistanciaAcumuladaMetros" double precision NOT NULL DEFAULT 0,
                "DistanciaDaLinhaMetros" double precision NOT NULL DEFAULT 0);
            CREATE INDEX ON "{_schema}"."OcorrenciasParadasPadroes" ("PadraoVersaoId", "Ordem");
            """);
        await create.ExecuteNonQueryAsync();
        _source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection)
            { SearchPath = _schema }.ConnectionString);
        _redis = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6380");
        _sequence = new(_source);
        _repo = Repo(_sequence);
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null) { await Db.KeyDeleteAsync(Key); await _redis.DisposeAsync(); }
        if (_source is not null) await _source.DisposeAsync();
        if (_admin is not null)
        {
            await using var drop = _admin.CreateCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE");
            await drop.ExecuteNonQueryAsync(); await _admin.DisposeAsync();
        }
    }

    private ViagemObservadaRepository Repo(IOcorrenciaParadaRepository sequence) =>
        new(_redis, NullLogger<ViagemObservadaRepository>.Instance, sequence);
    private Task<ViagemObservadaResultado> Write(double p, int seconds = 0, Guid? itinerary = null) =>
        _repo.TentarAtualizarAsync(_ordem, itinerary ?? _itinerary, _t0.AddSeconds(seconds), p, default);
    private async Task<string[]> Snapshot() => (await Db.HashGetAllAsync(Key))
        .Select(e => $"{e.Name}={e.Value}").OrderBy(e => e).ToArray();
    private async Task<OcorrenciaParada> Add(int order, double p, Guid? stop = null, Guid? itinerary = null, bool active = true)
    {
        var version = active ? itinerary ?? _itinerary : Guid.NewGuid();
        var occurrence = new OcorrenciaParada(Guid.NewGuid(), version,
            stop ?? Guid.NewGuid(), order, p);
        await using var insert = _source.CreateCommand("""
            INSERT INTO "OcorrenciasParadasPadroes" ("Id","PadraoVersaoId","ParadaId","Ordem","PosicaoTracado")
            VALUES (@id, @itinerary, @stop, @order, @p)
            """);
        insert.Parameters.AddWithValue("id", occurrence.Id); insert.Parameters.AddWithValue("itinerary", occurrence.ItinerarioId);
        insert.Parameters.AddWithValue("stop", occurrence.ParadaId); insert.Parameters.AddWithValue("order", order);
        insert.Parameters.AddWithValue("p", p); await insert.ExecuteNonQueryAsync(); return occurrence;
    }

    [Fact]
    public async Task SequenciaOperacional_IgnoraRelacaoInativa()
    {
        await Add(1,.2,active:false); var active=await Add(1,.4);
        var result=await _sequence.BuscarTransicaoAsync(_itinerary,.1,.5,Guid.Empty,0,true,default);
        Assert.Equal(active.Id,result.Proxima!.Id); Assert.Equal(1,result.Proxima.Ordem);
    }

    [Theory]
    [InlineData(.05, 0)] [InlineData(.60, 4)] [InlineData(.40, 3)] [InlineData(.90, 5)]
    public async Task Criacao_BaselineSemEventos(double p, int expectedOrder)
    {
        var stops = new List<OcorrenciaParada>();
        foreach (var fraction in new[] { .1, .2, .4, .55, .7 }) stops.Add(await Add(stops.Count + 1, fraction));
        var result = await Write(p);
        Assert.Equal(ViagemObservadaStatus.Created, result.Status);
        Assert.Empty(result.OcorrenciasUltrapassadas);
        Assert.Equal(expectedOrder, result.Estado!.UltimaParadaOrdem);
        Assert.Equal(expectedOrder == 0 ? Guid.Empty : stops[expectedOrder - 1].Id, result.Estado.UltimaParadaItinerarioId);
        Assert.Null(await Db.KeyTimeToLiveAsync(Key));
        var next = await Write(.95, 1);
        Assert.Equal(stops.Skip(expectedOrder), next.OcorrenciasUltrapassadas);
        Assert.Equal(result.Estado.ViagemId, next.Estado!.ViagemId);
    }

    [Theory]
    [InlineData(.35, 1)] [InlineData(.36, 1)] [InlineData(.44, 2)] [InlineData(.51, 3)]
    public async Task Cruzamentos_UmaOuMultiplasInclusiveIgualdade(double p, int count)
    {
        var stops = new[] { await Add(1, .35), await Add(2, .4), await Add(3, .5) };
        await Write(.3);
        var result = await Write(p, 1);
        Assert.Equal(stops.Take(count), result.OcorrenciasUltrapassadas);
        Assert.Equal(stops[count - 1].Id, result.Estado!.UltimaParadaItinerarioId);
        Assert.Equal(p, result.Estado.PosicaoNaRotaConfirmada);
        Assert.Empty((await Write(p, 2)).OcorrenciasUltrapassadas);
    }

    [Theory]
    [InlineData(.349, .351, .3495, .352)] [InlineData(.3, .36, .34, .37)]
    public async Task JitterERegressao_NaoDuplicam(double a, double b, double c, double d)
    {
        var stop = await Add(1, .35);
        await Write(a);
        Assert.Equal(stop, Assert.Single((await Write(b, 1)).OcorrenciasUltrapassadas));
        var regression = await Write(c, 2);
        Assert.Empty(regression.OcorrenciasUltrapassadas);
        Assert.Equal(c, regression.Estado!.PosicaoNaRotaConfirmada);
        Assert.Equal(stop.Id, regression.Estado.UltimaParadaItinerarioId);
        Assert.Empty((await Write(d, 3)).OcorrenciasUltrapassadas);
    }

    [Fact]
    public async Task ParadaFisicaRepetidaEPosicoesIguais_SaoOcorrenciasDistintas()
    {
        var stopId = Guid.NewGuid();
        var a = await Add(1, .35, stopId); var b = await Add(5, .4, stopId); var c = await Add(6, .4);
        await Write(.3);
        var result = await Write(.44, 1);
        Assert.Equal(new[] { a, b, c }, result.OcorrenciasUltrapassadas);
        Assert.Equal(6, result.Estado!.UltimaParadaOrdem);
        Assert.Null(result.ProximaOcorrenciaOperacional);
        Assert.Equal(ViagemObservadaStatus.Updated, (await Write(.45, 2)).Status);
        Assert.Empty((await Write(.46, 3)).OcorrenciasUltrapassadas);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("regression")] [InlineData("range")]
    [InlineData("nan")] [InlineData("infinity")] [InlineData("order")]
    public async Task SequenciaInvalida_MesmoForaDaJanela_NaoAlteraHash(string problem)
    {
        await Add(1, .2); await Write(.1); var before = await Snapshot();
        await Add(3, .8);
        await Add(problem == "duplicate" ? 3 : problem == "order" ? 0 : 4,
            problem switch { "regression" => .79, "range" => 1.1, "nan" => double.NaN,
                "infinity" => double.PositiveInfinity, _ => .9 });
        var result = await Write(.3, 1);
        Assert.Equal(ViagemObservadaStatus.InvalidSequence, result.Status);
        Assert.Empty(result.OcorrenciasUltrapassadas); Assert.Equal(before, await Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CursorIncompativelOuOutroItinerario_FailClosed(bool foreign)
    {
        var stop = await Add(1, .2); await Write(.3);
        if (foreign) stop = await Add(1, .2, itinerary: Guid.NewGuid());
        await Db.HashSetAsync(Key, "UltimaParadaItinerarioId", stop.Id.ToString("N"));
        await Db.HashSetAsync(Key, "UltimaParadaOrdem", foreign ? "1" : "2");
        var before = await Snapshot(); var result = await Write(.4, 1);
        Assert.Equal(foreign ? ViagemObservadaStatus.OccurrenceNotFromItinerary
            : ViagemObservadaStatus.InvalidSequence, result.Status);
        Assert.Equal(before, await Snapshot()); Assert.Empty(result.OcorrenciasUltrapassadas);
    }

    [Fact]
    public async Task Legado31_InicializaBaselineDaPosicaoPersistidaSemEventos()
    {
        var a = await Add(1, .2); var b = await Add(2, .5); await Add(3, .7);
        var initial = (await Write(.6)).Estado!;
        await Db.HashDeleteAsync(Key, ["UltimaParadaItinerarioId", "UltimaParadaOrdem"]);
        var migrated = await Write(.72, 1);
        Assert.Empty(migrated.OcorrenciasUltrapassadas);
        Assert.Equal(b.Id, migrated.Estado!.UltimaParadaItinerarioId);
        Assert.Equal(initial.ViagemId, migrated.Estado.ViagemId);
        Assert.Equal(initial.TimestampObservacaoInicial, migrated.Estado.TimestampObservacaoInicial);
        Assert.Equal(8, await Db.HashLengthAsync(Key));
    }

    [Theory]
    [InlineData("UltimaParadaItinerarioId")] [InlineData("UltimaParadaOrdem")]
    public async Task EstadoParcial_NaoMigraSilenciosamente(string missing)
    {
        await Write(.2); await Db.HashDeleteAsync(Key, missing); var before = await Snapshot();
        Assert.Equal(ViagemObservadaStatus.InvalidState, (await Write(.3, 1)).Status);
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task Restart_CursorPersistidoEvitaRepeticao()
    {
        var a = await Add(1, .35); var b = await Add(2, .4);
        await Write(.3); await Write(.36, 1);
        await using var another = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6380");
        var restarted = new ViagemObservadaRepository(another, NullLogger<ViagemObservadaRepository>.Instance, _sequence);
        var result = await restarted.TentarAtualizarAsync(_ordem, _itinerary, _t0.AddSeconds(2), .44, default);
        Assert.Equal(b, Assert.Single(result.OcorrenciasUltrapassadas));
    }

    [Fact]
    public async Task T10T11_MesmoSnapshot_SomenteVencedorEmite()
    {
        var a = await Add(1, .35); var b = await Add(2, .4); await Write(.3);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = Repo(new Decorator(_sequence, async (_, _) => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); }));
        var t10 = gated.TentarAtualizarAsync(_ordem, _itinerary, _t0.AddSeconds(10), .36, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var t11 = await Write(.44, 11); release.SetResult(); var stale = await t10;
        Assert.Equal(new[] { a, b }, t11.OcorrenciasUltrapassadas);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, stale.Status); Assert.Empty(stale.OcorrenciasUltrapassadas);
        var state = (await Write(.44, 11)).Estado!;
        Assert.Equal(.44, state.PosicaoNaRotaConfirmada); Assert.Equal(b.Id, state.UltimaParadaItinerarioId);
    }

    [Fact]
    public async Task ConflitoEntreSqlELua_ReleERecalculaJanela()
    {
        var a = await Add(1, .35); var b = await Add(2, .4); await Write(.3);
        var calls = 0;
        _repo = Repo(new Decorator(_sequence, async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var winner = await Repo(_sequence).TentarAtualizarAsync(_ordem, _itinerary, _t0.AddSeconds(1), .36, default);
                Assert.Equal(a, Assert.Single(winner.OcorrenciasUltrapassadas));
            }
        }));
        var result = await Write(.44, 2);
        Assert.Equal(2, calls); Assert.Equal(b, Assert.Single(result.OcorrenciasUltrapassadas));
        Assert.Equal(b.Id, result.Estado!.UltimaParadaItinerarioId);
    }

    [Fact]
    public async Task ConflitoPersistente_LimiteFinitoSemEventos()
    {
        await Add(1, .35); await Write(.3); var calls = 0;
        _repo = Repo(new Decorator(_sequence, async (_, _) =>
        {
            calls++;
            await Db.HashSetAsync(Key, "PosicaoNaRotaConfirmada", (.3 + calls * .001).ToString("R", CultureInfo.InvariantCulture));
        }));
        var result = await Write(.4, 1);
        Assert.Equal(ViagemObservadaStatus.Conflict, result.Status); Assert.Equal(3, calls);
        Assert.Empty(result.OcorrenciasUltrapassadas);
        Assert.Equal("0", (string)(await Db.HashGetAsync(Key, "UltimaParadaOrdem"))!);
        Assert.Equal(_t0.UtcTicks.ToString("D19"), (string)(await Db.HashGetAsync(Key, "TimestampUltimaAtualizacao"))!);
    }

    [Fact]
    public async Task ItinerarioAlteradoETimestampIgual_NaoConsultamSql()
    {
        await Write(.3); var before = await Snapshot();
        _repo = Repo(new Decorator(_sequence, (_, _) => throw new InvalidOperationException("SQL não deve ser chamado")));
        Assert.Equal(ViagemObservadaStatus.ItineraryChanged, (await Write(.4, 1, Guid.NewGuid())).Status);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, (await Write(.4)).Status);
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task FalhaSql_NaoAvancaPosicaoNemCursor()
    {
        await Write(.3); var before = await Snapshot();
        _repo = Repo(new Decorator(_sequence, (_, _) => throw new TimeoutException()));
        var result = await Write(.4, 1);
        Assert.Equal(ViagemObservadaStatus.InfrastructureFailure, result.Status);
        Assert.Empty(result.OcorrenciasUltrapassadas); Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task CorridaMesmoTimestamp_SomenteUmRetornaOcorrencias()
    {
        var stop = await Add(1, .35); await Write(.3);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        _repo = Repo(new Decorator(_sequence, async (_, _) =>
        {
            if (Interlocked.Increment(ref arrived) == 2) gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }));
        var results = await Task.WhenAll(Write(.36, 1), Write(.36, 1));
        Assert.Single(results, r => r.Status == ViagemObservadaStatus.Updated);
        Assert.Single(results, r => r.Status == ViagemObservadaStatus.RejectedOlderOrEqual);
        Assert.Equal(stop, Assert.Single(results.SelectMany(r => r.OcorrenciasUltrapassadas)));
        Assert.Equal(stop.Id.ToString("N"), (string)(await Db.HashGetAsync(Key, "UltimaParadaItinerarioId"))!);
    }

    [Theory]
    [InlineData("UltimaParadaOrdem", "-1")]
    [InlineData("UltimaParadaOrdem", "2147483648")]
    [InlineData("UltimaParadaOrdem", "1.5")]
    [InlineData("UltimaParadaOrdem", "bad")]
    [InlineData("UltimaParadaItinerarioId", "bad")]
    [InlineData("UltimaParadaItinerarioId", "00000000000000000000000000000000")]
    public async Task CursorRedisCorrompido_ZeroWrites(string field, string value)
    {
        await Add(1, .2); await Write(.3); await Db.HashSetAsync(Key, field, value);
        var before = await Snapshot(); var result = await Write(.4, 1);
        Assert.Equal(ViagemObservadaStatus.InvalidState, result.Status);
        Assert.Empty(result.OcorrenciasUltrapassadas); Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task RespostaEvalPerdida_CursorEPosicaoPersistemSemEmitirCandidatos()
    {
        var stop = await Add(1, .35); await Write(.3);
        var db = DispatchProxy.Create<IDatabase, ViagemObservadaRepositoryTests.TimeoutProxy>();
        ((ViagemObservadaRepositoryTests.TimeoutProxy)db).Target = Db;
        var redis = DispatchProxy.Create<IConnectionMultiplexer, ViagemObservadaRepositoryTests.TimeoutProxy>();
        ((ViagemObservadaRepositoryTests.TimeoutProxy)redis).Target = _redis;
        ((ViagemObservadaRepositoryTests.TimeoutProxy)redis).Database = db;
        var lost = await new ViagemObservadaRepository(redis, NullLogger<ViagemObservadaRepository>.Instance, _sequence)
            .TentarAtualizarAsync(_ordem, _itinerary, _t0.AddSeconds(1), .36, default);
        Assert.Equal(ViagemObservadaStatus.InfrastructureFailure, lost.Status);
        Assert.Empty(lost.OcorrenciasUltrapassadas);
        var retry = await Write(.36, 1);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, retry.Status);
        Assert.Equal(stop.Id, retry.Estado!.UltimaParadaItinerarioId);
        Assert.Equal(.36, retry.Estado.PosicaoNaRotaConfirmada);
        Assert.Empty((await Write(.37, 2)).OcorrenciasUltrapassadas);
    }

    [Fact]
    public async Task PlanoSql_FiltraItinerarioAntesDeValidarSequencia()
    {
        await using (var seed = _source.CreateCommand("""
            INSERT INTO "OcorrenciasParadasPadroes" ("Id","PadraoVersaoId","ParadaId","Ordem","PosicaoTracado")
            SELECT gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1, .5 FROM generate_series(1,5000);
            INSERT INTO "OcorrenciasParadasPadroes" ("Id","PadraoVersaoId","ParadaId","Ordem","PosicaoTracado")
            SELECT gen_random_uuid(), @itinerary, gen_random_uuid(), n, n/100.0 FROM generate_series(1,50) n;
            ANALYZE "OcorrenciasParadasPadroes";
            """))
        {
            seed.Parameters.AddWithValue("itinerary", _itinerary); await seed.ExecuteNonQueryAsync();
        }
        await using var explain = _source.CreateCommand("EXPLAIN (ANALYZE, BUFFERS) " + OcorrenciaParadaRepository.Sql);
        explain.Parameters.AddWithValue("versao", _itinerary);
        explain.Parameters.AddWithValue("anterior", .3); explain.Parameters.AddWithValue("atual", .36);
        explain.Parameters.AddWithValue("ultima_id", Guid.Empty); explain.Parameters.AddWithValue("ultima_ordem", 0);
        explain.Parameters.AddWithValue("baseline", true);
        explain.Parameters.AddWithValue("circular", false);
        await using var reader = await explain.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        output.WriteLine(string.Join(Environment.NewLine, lines));
        Assert.Contains(lines, line => line.Contains("Index Cond:") && line.Contains("PadraoVersaoId"));
    }

    private sealed class Decorator(IOcorrenciaParadaRepository inner,
        Func<double, TransicaoParadas, Task> after) : IOcorrenciaParadaRepository
    {
        public async Task<TransicaoParadas> BuscarTransicaoAsync(Guid id, double anterior, double atual,
            Guid ultimaId, int ultimaOrdem, bool baseline, CancellationToken ct)
        {
            var result = await inner.BuscarTransicaoAsync(id, anterior, atual, ultimaId, ultimaOrdem, baseline, ct);
            await after(atual, result); return result;
        }
    }

    [Fact]
    public async Task Circular_WrapIncrementaVoltaEOrdenaFimAntesDoInicio()
    {
        var versao = Guid.NewGuid();
        var fim = await Add(3, .9, itinerary: versao);
        var inicio = await Add(1, .1, itinerary: versao);
        var meio = await Add(2, .5, itinerary: versao);

        var result = await _sequence.BuscarTransicaoV2Async(versao, .8, .2,
            meio.Id, meio.Ordem, false, "CIRCULAR", 4, default);

        Assert.True(result.HouveWrap);
        Assert.Equal(5, result.Volta);
        Assert.Equal(inicio.Id, result.UltimaId);
        Assert.Equal(1, result.UltimaOrdem);
        Assert.Equal(new[] { fim.Id, inicio.Id }, result.Ultrapassadas.Select(x => x.Id));
        Assert.Equal(new[] { 4, 5 }, result.Ultrapassadas.Select(x => x.Volta));
        Assert.Equal(meio.Id, result.Proxima!.Id);
    }
}
