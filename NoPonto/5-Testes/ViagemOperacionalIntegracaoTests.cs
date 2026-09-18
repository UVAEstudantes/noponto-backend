using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;
using System.Reflection;
using Npgsql;
using NoPonto.Domain.Entities;

namespace NoPonto.Tests;

public sealed class ViagemOperacionalIntegracaoTests(ViagemOperacionalFixture db) : IClassFixture<ViagemOperacionalFixture>, IAsyncLifetime
{
    private readonly string _ordem="TESTE-V3-"+Guid.NewGuid().ToString("N");
    private readonly string _stream="teste:viagem3:"+Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _t=DateTimeOffset.UtcNow.AddMinutes(-5);
    private IDatabase Redis=>db.Redis.GetDatabase();
    private string Key=>ViagemObservadaRepository.ChaveVeiculoViagem(_ordem);
    private ViagemOperacionalRepository Repository()=>new(db.Redis,db.Source,Options.Create(new GpsPollingOptions()),NullLogger<ViagemOperacionalRepository>.Instance){StreamKey=_stream};
    private HistoricoPassagemWorker Worker(IHistoricoEventoRepository? repository=null)=>new(db.Redis,repository??new HistoricoEventoRepository(db.Source),NullLogger<HistoricoPassagemWorker>.Instance){StreamKey=_stream,DeadLetterKey=_stream+":dlq"};
    private PosicaoVeiculoDto G(int seconds,double p=.1,bool novo=false)=>new(){Ordem=_ordem,CodigoLinha="VIAGEM3",ItinerarioId=novo?db.R2:db.R1,
        PosicaoNaRota=p,ComprimentoRotaMetros=2220,TimestampGps=_t.AddSeconds(seconds),Latitude=-22.9,
        Longitude=novo?-43.19-.02*p:-43.21+.02*p,Bearing=novo?270:90,Velocidade=20};
    public Task InitializeAsync()=>Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach(var entry in await Redis.StreamRangeAsync(_stream)) await Redis.KeyDeleteAsync([HistoricoPassagemWorker.Tentativas(entry.Id),HistoricoPassagemWorker.UltimoErro(entry.Id)]);
        await Redis.KeyDeleteAsync([Key,_stream,_stream+":dlq"]);
    }
    private async Task<ViagemOperacionalState> State()=>ViagemOperacionalCodec.Decode((await Redis.HashGetAllAsync(Key)).ToDictionary(x=>x.Name.ToString(),x=>x.Value.ToString()),_ordem);
    private async Task Terminal()
    {
        Assert.Equal(ViagemObservadaStatus.Created,(await Repository().TentarAtualizarAsync(G(0),default)).Status);
        Assert.Equal(ViagemObservadaStatus.Updated,(await Repository().TentarAtualizarAsync(G(100,.65),default)).Status);
    }

    [Fact]
    public async Task RedisTerminal_ReinicioT0T1_Finalizada_TresPassagensMaisInicioEFim()
    {
        await Terminal();
        Assert.Equal(EstadoViagem.PossivelFim,(await State()).Estado);
        await Repository().TentarAtualizarAsync(G(110,.66),default);
        Assert.Equal(1,(await State()).ConfirmacoesPosTerminal);
        await Repository().TentarAtualizarAsync(G(120,.67),default);
        Assert.Equal(EstadoViagem.Finalizada,(await State()).Estado);
        Assert.Equal(G(120).TimestampGps,(await State()).TimestampFim);
        var entries=await Redis.StreamRangeAsync(_stream);
        Assert.Equal(5,entries.Length);
        var events=entries.Select(e=>EventoViagemValidator.Parse(e.Values.ToDictionary(v=>v.Name.ToString(),v=>v.Value.ToString()))).ToArray();
        Assert.Equal(new[]{1,2,3},events.Where(e=>e.Tipo=="PassagemParada").Select(e=>e.Ordem!.Value));
        var worker=Worker(); await worker.GarantirGrupoAsync();
        foreach(var entry in await Redis.StreamReadGroupAsync(_stream,HistoricoPassagemWorker.Group,worker.Consumer,">",100)) await worker.ProcessarAsync(entry,default);
        Assert.Equal(0,(await Redis.StreamPendingAsync(_stream,HistoricoPassagemWorker.Group)).PendingMessageCount);
        using var scope=db.Provider.CreateScope();
        var context=scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var history=await context.HistoricoPassagens.Where(h=>h.Ordem==_ordem).OrderBy(h=>h.TimestampPassagem).ToArrayAsync();
        Assert.Equal(3,history.Length);
        Assert.All(history,h=>{Assert.NotNull(h.ViagemId);Assert.NotNull(h.ParadaItinerarioId);Assert.NotNull(h.SentidoId);Assert.NotNull(h.TimestampPassagem);Assert.Null(h.DistanciaParadaMetros);});
    }

    [Fact]
    public async Task R2_NaoFinalizaPrematuramente_PreservaTerminalNoRedis()
    {
        await Terminal(); var old=(await State()).Observada.ViagemId;
        await Repository().TentarAtualizarAsync(G(110,.66),default);
        Assert.Equal(1,(await State()).ConfirmacoesPosTerminal);
        Assert.Equal(ViagemObservadaStatus.Updated,(await Repository().TentarAtualizarAsync(G(120,.1,true),default)).Status);
        var first=await State();Assert.Equal(EstadoViagem.PossivelFim,first.Estado);Assert.Equal(old,first.Observada.ViagemId);Assert.Null(first.Candidato);
        Assert.Equal(1,first.ConfirmacoesPosTerminal);
        Assert.Equal(4,await Redis.StreamLengthAsync(_stream));
        await Repository().TentarAtualizarAsync(G(130,.67),default);
        Assert.Equal(EstadoViagem.Finalizada,(await State()).Estado);
        Assert.Equal(5,await Redis.StreamLengthAsync(_stream));
        await Repository().TentarAtualizarAsync(G(140,.1,true),default);
        Assert.NotNull((await State()).Candidato);
        await Repository().TentarAtualizarAsync(G(150,.2,true),default);
        var second=await State();Assert.Equal(EstadoViagem.Ativa,second.Estado);Assert.NotEqual(old,second.Observada.ViagemId);Assert.Equal(db.R2,second.Observada.ItinerarioId);
        Assert.Equal(6,await Redis.StreamLengthAsync(_stream));
    }

    private async Task<int> CommitState(HashEntry[] snapshot, ViagemOperacionalState next)
    {
        var pairs=snapshot.SelectMany(e=>new[]{e.Name.ToString(),e.Value.ToString()}).ToArray();
        return (int)await Redis.ScriptEvaluateAsync(ViagemOperacionalRedisScript.Commit,[Key,_stream],
            [System.Text.Json.JsonSerializer.Serialize(pairs),System.Text.Json.JsonSerializer.Serialize(ViagemOperacionalCodec.Encode(next)),
                "[]",ViagemOperacionalCodec.Tick(DateTimeOffset.UtcNow)]);
    }

    [Fact]
    public async Task Lua_PossivelFim_NaoPermiteReducaoNemRetornoParaAtiva()
    {
        await Terminal();await Repository().TentarAtualizarAsync(G(110,.66),default);
        var snapshot=await Redis.HashGetAllAsync(Key);var s=await State();
        var proposed=s with { Observada=s.Observada with { TimestampUltimaAtualizacao=G(120).TimestampGps },ConfirmacoesPosTerminal=0 };
        Assert.Equal(5,await CommitState(snapshot,proposed));
        Assert.Equal(1,(await State()).ConfirmacoesPosTerminal);
        Assert.Equal(5,await CommitState(snapshot,proposed with { Estado=EstadoViagem.Ativa }));
        Assert.Equal(EstadoViagem.PossivelFim,(await State()).Estado);
        Assert.Equal(1,(await State()).ConfirmacoesPosTerminal);
        Assert.Equal(4,await Redis.StreamLengthAsync(_stream));
    }

    [Fact]
    public async Task Lua_FinalizadaNaoPodeRegredirParaAtiva()
    {
        await Terminal();await Repository().TentarAtualizarAsync(G(110,.66),default);
        await Repository().TentarAtualizarAsync(G(120,.67),default);
        var snapshot=await Redis.HashGetAllAsync(Key);var s=await State();
        Assert.Equal(5,await CommitState(snapshot,s with { Estado=EstadoViagem.Ativa,ConfirmacoesPosTerminal=0,
            TimestampFim=null,Observada=s.Observada with { TimestampUltimaAtualizacao=G(130).TimestampGps } }));
        Assert.Equal(EstadoViagem.Finalizada,(await State()).Estado);
        Assert.Equal(5,await Redis.StreamLengthAsync(_stream));
    }

    [Fact]
    public async Task BaselineNoTerminal_NaoEmiteRetroativos()
    {
        await Repository().TentarAtualizarAsync(G(0,.7),default);
        Assert.Equal(3,(await State()).Observada.UltimaParadaOrdem);
        Assert.Equal(EstadoViagem.Ativa,(await State()).Estado);
        Assert.Single(await Redis.StreamRangeAsync(_stream));
    }

    [Fact]
    public async Task WrongTypeStream_ZeroWritesNoHash()
    {
        await Redis.StringSetAsync(_stream,"INCOMPATIVEL");
        Assert.Equal(ViagemObservadaStatus.InvalidState,(await Repository().TentarAtualizarAsync(G(0),default)).Status);
        Assert.False(await Redis.KeyExistsAsync(Key));
        await Redis.KeyDeleteAsync(_stream);
    }

    [Fact]
    public async Task HashParcial_NaoSobrescreve()
    {
        await Redis.HashSetAsync(Key,"EstadoViagem","Ativa");
        Assert.Equal(ViagemObservadaStatus.InvalidState,(await Repository().TentarAtualizarAsync(G(0),default)).Status);
        Assert.Equal(1,await Redis.HashLengthAsync(Key));Assert.False(await Redis.KeyExistsAsync(_stream));
    }

    [Fact]
    public async Task ReentregaAposDbAntesAck_Idempotente_XAutoClaim_ReinicioWorker()
    {
        await Terminal();var worker=Worker();await worker.GarantirGrupoAsync();await worker.GarantirGrupoAsync();
        var entries=await Redis.StreamReadGroupAsync(_stream,HistoricoPassagemWorker.Group,"instancia-morta",">",100);
        var repository=new HistoricoEventoRepository(db.Source);
        foreach(var entry in entries) await repository.PersistirAsync(EventoViagemValidator.Parse(entry.Values.ToDictionary(v=>v.Name.ToString(),v=>v.Value.ToString())),default);
        Assert.Equal(4,(await Redis.StreamPendingAsync(_stream,HistoricoPassagemWorker.Group)).PendingMessageCount);
        foreach(var entry in entries) await Redis.ExecuteAsync("XCLAIM",_stream,HistoricoPassagemWorker.Group,"instancia-morta",0,entry.Id,"IDLE",60001,"JUSTID");
        await Worker().RecuperarPendentesAsync(default);
        Assert.Equal(0,(await Redis.StreamPendingAsync(_stream,HistoricoPassagemWorker.Group)).PendingMessageCount);
        using var scope=db.Provider.CreateScope();
        Assert.Equal(3,await scope.ServiceProvider.GetRequiredService<TransporteDbContext>().HistoricoPassagens.CountAsync(h=>h.Ordem==_ordem));
    }

    private sealed class Falha : IHistoricoEventoRepository
    {
        public Task PersistirAsync(EventoViagem e,CancellationToken ct)=>throw new TimeoutException("PG indisponível simulado");
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CincoFalhas_DeadLetterPreservaPayload_AckELimpaContador(bool permanente)
    {
        if(permanente) await Redis.StreamAddAsync(_stream,[new("tipo","INVALIDO")]);
        else await Repository().TentarAtualizarAsync(G(0),default);
        var worker=Worker(new Falha());await worker.GarantirGrupoAsync();
        var entry=Assert.Single(await Redis.StreamReadGroupAsync(_stream,HistoricoPassagemWorker.Group,worker.Consumer,">",100));
        for(var i=0;i<4;i++) await worker.ProcessarAsync(entry,default);
        Assert.Equal(1,(await Redis.StreamPendingAsync(_stream,HistoricoPassagemWorker.Group)).PendingMessageCount);
        await worker.ProcessarAsync(entry,default);
        Assert.Equal(0,(await Redis.StreamPendingAsync(_stream,HistoricoPassagemWorker.Group)).PendingMessageCount);
        Assert.Single(await Redis.StreamRangeAsync(_stream+":dlq"));
        Assert.False(await Redis.KeyExistsAsync(HistoricoPassagemWorker.Tentativas(entry.Id)));
    }

    [Fact]
    public async Task Trim_PreservaPendingEUnread_ApenasProcessadosAntigosRemovidos()
    {
        var old=DateTimeOffset.UtcNow.AddDays(-9).ToUnixTimeMilliseconds();
        await Redis.StreamAddAsync(_stream,[new("tipo","antigo")],$"{old}-0");
        await Redis.StreamAddAsync(_stream,[new("tipo","pending")],$"{old+1}-0");
        await Redis.StreamAddAsync(_stream,[new("tipo","unread")],$"{old+2}-0");
        var worker=Worker();await worker.GarantirGrupoAsync();
        var entries=await Redis.StreamReadGroupAsync(_stream,HistoricoPassagemWorker.Group,worker.Consumer,">",2);
        await Redis.StreamAcknowledgeAsync(_stream,HistoricoPassagemWorker.Group,entries[0].Id);
        await worker.TrimSeguroAsync(DateTimeOffset.UtcNow);
        var kept=await Redis.StreamRangeAsync(_stream);
        Assert.Equal(2,kept.Length);Assert.Equal(entries[1].Id,kept[0].Id);
        Assert.Equal(1,(await Redis.StreamPendingAsync(_stream,HistoricoPassagemWorker.Group)).PendingMessageCount);
    }

    [Fact]
    public async Task Migration_LegadoNullable_ModeloSemDiferenca()
    {
        using var scope=db.Provider.CreateScope();var context=scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var legacy=await context.HistoricoPassagens.SingleAsync(h=>h.Id==db.Legacy);
        Assert.Null(legacy.ViagemId);Assert.Null(legacy.ParadaItinerarioId);Assert.Null(legacy.SentidoId);Assert.Null(legacy.TimestampPassagem);
        Assert.Equal(50,legacy.DistanciaParadaMetros);
        Assert.Contains("20260914180000_ViagemOperacionalOutbox",await context.Database.GetAppliedMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task DuasInstancias_EntregasEPostgresConcorrentes_SemDuplicacao()
    {
        await Terminal();var a=Worker();var b=Worker();await a.GarantirGrupoAsync();await b.GarantirGrupoAsync();
        Assert.NotEqual(a.Consumer,b.Consumer);
        var reads=await Task.WhenAll(Redis.StreamReadGroupAsync(_stream,HistoricoPassagemWorker.Group,a.Consumer,">",2),
            Redis.StreamReadGroupAsync(_stream,HistoricoPassagemWorker.Group,b.Consumer,">",100));
        Assert.Equal(4,reads.SelectMany(x=>x).Select(e=>e.Id).Distinct().Count());
        await Task.WhenAll(reads[0].Select(e=>a.ProcessarAsync(e,default)).Concat(reads[1].Select(e=>b.ProcessarAsync(e,default))));
        Assert.Equal(0,(await Redis.StreamPendingAsync(_stream,HistoricoPassagemWorker.Group)).PendingMessageCount);
        var passage=EventoViagemValidator.Parse((await Redis.StreamRangeAsync(_stream))[1].Values.ToDictionary(v=>v.Name.ToString(),v=>v.Value.ToString()));
        await Task.WhenAll(Enumerable.Range(0,20).Select(_=>new HistoricoEventoRepository(db.Source).PersistirAsync(passage,default)));
        using var scope=db.Provider.CreateScope();
        Assert.Equal(3,await scope.ServiceProvider.GetRequiredService<TransporteDbContext>().HistoricoPassagens.CountAsync(h=>h.Ordem==_ordem));
    }

    public class RespostaPerdidaProxy : DispatchProxy
    {
        public object Target {get;set;}=null!; public IDatabase? Database {get;set;}
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method!.Name==nameof(IConnectionMultiplexer.GetDatabase)) return Database;
            var result=method.Invoke(Target,args);
            return method.Name==nameof(IDatabase.ScriptEvaluateAsync) && args is {Length:>=3}
                && args[0] is string script && script==ViagemOperacionalRedisScript.Commit ? Perder((Task<RedisResult>)result!) : result;
        }
        private static async Task<RedisResult> Perder(Task<RedisResult> task){await task;throw new TimeoutException("Resposta perdida após EVAL real");}
    }

    [Fact]
    public async Task TimeoutAposEval_OutboxECursorPersistem_RepeticaoNaoDuplica()
    {
        await Repository().TentarAtualizarAsync(G(0),default);
        var database=DispatchProxy.Create<IDatabase,RespostaPerdidaProxy>();((RespostaPerdidaProxy)database).Target=Redis;
        var multiplexer=DispatchProxy.Create<IConnectionMultiplexer,RespostaPerdidaProxy>();
        ((RespostaPerdidaProxy)multiplexer).Target=db.Redis;((RespostaPerdidaProxy)multiplexer).Database=database;
        var lost=await new ViagemOperacionalRepository(multiplexer,db.Source,Options.Create(new GpsPollingOptions()),NullLogger<ViagemOperacionalRepository>.Instance){StreamKey=_stream}
            .TentarAtualizarAsync(G(100,.65),default);
        Assert.Equal(ViagemObservadaStatus.InfrastructureFailure,lost.Status);
        Assert.Equal(3,(await State()).Observada.UltimaParadaOrdem);Assert.Equal(4,await Redis.StreamLengthAsync(_stream));
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual,(await Repository().TentarAtualizarAsync(G(100,.65),default)).Status);
        Assert.Equal(4,await Redis.StreamLengthAsync(_stream));
        var worker=Worker();await worker.GarantirGrupoAsync();
        foreach(var entry in await Redis.StreamReadGroupAsync(_stream,HistoricoPassagemWorker.Group,worker.Consumer,">",100)) await worker.ProcessarAsync(entry,default);
        Assert.Equal(0,(await Redis.StreamPendingAsync(_stream,HistoricoPassagemWorker.Group)).PendingMessageCount);
    }

    [Fact]
    public async Task CorridaMesmoTimestamp_CasOutboxSomenteUmaVez()
    {
        await Repository().TentarAtualizarAsync(G(0),default);
        var result=await Task.WhenAll(Enumerable.Range(0,20).Select(_=>Repository().TentarAtualizarAsync(G(100,.65),default)));
        Assert.Single(result,r=>r.Status==ViagemObservadaStatus.Updated);
        Assert.All(result,r=>Assert.Contains(r.Status,new[]{ViagemObservadaStatus.Updated,ViagemObservadaStatus.RejectedOlderOrEqual}));
        Assert.Equal(4,await Redis.StreamLengthAsync(_stream));
    }

    [Theory]
    [InlineData("ConfirmacoesPosTerminal","-1")]
    [InlineData("EstadoViagem","Desconhecido")]
    [InlineData("TimestampFim","invalid")]
    [InlineData("CandidatoTimestamp","invalid")]
    [InlineData("CandidatoLatitudeInicial","invalid")]
    [InlineData("CandidatoLongitudeInicial","invalid")]
    [InlineData("SentidoId","invalid")]
    public async Task MaquinaCorrompida_FailClosedSemWrite(string field,string value)
    {
        await Terminal();await Redis.HashSetAsync(Key,field,value);
        var before=(await Redis.HashGetAllAsync(Key)).OrderBy(v=>v.Name.ToString()).ToArray();
        Assert.Equal(ViagemObservadaStatus.InvalidState,(await Repository().TentarAtualizarAsync(G(110,.66),default)).Status);
        Assert.Equal(before,(await Redis.HashGetAllAsync(Key)).OrderBy(v=>v.Name.ToString()).ToArray());
        Assert.Equal(4,await Redis.StreamLengthAsync(_stream));
    }

    [Fact]
    public async Task FkEUniqueParcial_Reais_JournalNaoPermiteReescreverEvento()
    {
        await Terminal();var fields=(await Redis.StreamRangeAsync(_stream))[1].Values.ToDictionary(v=>v.Name.ToString(),v=>v.Value.ToString());
        var e=EventoViagemValidator.Parse(fields);var repository=new HistoricoEventoRepository(db.Source);
        await repository.PersistirAsync(e,default);
        await Assert.ThrowsAsync<FormatException>(()=>repository.PersistirAsync(e with{TimestampGps=e.TimestampGps!.Value.AddSeconds(1)},default));
        using var scope=db.Provider.CreateScope();var context=scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        HistoricoPassagem H(Guid ocorrencia)=>new(){Id=Guid.NewGuid(),Ordem=_ordem,CodigoLinha="VIAGEM3",ItinerarioId=db.R1,ParadaId=db.Stop,
            ViagemId=e.ViagemId,ParadaItinerarioId=ocorrencia,SentidoId=db.S1,TimestampPassagem=e.TimestampPassagem,TimestampGps=G(100).TimestampGps,TimestampRegistro=DateTimeOffset.UtcNow};
        context.HistoricoPassagens.Add(H(e.ParadaItinerarioId!.Value));
        var duplicate=await Assert.ThrowsAsync<DbUpdateException>(()=>context.SaveChangesAsync());
        Assert.Equal("23505",Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        context.ChangeTracker.Clear();context.HistoricoPassagens.Add(H(Guid.NewGuid()));
        var foreign=await Assert.ThrowsAsync<DbUpdateException>(()=>context.SaveChangesAsync());
        Assert.Equal("23503",Assert.IsType<PostgresException>(foreign.InnerException).SqlState);
    }

    [Fact]
    public async Task R1Finalizada_NaoReabre_RegistraNovoCandidato()
    {
        await Terminal();await Repository().TentarAtualizarAsync(G(110,.66),default);await Repository().TentarAtualizarAsync(G(120,.67),default);
        var id=(await State()).Observada.ViagemId;
        await Repository().TentarAtualizarAsync(G(130,.1,true),default);Assert.NotNull((await State()).Candidato);
        await Repository().TentarAtualizarAsync(G(140,.68),default);
        var finalizada = await State();
        Assert.NotNull(finalizada.Candidato);Assert.Equal(db.R1,finalizada.Candidato!.ItinerarioId);
        Assert.Equal(id,finalizada.Observada.ViagemId);Assert.Equal(EstadoViagem.Finalizada,finalizada.Estado);
        Assert.Equal(5,await Redis.StreamLengthAsync(_stream));
    }

    [Fact]
    public async Task AmbiguidadeEntreSentidos_Reais_PreservaPossivelFimSemCandidato()
    {
        await Terminal();var sense=Guid.NewGuid();var route=Guid.NewGuid();
        using var scope=db.Provider.CreateScope();var context=scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        context.Sentidos.Add(new(){Id=sense,LinhaId=db.Linha,Nome="Não interpretar"});
        var geometry=await context.Itinerarios.Where(i=>i.Id==db.R2).Select(i=>i.Geometria).SingleAsync();
        context.Itinerarios.Add(new(){Id=route,SentidoId=sense,Geometria=geometry});await context.SaveChangesAsync();
        try
        {
            await Repository().TentarAtualizarAsync(G(110,.1,true),default);
            Assert.Equal(EstadoViagem.PossivelFim,(await State()).Estado);Assert.Null((await State()).Candidato);
            Assert.Equal(4,await Redis.StreamLengthAsync(_stream));
        }
        finally{await context.Itinerarios.Where(i=>i.Id==route).ExecuteDeleteAsync();await context.Sentidos.Where(s=>s.Id==sense).ExecuteDeleteAsync();}
    }

    [Fact]
    public async Task LinhaDiferenteEstrutural_PreservaPossivelFimSemCandidato()
    {
        await Terminal();var line=Guid.NewGuid();var sense=Guid.NewGuid();var route=Guid.NewGuid();
        using var scope=db.Provider.CreateScope();var context=scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var modal=await context.Linhas.Where(l=>l.Id==db.Linha).Select(l=>l.ModalId).SingleAsync();
        var geometry=await context.Itinerarios.Where(i=>i.Id==db.R2).Select(i=>i.Geometria).SingleAsync();
        context.Linhas.Add(new(){Id=line,ModalId=modal,Codigo="OUTRA3",Nome="Outra"});
        context.Sentidos.Add(new(){Id=sense,LinhaId=line,Nome="Outra"});context.Itinerarios.Add(new(){Id=route,SentidoId=sense,Geometria=geometry});
        await context.SaveChangesAsync();
        try
        {
            await Repository().TentarAtualizarAsync(G(110,.1,true) with{CodigoLinha="OUTRA3",ItinerarioId=route},default);
            Assert.Equal(EstadoViagem.PossivelFim,(await State()).Estado);Assert.Null((await State()).Candidato);
            Assert.Equal(db.Linha,(await State()).LinhaId);Assert.Equal(4,await Redis.StreamLengthAsync(_stream));
        }
        finally{await context.Itinerarios.Where(i=>i.Id==route).ExecuteDeleteAsync();await context.Sentidos.Where(s=>s.Id==sense).ExecuteDeleteAsync();await context.Linhas.Where(l=>l.Id==line).ExecuteDeleteAsync();}
    }

    [Theory]
    [InlineData(180,true)]
    [InlineData(181,false)]
    public async Task JanelaCandidato_RedisReal(int intervalo,bool cria)
    {
        await Terminal();await Repository().TentarAtualizarAsync(G(110,.66),default);await Repository().TentarAtualizarAsync(G(120,.67),default);
        await Repository().TentarAtualizarAsync(G(130,.1,true),default);
        var previous=await State();await Repository().TentarAtualizarAsync(G(130+intervalo,.2,true),default);
        var next=await State();Assert.Equal(cria,next.Estado==EstadoViagem.Ativa);
        Assert.Equal(cria,previous.Observada.ViagemId!=next.Observada.ViagemId);
        Assert.Equal(cria?6:5,await Redis.StreamLengthAsync(_stream));
    }

    [Fact]
    public async Task StreamSaturado_PreflightNaoCriaHash()
    {
        await Redis.StreamAddAsync(_stream,[new("tipo","saturado")],"18446744073709551615-18446744073709551615");
        Assert.Equal(ViagemObservadaStatus.InvalidState,(await Repository().TentarAtualizarAsync(G(0),default)).Status);
        Assert.False(await Redis.KeyExistsAsync(Key));Assert.Single(await Redis.StreamRangeAsync(_stream));
    }

    private sealed class MonitorPersistencia(IHistoricoEventoRepository inner) : IHistoricoEventoRepository
    {
        public readonly TaskCompletionSource Completo=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _passagens;
        public async Task PersistirAsync(EventoViagem e,CancellationToken ct)
        {
            await inner.PersistirAsync(e,ct);
            if(e.Tipo=="PassagemParada"&&Interlocked.Increment(ref _passagens)==3) Completo.TrySetResult();
        }
    }

    [Fact]
    public async Task WorkerHosted_LoopRealRedis7_PersisteEAck_SemBloquearPolling()
    {
        var monitor=new MonitorPersistencia(new HistoricoEventoRepository(db.Source));
        using var worker=Worker(monitor);
        await worker.StartAsync(default);
        try
        {
            await Terminal().WaitAsync(TimeSpan.FromSeconds(10));
            await monitor.Completo.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while((await Redis.StreamPendingAsync(_stream,HistoricoPassagemWorker.Group)).PendingMessageCount!=0)
                await Task.Delay(10,deadline.Token);
        }
        finally{await worker.StopAsync(default);}
        Assert.Equal(0,(await Redis.StreamPendingAsync(_stream,HistoricoPassagemWorker.Group)).PendingMessageCount);
    }

    [Fact]
    public async Task Block5000_ConexaoDedicada_CancelamentoNaoBloqueiaConexaoGps()
    {
        var worker=Worker();await worker.GarantirGrupoAsync();
        var config=ConfigurationOptions.Parse(db.Redis.Configuration);config.AsyncTimeout=10000;
        using var reader=await ConnectionMultiplexer.ConnectAsync(config);using var cts=new CancellationTokenSource();
        var read=worker.LerNovosAsync(reader.GetDatabase(),cts.Token);
        try
        {
            await Task.Delay(100);
            Assert.False(read.IsCompleted);
            await Redis.PingAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally{cts.Cancel();}
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>read);
    }

    private sealed class FalhaContada : IHistoricoEventoRepository
    {
        public int Calls;
        public Task PersistirAsync(EventoViagem e,CancellationToken ct){Interlocked.Increment(ref Calls);throw new TimeoutException("Falha durável identificável");}
    }

    [Fact]
    public async Task FalhaDeadLetterAposQuinta_ReinicioNaoRepetePostgres_PreservaErro()
    {
        await Repository().TentarAtualizarAsync(G(0),default);
        var failure=new FalhaContada();var worker=Worker(failure);await worker.GarantirGrupoAsync();
        var entry=Assert.Single(await Redis.StreamReadGroupAsync(_stream,HistoricoPassagemWorker.Group,worker.Consumer,">",100));
        for(var i=0;i<4;i++) await worker.ProcessarAsync(entry,default);
        await Redis.StringSetAsync(_stream+":dlq","TIPO_INCOMPATIVEL");
        await Assert.ThrowsAsync<RedisServerException>(()=>worker.ProcessarAsync(entry,default));
        Assert.Equal(5,failure.Calls);Assert.Equal("5",(await Redis.StringGetAsync(HistoricoPassagemWorker.Tentativas(entry.Id))).ToString());
        await Redis.KeyDeleteAsync(_stream+":dlq");
        await Worker(failure).ProcessarAsync(entry,default);
        Assert.Equal(5,failure.Calls);
        Assert.Equal(0,(await Redis.StreamPendingAsync(_stream,HistoricoPassagemWorker.Group)).PendingMessageCount);
        Assert.Contains("Falha durável identificável",Assert.Single(await Redis.StreamRangeAsync(_stream+":dlq")).Values.Single(v=>v.Name=="erro").Value.ToString());
    }

    [Fact]
    public async Task AclSemXadd_PreflightImpedeHset()
    {
        var config=ConfigurationOptions.Parse(db.Redis.Configuration);config.AllowAdmin=true;
        using var admin=await ConnectionMultiplexer.ConnectAsync(config);var user="teste-v3-"+Guid.NewGuid().ToString("N");
        await admin.GetDatabase().ExecuteAsync("ACL","SETUSER",user,"on",">senha-teste-v3","resetkeys","~"+Key,"~"+_stream,"+@all","-xadd");
        try
        {
            config.User=user;config.Password="senha-teste-v3";
            using var restricted=await ConnectionMultiplexer.ConnectAsync(config);
            var repository=new ViagemOperacionalRepository(restricted,db.Source,Options.Create(new GpsPollingOptions()),NullLogger<ViagemOperacionalRepository>.Instance){StreamKey=_stream};
            Assert.Equal(ViagemObservadaStatus.InvalidState,(await repository.TentarAtualizarAsync(G(0),default)).Status);
            Assert.False(await Redis.KeyExistsAsync(Key));Assert.False(await Redis.KeyExistsAsync(_stream));
        }
        finally{await admin.GetDatabase().ExecuteAsync("ACL","DELUSER",user);}
    }

    [Fact]
    public async Task LuaArgumentosInvalidos_ZeroWrites()
    {
        var result=await Redis.ScriptEvaluateAsync(ViagemOperacionalRedisScript.Commit,[Key,_stream],
            ["[]","{}","[]",ViagemOperacionalCodec.Tick(DateTimeOffset.UtcNow)]);
        Assert.Equal(5,(int)result);Assert.False(await Redis.KeyExistsAsync(Key));Assert.False(await Redis.KeyExistsAsync(_stream));
    }

    [Fact]
    public async Task HashAnterior3_2_ComCursor_AdotaSemPerderCruzamentosNovos()
    {
        await Repository().TentarAtualizarAsync(G(0),default);var id=(await State()).Observada.ViagemId;
        await Redis.HashDeleteAsync(Key,ViagemOperacionalCodec.Names.Skip(8).Select(n=>(RedisValue)n).ToArray());
        var result=await Repository().TentarAtualizarAsync(G(100,.65),default);
        Assert.Equal(ViagemObservadaStatus.Updated,result.Status);Assert.Equal(id,(await State()).Observada.ViagemId);
        Assert.Equal(EstadoViagem.PossivelFim,(await State()).Estado);
        Assert.Equal(3,result.OcorrenciasUltrapassadas.Count);Assert.Equal(4,await Redis.StreamLengthAsync(_stream));
    }

    [Fact]
    public async Task HashAnterior3_1_SemCursor_BaselinePreservadoSemRetroativos()
    {
        await Repository().TentarAtualizarAsync(G(0),default);var id=(await State()).Observada.ViagemId;
        await Redis.HashDeleteAsync(Key,ViagemOperacionalCodec.Names.Skip(6).Select(n=>(RedisValue)n).ToArray());
        var result=await Repository().TentarAtualizarAsync(G(100,.65),default);
        Assert.Equal(ViagemObservadaStatus.Updated,result.Status);Assert.Equal(id,(await State()).Observada.ViagemId);
        Assert.Empty(result.OcorrenciasUltrapassadas);Assert.Single(await Redis.StreamRangeAsync(_stream));
        Assert.Equal(0,(await State()).Observada.UltimaParadaOrdem);
    }

    [Fact]
    public async Task Ativa_GlobalB_ProjecaoAAvancaA_EEmiteSomenteParadaA_SemDuplicarNoRetorno()
    {
        var repository=Repository();
        Assert.Equal(ViagemObservadaStatus.Created,
            (await repository.TentarAtualizarAsync(G(0,.19),default)).Status);
        var contexto=Assert.IsType<ContextoOperacional>(
            await repository.LerContextoAsync(_ordem,default));
        var observacionalB=G(10,.80,true) with { CodigoLinha="414" };
        var projecaoA=ResultadoProjecaoOperacional.Encontrada(
            new(db.R1,.21,3,2220));

        var resultado=await repository.TentarAtualizarAsync(
            observacionalB,contexto,projecaoA,default);

        Assert.Equal(ViagemObservadaStatus.Updated,resultado.Status);
        var estado=await State();
        Assert.Equal(db.R1,estado.Observada.ItinerarioId);
        Assert.Equal("VIAGEM3",estado.CodigoLinha);
        Assert.Equal(db.S1,estado.SentidoId);
        Assert.Equal(.21,estado.Observada.PosicaoNaRotaConfirmada);
        var passagem=Assert.Single(resultado.OcorrenciasUltrapassadas);
        Assert.Equal(db.Occurrences[0],passagem.Id);
        Assert.Equal(db.R1,passagem.ItinerarioId);
        Assert.Equal(2,await Redis.StreamLengthAsync(_stream));
        var eventos=(await Redis.StreamRangeAsync(_stream))
            .Select(x=>EventoViagemValidator.Parse(x.Values.ToDictionary(
                v=>v.Name.ToString(),v=>v.Value.ToString()))).ToArray();
        Assert.DoesNotContain(eventos,e=>e.ItinerarioId==db.R2);

        var retorno=await repository.TentarAtualizarAsync(G(20,.21),
            Assert.IsType<ContextoOperacional>(await repository.LerContextoAsync(_ordem,default)),
            ResultadoProjecaoOperacional.Encontrada(new(db.R1,.21,2,2220)),default);
        Assert.Equal(ViagemObservadaStatus.Updated,retorno.Status);
        Assert.Empty(retorno.OcorrenciasUltrapassadas);
        Assert.Equal(2,await Redis.StreamLengthAsync(_stream));
    }

    [Fact]
    public async Task SnapshotAusente_ETransportadoComoCasVazio()
    {
        var contexto=Assert.IsType<ContextoOperacional>(
            await Repository().LerContextoAsync(_ordem,default));

        Assert.Empty(contexto.SnapshotCas);
        Assert.Null(contexto.Observada);
        Assert.Null(contexto.Estado);
        Assert.False(contexto.PodeProjetar);
    }

    [Fact]
    public async Task PossivelFim_GlobalB_ProjecaoTerminalAContinuaConfirmacoesEFinalizaA()
    {
        await Terminal();
        var repository=Repository();
        var primeira=await repository.TentarAtualizarAsync(G(110,.10,true),
            Assert.IsType<ContextoOperacional>(await repository.LerContextoAsync(_ordem,default)),
            ResultadoProjecaoOperacional.Encontrada(new(db.R1,.66,2,2220)),default);
        Assert.Equal(ViagemObservadaStatus.Updated,primeira.Status);
        Assert.Equal(1,(await State()).ConfirmacoesPosTerminal);

        var segunda=await repository.TentarAtualizarAsync(G(120,.11,true),
            Assert.IsType<ContextoOperacional>(await repository.LerContextoAsync(_ordem,default)),
            ResultadoProjecaoOperacional.Encontrada(new(db.R1,.67,2,2220)),default);
        var final=await State();
        Assert.Equal(ViagemObservadaStatus.Updated,segunda.Status);
        Assert.Equal(EstadoViagem.Finalizada,final.Estado);
        Assert.Equal(db.R1,final.Observada.ItinerarioId);
        Assert.Equal(db.S1,final.SentidoId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DivergenciaComProjecaoInelegivelOuFalha_CongelaA_SemEvento(bool infraestrutura)
    {
        await Terminal();
        var repository=Repository();
        var contexto=Assert.IsType<ContextoOperacional>(
            await repository.LerContextoAsync(_ordem,default));
        var antes=(await Redis.HashGetAllAsync(Key)).OrderBy(x=>x.Name.ToString()).ToArray();
        var eventosAntes=await Redis.StreamLengthAsync(_stream);
        var projecao=infraestrutura
            ? ResultadoProjecaoOperacional.Falha()
            : ResultadoProjecaoOperacional.Inelegivel();

        var resultado=await repository.TentarAtualizarAsync(
            G(110,.10,true),contexto,projecao,default);

        Assert.Equal(infraestrutura
            ? ViagemObservadaStatus.InfrastructureFailure
            : ViagemObservadaStatus.ItineraryChanged,resultado.Status);
        Assert.Equal(antes,(await Redis.HashGetAllAsync(Key)).OrderBy(x=>x.Name.ToString()).ToArray());
        Assert.Equal(eventosAntes,await Redis.StreamLengthAsync(_stream));
        Assert.Equal(0,(await State()).ConfirmacoesPosTerminal);
    }

    [Fact]
    public async Task ProjecaoARegressiva_ERejeitadaSemAlterarCursorOuHash()
    {
        var repository=Repository();
        await repository.TentarAtualizarAsync(G(0,.30),default);
        var contexto=Assert.IsType<ContextoOperacional>(
            await repository.LerContextoAsync(_ordem,default));
        var antes=(await Redis.HashGetAllAsync(Key)).OrderBy(x=>x.Name.ToString()).ToArray();

        var resultado=await repository.TentarAtualizarAsync(G(10,.80,true),contexto,
            ResultadoProjecaoOperacional.Encontrada(new(db.R1,.29,2,2220)),default);

        Assert.Equal(ViagemObservadaStatus.ItineraryChanged,resultado.Status);
        Assert.Equal(antes,(await Redis.HashGetAllAsync(Key)).OrderBy(x=>x.Name.ToString()).ToArray());
        Assert.Single(await Redis.StreamRangeAsync(_stream));
    }

    [Fact]
    public async Task SnapshotMudaDepoisDaProjecao_CasRejeitaESemPassagemIncorreta()
    {
        var repository=Repository();
        await repository.TentarAtualizarAsync(G(0,.19),default);
        var contextoAntigo=Assert.IsType<ContextoOperacional>(
            await repository.LerContextoAsync(_ordem,default));
        await repository.TentarAtualizarAsync(G(10,.21),default);
        var hashDepoisDoConcorrente=(await Redis.HashGetAllAsync(Key))
            .OrderBy(x=>x.Name.ToString()).ToArray();
        var eventosDepoisDoConcorrente=await Redis.StreamLengthAsync(_stream);

        var resultado=await repository.TentarAtualizarAsync(G(20,.80,true),contextoAntigo,
            ResultadoProjecaoOperacional.Encontrada(new(db.R1,.41,2,2220)),default);

        Assert.Equal(ViagemObservadaStatus.Conflict,resultado.Status);
        Assert.Equal(hashDepoisDoConcorrente,(await Redis.HashGetAllAsync(Key))
            .OrderBy(x=>x.Name.ToString()).ToArray());
        Assert.Equal(eventosDepoisDoConcorrente,await Redis.StreamLengthAsync(_stream));
    }
}
