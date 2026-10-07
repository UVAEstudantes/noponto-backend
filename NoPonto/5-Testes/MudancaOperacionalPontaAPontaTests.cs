using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.API.Hubs;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

// Reutiliza a fixture migrada. NÃO incluir este filtro em rodadas sem serviços descartáveis.
public sealed class MudancaOperacionalPontaAPontaTests(ViagemOperacionalFixture db) : IClassFixture<ViagemOperacionalFixture>
{
    private sealed class FonteVazia : IStatusGpsSource, IGpsSourceResolver
    {
        public string Name=>"TEST";
        public IGpsSource GetPrimary(string modal)=>this;
        public IReadOnlyList<IGpsSource> GetShadows(string modal)=>[];
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<GpsObservation>>([]);
        public Task<GpsSourceReadResult> GetResultAsync(CancellationToken ct)=>Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Sucesso,[],TimeSpan.Zero));
    }
    private sealed class EtaLocal : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[]",System.Text.Encoding.UTF8,"application/json")});
    }
    // Observador transparente: nenhuma decisão, prova ou persistência é simulada.
    internal sealed class Observador(ViagemOperacionalRepository real) : IViagemObservadaRepository
    {
        public List<PosicaoVeiculoDto> Entradas {get;}=[];
        public List<ViagemObservadaResultado> Resultados {get;}=[];
        public Task<ContextoOperacional?> LerContextoAsync(string ordem,CancellationToken ct)=>real.LerContextoAsync(ordem,ct);
        public Task<ContextoOperacional?> LerDuravelParaRetryAsync(string ordem,CancellationToken ct)=>real.LerDuravelParaRetryAsync(ordem,ct);
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(string ordem,Guid versao,DateTimeOffset timestamp,double p,CancellationToken ct)=>real.TentarAtualizarAsync(ordem,versao,timestamp,p,ct);
        public async Task<ViagemObservadaResultado> TentarAtualizarAsync(PosicaoVeiculoDto gps,CancellationToken ct)
        { Entradas.Add(gps); var r=await real.TentarAtualizarAsync(gps,ct); Resultados.Add(r); return r; }
        public async Task<ViagemObservadaResultado> TentarAtualizarAsync(PosicaoVeiculoDto gps,ContextoOperacional? contexto,ResultadoProjecaoOperacional projecao,CancellationToken ct)
        { Entradas.Add(gps); var r=await real.TentarAtualizarAsync(gps,contexto,projecao,ct); Resultados.Add(r); return r; }
    }
    internal sealed class Harness : IAsyncDisposable
    {
        private readonly ViagemOperacionalFixture _db;
        private readonly ServiceProvider _services;
        private readonly RedisCache _cache;
        private readonly HttpClient _http;
        private readonly GpsPollingService _polling;
        private readonly GpsSppoSnapshotStore _store=new();
        private readonly GpsPollingOptions _options;
        private readonly HashSet<string> _linhas=[];
        private readonly string _retryPrefix="teste:e2e:retry:"+Guid.NewGuid().ToString("N")+":";
        internal string RetryPrefix=>_retryPrefix;
        private readonly RetryOperacionalGpsService _retry;
        public readonly string Ordem="E2E-"+Guid.NewGuid().ToString("N");
        public readonly string Stream="teste:e2e:"+Guid.NewGuid().ToString("N");
        // O validator produtivo usa UtcNow. Uma âncora relativa única evita datas históricas expiradas.
        private readonly DateTimeOffset _t=DateTimeOffset.UtcNow.AddMinutes(-2);
        public Observador Spy {get;}
        public ViagemOperacionalRepository Repository {get;}
        public Harness(ViagemOperacionalFixture db,bool enabled=true,bool batch=false,int checkpointSegundos=0,
            Func<Task>? afterStateWrite=null,Func<Task>? beforeProjection=null,string? ordem=null,string? retryPrefix=null)
        {
            if(ordem is not null) Ordem=ordem;
            if(retryPrefix is not null) _retryPrefix=retryPrefix;
            _db=db;
            _options=new(){MudancaOperacionalHabilitada=enabled,EnriquecerTodasLinhas=true,CheckpointViagemSegundos=checkpointSegundos};
            Repository=new(db.Redis,db.Source,Options.Create(_options),NullLogger<ViagemOperacionalRepository>.Instance)
                {StreamKey=Stream,AfterDurableStateWriteAsync=afterStateWrite,BeforeDurableProjectionAsync=beforeProjection};
            Spy=new(Repository);
            var services=new ServiceCollection(); services.AddLogging(); services.AddSignalR();
            _services=services.BuildServiceProvider();
            _cache=new RedisCache(Options.Create(new RedisCacheOptions{Configuration=Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION")??"localhost:6380"}));
            _http=new(new EtaLocal()){BaseAddress=new Uri("http://eta.test.invalid")};
            var matching=new GpsMatchingBatchOptions{Enabled=batch};
            var enriquecedor=new GpsEnriquecimentoService(new GpsPadraoRepository(db.Source,NullLogger<GpsPadraoRepository>.Instance),Options.Create(_options),Options.Create(matching),NullLogger<GpsEnriquecimentoService>.Instance);
            var retryOptions=Options.Create(new RetryOperacionalGpsOptions());
            _retry=new RetryOperacionalGpsService(new PendenciaOperacionalGpsRepository(db.Redis,retryOptions){Prefixo=_retryPrefix},
                new ViagemObservadaService(Spy,NullLogger<ViagemObservadaService>.Instance),enriquecedor,
                retryOptions,Options.Create(_options),NullLogger<RetryOperacionalGpsService>.Instance);
            _polling=new(_store,_cache,_services.GetRequiredService<IHubContext<GpsHub>>(),NullLogger<GpsPollingService>.Instance,
                null!,_services.GetRequiredService<IServiceScopeFactory>(),enriquecedor,
                new GpsEtaClient(_http,NullLogger<GpsEtaClient>.Instance),new FonteVazia(),
                new PosicaoVeiculoCacheRepository(db.Redis,new PosicaoVeiculoPayloadWriter(db.Redis),NullLogger<PosicaoVeiculoCacheRepository>.Instance),
                new ViagemObservadaService(Spy,NullLogger<ViagemObservadaService>.Instance),retryOperacional:_retry);
        }
        internal Task RecuperarPendencias()=>_retry.ExecutarCicloAsync(default);
        public async Task Ciclo(int segundos,double p,bool b=false)
        {
            await CicloPosicao(segundos,"VIAGEM3",-22.9,b?-43.19-.02*p:-43.21+.02*p,b?270:90);
        }
        public async Task CicloPosicao(int segundos,string codigo,double latitude,double longitude,double bearing)
        {
            _linhas.Add(codigo);
            var ts=_t.AddSeconds(segundos);
            var gps=new PosicaoVeiculoDto{Ordem=Ordem,CodigoLinha=codigo,Latitude=latitude,
                Longitude=longitude,Bearing=bearing,Velocidade=20,
                TimestampGps=ts,RecebidoEmUtc=ts,ModalFonte="ONIBUS",ProvedorFonte="SPPO_ZIRIX"};
            await _store.PublicarAsync(ts,ts,ts,ts,ts,[gps]);
            var method=typeof(GpsPollingService).GetMethod("ProcessarCicloAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var task=(Task<bool>)method.Invoke(_polling,[ts,_options,Stopwatch.GetTimestamp(),null,CancellationToken.None])!;
            Assert.True(await task.WaitAsync(TimeSpan.FromSeconds(30)),"Polling não concluiu o ciclo; não considerar validação ponta a ponta.");
            Assert.Null(_store.Ler());
        }
        public async Task<ViagemOperacionalState> Estado()=> (await Repository.LerContextoAsync(Ordem,default))!.Estado!;
        public async Task<EventoViagem[]> Materializar()
        {
            var worker=new ViagemOutboxWorker(_db.Source,new HistoricoEventoRepository(_db.Source),NullLogger<ViagemOutboxWorker>.Instance);
            var items=await worker.ClaimAsync(default);
            await worker.ProcessarLoteAsync(items,default);
            // Reentrega idêntica testa idempotência do materializador, sem novo claim de processados.
            var historico=new HistoricoEventoRepository(_db.Source);
            await using var c=_db.Source.CreateCommand("SELECT \"Payload\"::text FROM \"EventosViagem\" WHERE \"Payload\"->>'ordem_veiculo'=@ordem ORDER BY \"TimestampEvento\",\"EventId\"");
            c.Parameters.AddWithValue("ordem",Ordem);
            var events=new List<EventoViagem>();
            await using(var reader=await c.ExecuteReaderAsync())
                while(await reader.ReadAsync()) events.Add(JsonSerializer.Deserialize<EventoViagem>(reader.GetString(0))!);
            foreach(var e in events) await historico.PersistirAsync(e,default);
            await using var count=_db.Source.CreateCommand("SELECT count(*) FROM \"EventosViagem\" WHERE \"Payload\"->>'ordem_veiculo'=@ordem");
            count.Parameters.AddWithValue("ordem",Ordem);
            Assert.Equal((long)events.Count,await count.ExecuteScalarAsync());
            Assert.Empty(await worker.ClaimAsync(default));
            return events.ToArray();
        }
        public async Task<long> Passagens()
        {
            await using var c=_db.Source.CreateCommand("SELECT count(*) FROM \"HistoricoPassagens\" WHERE \"Ordem\"=@ordem");
            c.Parameters.AddWithValue("ordem",Ordem); return (long)(await c.ExecuteScalarAsync())!;
        }
        public async ValueTask DisposeAsync()
        {
            _polling.Dispose(); _http.Dispose(); _cache.Dispose(); await _services.DisposeAsync();
            await _db.Redis.GetDatabase().KeyDeleteAsync([GpsPollingService.ChaveVeiculoAtivo(Ordem),GpsPollingService.ChaveVeiculoRecente(Ordem),PosicaoVeiculoCacheRepository.ChaveVeiculoTimestamp(Ordem),PosicaoVeiculoCacheRepository.ChaveVeiculoLock(Ordem),ViagemObservadaRepository.ChaveVeiculoViagem(Ordem),Stream]);
            // Linha compartilhada por esta classe; fixture e Redis devem ser exclusivamente de testes.
            await _db.Redis.GetDatabase().KeyDeleteAsync(GpsPollingService.ChaveLinha("VIAGEM3"));
            foreach(var codigo in _linhas) await _db.Redis.GetDatabase().KeyDeleteAsync(GpsPollingService.ChaveLinha(codigo));
            await _db.Redis.GetDatabase().KeyDeleteAsync([_retryPrefix+"dados",_retryPrefix+"prazos",
                _retryPrefix+"veiculo:"+Ordem,_retryPrefix+"lease:"+Ordem]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodigosDistintosSobrepostos_ABB_ConfirmaNovaExecucaoSemPassagem(bool batch)
    {
        var a = await db.CriarCircularAsync(); var b = await db.CriarCircularAsync();
        Assert.NotEqual(a.CodigoLinha, b.CodigoLinha);
        await using var h = new Harness(db, batch: batch);
        async Task Observar(int segundos, EstruturaViagem e, double p)
        {
            var ponto = await db.PontoCircularAsync(e.PadraoVersaoId, p);
            await h.CicloPosicao(segundos, e.CodigoLinha, ponto.Latitude, ponto.Longitude, ponto.Bearing);
            Assert.Equal(e.PadraoVersaoId, h.Spy.Entradas[^1].PadraoVersaoId);
        }
        await Observar(0, a, .25); await Observar(10, a, .26);
        var antiga = await h.Estado();
        await Observar(20, b, .27);
        Assert.True(h.Spy.Entradas[^1].MatchingOperacionalPlausivel);
        var candidata = await h.Estado();
        Assert.Equal(antiga.Observada.ViagemId, candidata.Observada.ViagemId);
        Assert.Equal(b.LinhaId, candidata.Candidato!.LinhaId);
        await Observar(30, b, .28);
        var nova = await h.Estado();
        Assert.NotEqual(antiga.Observada.ViagemId, nova.Observada.ViagemId);
        Assert.Equal(b.LinhaId, nova.LinhaId); Assert.Null(nova.Candidato);
        Assert.Equal(0, nova.Observada.Volta);
        var eventos = await h.Materializar();
        Assert.Single(eventos, e => e.Tipo == "ViagemFinalizada" && e.ViagemId == antiga.Observada.ViagemId);
        Assert.Single(eventos, e => e.Tipo == "ViagemIniciada" && e.ViagemId == nova.Observada.ViagemId);
        Assert.DoesNotContain(eventos, e => e.Tipo == "PassagemParada");
        Assert.Equal(0L, await h.Passagens());
    }

    [Fact]
    public async Task FalhaDuravelDepoisGpsAceito_RepeticaoMesmoTimestampRecuperaCandidato()
    {
        var a = await db.CriarCircularAsync(); var b = await db.CriarCircularAsync();
        var falhar = false;
        await using var h = new Harness(db, afterStateWrite: () => falhar
            ? throw new InvalidOperationException("Falha local de reprodução") : Task.CompletedTask);
        async Task Observar(int segundos, EstruturaViagem e, double p)
        {
            var ponto = await db.PontoCircularAsync(e.PadraoVersaoId, p);
            await h.CicloPosicao(segundos, e.CodigoLinha, ponto.Latitude, ponto.Longitude, ponto.Bearing);
        }
        await Observar(0, a, .25); await Observar(10, a, .26);
        var anterior = await h.Estado();
        try
        {
            falhar = true;
            await Observar(20, b, .27);
            Assert.Equal(ViagemObservadaStatus.InfrastructureFailure, h.Spy.Resultados[^1].Status);
        }
        finally { falhar = false; }
        Assert.Equal(anterior, await h.Estado());
        var chamadas = h.Spy.Entradas.Count;
        await Observar(20, b, .27);
        Assert.Equal(chamadas + 1, h.Spy.Entradas.Count);
        var recuperada = await h.Estado();
        Assert.Equal(anterior.Observada.ViagemId, recuperada.Observada.ViagemId);
        Assert.Equal(b.LinhaId, recuperada.Candidato!.LinhaId);
        await Observar(30, b, .28);
        var confirmada = await h.Estado();
        Assert.NotEqual(anterior.Observada.ViagemId, confirmada.Observada.ViagemId);
        Assert.Null(confirmada.Candidato);
        var eventos = await h.Materializar();
        Assert.Single(eventos, e => e.Tipo == "ViagemFinalizada");
        Assert.DoesNotContain(eventos, e => e.Tipo == "PassagemParada");
    }

    // Recuperação leve com prova recalculada; executar somente em serviços descartáveis.
    [Fact]
    public async Task RecuperacaoPendente_MesmoGpsDepoisRollback_DeveRecuperarCandidatoEConfirmar()
    {
        var a = await db.CriarCircularAsync(); var b = await db.CriarCircularAsync();
        var falhar = false;
        await using var h = new Harness(db, afterStateWrite: () => falhar
            ? throw new InvalidOperationException("Falha de reprodução 2A.2O") : Task.CompletedTask);
        async Task Observar(int segundos, EstruturaViagem e, double p)
        {
            var ponto = await db.PontoCircularAsync(e.PadraoVersaoId, p);
            await h.CicloPosicao(segundos, e.CodigoLinha, ponto.Latitude, ponto.Longitude, ponto.Bearing);
        }
        await Observar(0, a, .25); await Observar(10, a, .26);
        var anterior = await h.Estado();
        try
        {
            falhar = true; await Observar(20, b, .27);
            Assert.Equal(ViagemObservadaStatus.InfrastructureFailure, h.Spy.Resultados[^1].Status);
        }
        finally { falhar = false; }
        Assert.Equal(anterior, await h.Estado());
        var chamadas = h.Spy.Entradas.Count;
        await Observar(20, b, .27);
        Assert.Equal(chamadas + 1, h.Spy.Entradas.Count);
        var candidato = await h.Estado();
        Assert.Equal(anterior.Observada.ViagemId, candidato.Observada.ViagemId);
        Assert.Equal(b.LinhaId, candidato.Candidato!.LinhaId);
        Assert.Equal(h.Spy.Entradas[^1].TimestampGps, candidato.Candidato.Timestamp);
        await Observar(30, b, .28);
        Assert.NotEqual(anterior.Observada.ViagemId, (await h.Estado()).Observada.ViagemId);
        var eventos = await h.Materializar();
        Assert.Single(eventos, e => e.Tipo == "ViagemFinalizada");
        Assert.DoesNotContain(eventos, e => e.Tipo == "PassagemParada");
        Assert.Equal(0L, await h.Passagens());
    }

    [Fact]
    public async Task CodigosDistintosSobrepostos_ABCBA_NaoAcumulaEvidenciasEntreCandidatos()
    {
        var a = await db.CriarCircularAsync(); var b = await db.CriarCircularAsync();
        var c = await db.CriarCircularAsync();
        await using var h = new Harness(db);
        async Task Observar(int segundos, EstruturaViagem e, double p)
        {
            var ponto = await db.PontoCircularAsync(e.PadraoVersaoId, p);
            await h.CicloPosicao(segundos, e.CodigoLinha, ponto.Latitude, ponto.Longitude, ponto.Bearing);
            Assert.Equal(e.PadraoVersaoId, h.Spy.Entradas[^1].PadraoVersaoId);
        }
        await Observar(0, a, .25); await Observar(10, a, .26);
        var antiga = await h.Estado();
        await Observar(20, b, .27); Assert.Equal(b.LinhaId, (await h.Estado()).Candidato!.LinhaId);
        await Observar(30, c, .28); Assert.Equal(c.LinhaId, (await h.Estado()).Candidato!.LinhaId);
        await Observar(40, b, .29); Assert.Equal(b.LinhaId, (await h.Estado()).Candidato!.LinhaId);
        Assert.Equal(antiga.Observada.ViagemId, (await h.Estado()).Observada.ViagemId);
        await Observar(50, a, .30);
        var final = await h.Estado();
        Assert.Null(final.Candidato); Assert.Equal(antiga.Observada.ViagemId, final.Observada.ViagemId);
        Assert.True(ViagemOperacionalRegra.IdentidadeConfiavel(final));
        Assert.DoesNotContain(await h.Materializar(), e => e.Tipo == "ViagemFinalizada" || e.Tipo == "PassagemParada");
        Assert.Equal(0L, await h.Passagens());
    }

    [Fact]
    public async Task Retry_RespostaPerdidaDepoisCommit_ReconheceDuravelSemDuplicarEventos()
    {
        var a=await db.CriarCircularAsync(); var b=await db.CriarCircularAsync(); var falhar=false;
        await using var h=new Harness(db,beforeProjection:()=>falhar?throw new InvalidOperationException("Resposta perdida"):Task.CompletedTask);
        async Task Observar(int t,EstruturaViagem e,double p)
        {var ponto=await db.PontoCircularAsync(e.PadraoVersaoId,p);await h.CicloPosicao(t,e.CodigoLinha,ponto.Latitude,ponto.Longitude,ponto.Bearing);}
        await Observar(0,a,.25); await Observar(10,a,.26); var anterior=await h.Estado();
        try {falhar=true;await Observar(20,b,.27);Assert.Equal(ViagemObservadaStatus.InfrastructureFailure,h.Spy.Resultados[^1].Status);}
        finally {falhar=false;}
        var commit=(await h.Repository.LerDuravelParaRetryAsync(h.Ordem,default))!;
        Assert.NotNull(commit.Estado!.Candidato);
        var chamadas=h.Spy.Entradas.Count;
        await Observar(20,b,.27);
        Assert.Equal(chamadas,h.Spy.Entradas.Count); // commit já ocorreu: não executar novamente
        Assert.Equal(commit.VersaoDuravel,(await h.Repository.LerDuravelParaRetryAsync(h.Ordem,default))!.VersaoDuravel);
        await Observar(30,b,.28); var nova=await h.Estado();
        Assert.NotEqual(anterior.Observada.ViagemId,nova.Observada.ViagemId);
        var eventos=await h.Materializar(); Assert.Single(eventos,e=>e.Tipo=="ViagemFinalizada");
        Assert.Single(eventos,e=>e.Tipo=="ViagemIniciada"&&e.ViagemId==nova.Observada.ViagemId);
        Assert.Equal(0L,await h.Passagens());
    }

    [Fact]
    public async Task Retry_NovaInstanciaComRedisPreservado_RecuperaSemReplayDaFonte()
    {
        var a=await db.CriarCircularAsync(); var b=await db.CriarCircularAsync(); var falhar=false;
        await using var h=new Harness(db,afterStateWrite:()=>falhar?throw new InvalidOperationException("Falha"):Task.CompletedTask);
        async Task Observar(int t,EstruturaViagem e,double p)
        {var ponto=await db.PontoCircularAsync(e.PadraoVersaoId,p);await h.CicloPosicao(t,e.CodigoLinha,ponto.Latitude,ponto.Longitude,ponto.Bearing);}
        await Observar(0,a,.25); await Observar(10,a,.26); var anterior=await h.Estado();
        try {falhar=true;await Observar(20,b,.27);} finally {falhar=false;}
        Assert.Equal(ViagemObservadaStatus.InfrastructureFailure,h.Spy.Resultados[^1].Status);
        var timestamp=h.Spy.Entradas[^1].TimestampGps;
        // Nenhum novo snapshot/fonte: nova instância, outra memória de matching, mesmo Redis.
        await using var reiniciado=new Harness(db,ordem:h.Ordem,retryPrefix:h.RetryPrefix);
        await reiniciado.RecuperarPendencias();
        var gps=Assert.Single(reiniciado.Spy.Entradas);
        Assert.Equal(timestamp,gps.TimestampGps); Assert.True(gps.MatchingOperacionalPlausivel);
        Assert.True(gps.ExigirPersistenciaDuravel);
        var recuperada=await reiniciado.Estado();
        Assert.Equal(anterior.Observada.ViagemId,recuperada.Observada.ViagemId);
        Assert.Equal(b.LinhaId,recuperada.Candidato!.LinhaId);
        Assert.Equal(0L,await reiniciado.Passagens());
    }

    [Fact]
    public async Task Retry_CircularAposRollback_PreservaProtecaoESuprimeIdentidadeMl()
    {
        var a=await db.CriarCircularAsync(); var falhar=false;
        await using var h=new Harness(db,afterStateWrite:()=>falhar?throw new InvalidOperationException("Falha circular"):Task.CompletedTask);
        async Task Observar(int t,double p,string? codigo=null)
        {var ponto=await db.PontoCircularAsync(a.PadraoVersaoId,p);await h.CicloPosicao(t,codigo??a.CodigoLinha,ponto.Latitude,ponto.Longitude,ponto.Bearing);}
        await Observar(0,.90); await Observar(10,.95); await Observar(20,.97,"VIAGEM3");
        var anterior=await h.Estado(); Assert.NotNull(anterior.Candidato);
        try {falhar=true;await Observar(80,.02);} finally {falhar=false;}
        Assert.Equal(ViagemObservadaStatus.InfrastructureFailure,h.Spy.Resultados[^1].Status);
        Assert.Equal(anterior,await h.Estado());
        await db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(h.Ordem));
        await h.RecuperarPendencias();
        var protegida=await h.Estado(); Assert.False(ViagemOperacionalRegra.IdentidadeConfiavel(protegida));
        Assert.Equal(anterior.Observada.ViagemId,protegida.Observada.ViagemId);
        Assert.Equal(anterior.Observada.Volta,protegida.Observada.Volta);
        Assert.Equal(anterior.Observada.UltimaOcorrenciaParadaPadraoId,protegida.Observada.UltimaOcorrenciaParadaPadraoId);
        var telemetry=EventoTelemetriaMlFactory.Criar(h.Spy.Entradas[^1],h.Spy.Resultados[^1],DateTimeOffset.UtcNow);
        Assert.Null(telemetry.ViagemId); Assert.Null(telemetry.Volta);
        Assert.DoesNotContain(await h.Materializar(),e=>e.Tipo=="PassagemParada"||e.Tipo=="ViagemFinalizada");
        Assert.Equal(0L,await h.Passagens());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABB_MatchingReal_ConfirmaEMaterializaSemPassagens(bool batch)
    {
        await using var h=new Harness(db,batch:batch);
        await h.Ciclo(0,.25); await h.Ciclo(10,.30);
        Assert.False(h.Spy.Entradas[0].MatchingOperacionalPlausivel);
        var old=await h.Estado();
        await h.Ciclo(20,.75,true);
        Assert.True(h.Spy.Entradas[^1].MatchingOperacionalPlausivel);
        var candidato=await h.Estado(); Assert.NotNull(candidato.Candidato);
        Assert.Equal(old.Observada.ViagemId,candidato.Observada.ViagemId);
        await h.Ciclo(30,.76,true);
        Assert.True(h.Spy.Entradas[^1].MatchingOperacionalPlausivel);
        var nova=await h.Estado();
        Assert.NotEqual(old.Observada.ViagemId,nova.Observada.ViagemId);
        Assert.Equal(EstadoViagem.Ativa,nova.Estado); Assert.Equal(db.R2,nova.Observada.PadraoVersaoId);
        Assert.Equal(db.S2,nova.SentidoId); Assert.Equal(0,nova.Observada.Volta);
        Assert.Equal(db.Linha,nova.LinhaId); Assert.Equal(db.P2,nova.Observada.PadraoOperacionalId);
        Assert.Null(nova.Candidato); Assert.True(nova.Observada.PosicaoNaRotaConfirmada>.75);
        Assert.NotEqual(old.Observada.OcorrenciaCursorId,nova.Observada.OcorrenciaCursorId);
        Assert.Equal(1,nova.Observada.OrdemCursor);
        var eventos=await h.Materializar();
        Assert.Equal(3,eventos.Length);
        var fim=Assert.Single(eventos,e=>e.Tipo=="ViagemFinalizada");
        Assert.Equal(old.Observada.ViagemId,fim.ViagemId); Assert.Null(fim.OcorrenciaParadaPadraoId);
        var inicio=Assert.Single(eventos,e=>e.Tipo=="ViagemIniciada"&&e.ViagemId==nova.Observada.ViagemId);
        Assert.Equal(fim.TimestampEvento,inicio.TimestampEvento);
        Assert.Equal(h.Spy.Entradas[^1].TimestampGps,inicio.TimestampEvento);
        Assert.Equal(0,await h.Passagens());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABA_BaselineCoerente_SomentePassagemFutura(bool batch)
    {
        await using var h=new Harness(db,batch:batch);
        await h.Ciclo(0,.25); await h.Ciclo(10,.30); var old=await h.Estado();
        await h.Ciclo(20,.75,true); Assert.NotNull((await h.Estado()).Candidato);
        await h.Ciclo(40,.55);
        var s=await h.Estado(); Assert.Null(s.Candidato);
        Assert.Equal(old.Observada.ViagemId,s.Observada.ViagemId);
        Assert.Equal(db.Occurrences[1],s.Observada.OcorrenciaCursorId); Assert.Equal(2,s.Observada.OrdemCursor);
        Assert.InRange(s.Observada.PosicaoNaRotaConfirmada,.549,.551);
        Assert.Equal(db.Occurrences[2],h.Spy.Resultados[^1].ProximaOcorrenciaOperacional!.Id);
        Assert.True(h.Spy.Resultados[^1].ProximaOcorrenciaOperacional!.PosicaoLinha>s.Observada.PosicaoNaRotaConfirmada);
        Assert.Empty(h.Spy.Resultados[^1].OcorrenciasUltrapassadas);
        var calls=h.Spy.Entradas.Count; await h.Ciclo(40,.55); await h.Ciclo(39,.54);
        Assert.Equal(calls,h.Spy.Entradas.Count);
        await db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(h.Ordem));
        Assert.Equal(s,await h.Estado());
        await h.Ciclo(50,.61);
        var eventos=await h.Materializar();
        Assert.Equal(2,eventos.Length); Assert.DoesNotContain(eventos,e=>e.Tipo=="ViagemFinalizada");
        var passagem=Assert.Single(eventos,e=>e.Tipo=="PassagemParada");
        Assert.Equal(db.Occurrences[2],passagem.OcorrenciaParadaPadraoId);
        Assert.Equal(old.Observada.ViagemId,passagem.ViagemId);
        Assert.True(passagem.TimestampPassagem>s.Observada.TimestampUltimaAtualizacao);
        Assert.Equal(1,await h.Passagens());
    }

    [Fact]
    public async Task FlagDesligada_ProvaRealNaoCriaCandidato()
    {
        await using var h=new Harness(db,enabled:false);
        await h.Ciclo(0,.25); await h.Ciclo(10,.30); var old=await h.Estado();
        await h.Ciclo(20,.75,true); await h.Ciclo(30,.76,true);
        Assert.True(h.Spy.Entradas[^1].MatchingOperacionalPlausivel);
        var s=await h.Estado(); Assert.Null(s.Candidato); Assert.Equal(old.Observada.ViagemId,s.Observada.ViagemId);
        Assert.Single(await h.Materializar()); Assert.Equal(0,await h.Passagens());
    }
}
