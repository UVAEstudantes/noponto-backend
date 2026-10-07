using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class RetryOperacionalGpsTests
{
    internal sealed class Store(RetryOperacionalGpsOptions? options=null) : IPendenciaOperacionalGpsStore
    {
        internal readonly Dictionary<string,PendenciaOperacionalGps> Itens=[];
        private readonly Dictionary<string,DateTimeOffset> _due=[];
        private readonly Dictionary<string,string> _leases=[];
        private readonly object _gate=new();
        internal bool Falhar; internal int Gravacoes;
        private RetryOperacionalGpsOptions Settings=>options??new();
        public Task<string> AdicionarAsync(PendenciaOperacionalGps p,CancellationToken ct)
        {
            lock(_gate)
            {
                if(Falhar) throw new TimeoutException();
                if(Itens.TryGetValue(p.Id,out var old)) return Task.FromResult(old.Assinatura==p.Assinatura?"EXISTENTE":"CONFLITO_PAYLOAD");
                if(Itens.Count>=Settings.MaxGlobal) return Task.FromResult("DESCARTADA_LIMITE_GLOBAL");
                if(Itens.Values.Count(x=>x.Gps.Ordem==p.Gps.Ordem)>=Settings.MaxPorVeiculo) return Task.FromResult("DESCARTADA_LIMITE_VEICULO");
                Itens.Add(p.Id,p); _due[p.Id]=p.CriadaEm; Gravacoes++;
                return Task.FromResult("CRIADA");
            }
        }
        public Task<LeasePendenciaOperacional?> ClaimAsync(string ordem,DateTimeOffset agora,CancellationToken ct)
        {
            lock(_gate)
            {
                if(Falhar) throw new TimeoutException();
                var p=Itens.Values.Where(x=>x.Gps.Ordem==ordem).OrderBy(x=>x.Gps.TimestampGps).FirstOrDefault();
                if(p is null||_due[p.Id]>agora||_leases.ContainsKey(ordem)) return Task.FromResult<LeasePendenciaOperacional?>(null);
                var token=Guid.NewGuid().ToString("N"); _leases[ordem]=token;
                return Task.FromResult<LeasePendenciaOperacional?>(new(p,token));
            }
        }
        public Task<bool> ConcluirAsync(LeasePendenciaOperacional l,CancellationToken ct)
        {
            lock(_gate)
            {
                if(Falhar) throw new TimeoutException();
                if(!_leases.TryGetValue(l.Pendencia.Gps.Ordem,out var t)||t!=l.Token) return Task.FromResult(false);
                Itens.Remove(l.Pendencia.Id); _due.Remove(l.Pendencia.Id); _leases.Remove(l.Pendencia.Gps.Ordem);
                return Task.FromResult(true);
            }
        }
        public Task<bool> ReagendarAsync(LeasePendenciaOperacional l,DateTimeOffset quando,CancellationToken ct)
        {
            lock(_gate)
            {
                if(Falhar) throw new TimeoutException();
                if(!_leases.TryGetValue(l.Pendencia.Gps.Ordem,out var t)||t!=l.Token) return Task.FromResult(false);
                Itens[l.Pendencia.Id]=l.Pendencia with{Tentativas=l.Pendencia.Tentativas+1};
                _due[l.Pendencia.Id]=quando<l.Pendencia.ExpiraEm?quando:l.Pendencia.ExpiraEm;
                _leases.Remove(l.Pendencia.Gps.Ordem); return Task.FromResult(true);
            }
        }
        public Task<bool> TemPendenciaAsync(string ordem,CancellationToken ct)
        {lock(_gate) {if(Falhar) throw new TimeoutException(); return Task.FromResult(Itens.Values.Any(x=>x.Gps.Ordem==ordem));}}
        public Task<IReadOnlyList<string>> VeiculosElegiveisAsync(DateTimeOffset agora,CancellationToken ct)
        {lock(_gate) return Task.FromResult<IReadOnlyList<string>>(Itens.Values.Where(x=>_due[x.Id]<=agora).Select(x=>x.Gps.Ordem).Distinct().Take(Settings.LotePorCiclo).ToArray());}
    }

    internal sealed class Enriquecedor : IEnriquecimentoRetryOperacionalGps
    {
        internal int Chamadas;
        internal bool Plausivel=true;
        internal bool AlterarObservacao;
        internal TaskCompletionSource? Entrou, Liberar;
        public async Task<ResultadoEnriquecimentoGps> RecalcularAsync(PendenciaOperacionalGps p,ContextoOperacional? c,CancellationToken ct)
        {
            Chamadas++; Entrou?.TrySetResult(); if(Liberar is not null) await Liberar.Task.WaitAsync(ct);
            return new(p.Gps with{Ordem=AlterarObservacao?"OUTRO":p.Gps.Ordem,PadraoVersaoId=Guid.NewGuid(),PosicaoNaRota=.54,
                MatchingOperacionalPlausivel=Plausivel},c,ResultadoProjecaoOperacional.NaoSolicitada());
        }
    }
    private sealed class Repo : IViagemObservadaRepository
    {
        internal int Chamadas;
        internal ContextoOperacional? Duravel, Contexto;
        internal bool Confirmar=true, Falhar;
        public Task<ContextoOperacional?> LerDuravelParaRetryAsync(string ordem,CancellationToken ct)=>Task.FromResult(Duravel);
        public Task<ContextoOperacional?> LerContextoAsync(string ordem,CancellationToken ct)=>Task.FromResult(Contexto);
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(string ordem,Guid id,DateTimeOffset ts,double p,CancellationToken ct)
        {Chamadas++; return Task.FromResult(new ViagemObservadaResultado(Falhar?ViagemObservadaStatus.InfrastructureFailure:ViagemObservadaStatus.Updated){PersistidoDuravelmente=!Falhar&&Confirmar});}
    }
    private static DateTimeOffset T=>DateTimeOffset.UtcNow.AddSeconds(-30);
    private static PosicaoVeiculoDto Gps(DateTimeOffset t)=>new(){Ordem="RETRY",CodigoLinha="10",TimestampGps=t,
        Latitude=-22.9,Longitude=-43.2,LatitudeAnterior=-22.9,LongitudeAnterior=-43.2001,
        TimestampAnterior=t.AddSeconds(-10),Velocidade=20,Bearing=90,ModalFonte="BUS",ProvedorFonte="TEST"};
    private static PosicaoVeiculoDto Anterior(PosicaoVeiculoDto p)=>p with{TimestampGps=p.TimestampAnterior!.Value,
        Latitude=p.LatitudeAnterior!.Value,Longitude=p.LongitudeAnterior!.Value};
    private static RetryOperacionalGpsService Service(Store s,Repo r,Enriquecedor e,RetryOperacionalGpsOptions? opt=null,
        Func<DateTimeOffset>? clock=null)=>new(s,new(r,NullLogger<ViagemObservadaService>.Instance),e,
        Options.Create(opt??new()),Options.Create(new GpsPollingOptions()),NullLogger<RetryOperacionalGpsService>.Instance)
        {Agora=clock??(()=>DateTimeOffset.UtcNow)};

    [Fact] public async Task FalhaTransitoria_RegistroSemProvaSerializada_RecalculaEConfirmaUmaVez()
    {
        var s=new Store(); var r=new Repo(); var e=new Enriquecedor(); var service=Service(s,r,e); var p=Gps(T) with{MatchingOperacionalPlausivel=true};
        await service.RegistrarAsync(p,Anterior(p),null,default);
        var pending=Assert.Single(s.Itens.Values); Assert.False(pending.Gps.MatchingOperacionalPlausivel);
        Assert.Null(pending.Gps.PadraoVersaoId); Assert.NotNull(pending.Predecessor);
        await service.RecuperarVeiculoAsync(p.Ordem,default); await service.RecuperarVeiculoAsync(p.Ordem,default);
        Assert.Empty(s.Itens); Assert.Equal(1,r.Chamadas); Assert.Equal(1,e.Chamadas);
    }
    [Fact] public async Task UpdatedSemCommit_NaoApagaPendencia_BackoffLimitaRepeticao()
    {
        var now=T; var s=new Store(); var r=new Repo{Confirmar=false}; var e=new Enriquecedor(); var service=Service(s,r,e,clock:()=>now);
        var p=Gps(now); await service.RegistrarAsync(p,Anterior(p),null,default);
        await service.RecuperarVeiculoAsync(p.Ordem,default); await service.RecuperarVeiculoAsync(p.Ordem,default);
        Assert.Equal(1,r.Chamadas); Assert.Equal(1,Assert.Single(s.Itens.Values).Tentativas);
        now=now.AddSeconds(21); r.Confirmar=true; await service.RecuperarVeiculoAsync(p.Ordem,default); Assert.Empty(s.Itens);
    }
    [Fact] public async Task RespostaPerdida_CommitDuravelReconhecido_SemRepetirEfeitos()
    {
        var s=new Store(); var r=new Repo(); var e=new Enriquecedor(); var service=Service(s,r,e); var p=Gps(T);
        await service.RegistrarAsync(p,Anterior(p),null,default);
        r.Duravel=new([],new(Guid.NewGuid(),p.Ordem,Guid.NewGuid(),p.TimestampGps.AddMinutes(-1),p.TimestampGps,.54),null);
        await service.RecuperarVeiculoAsync(p.Ordem,default); Assert.Empty(s.Itens); Assert.Equal(0,r.Chamadas); Assert.Equal(0,e.Chamadas);
    }
    [Fact] public async Task NovoEstadoAntesDoRetry_DescartaAntigoSemRetroceder()
    {
        var s=new Store(); var r=new Repo(); var e=new Enriquecedor(); var service=Service(s,r,e); var p=Gps(T);
        await service.RegistrarAsync(p,Anterior(p),null,default);
        r.Contexto=new([],new(Guid.NewGuid(),p.Ordem,Guid.NewGuid(),p.TimestampGps.AddMinutes(-1),p.TimestampGps.AddSeconds(1),.6),null);
        await service.RecuperarVeiculoAsync(p.Ordem,default); Assert.Empty(s.Itens); Assert.Equal(0,r.Chamadas);
    }
    [Theory][InlineData("ausente")][InlineData("salto")][InlineData("identidade")]
    public async Task PredecessorInvalido_NaoReconstruiOperacao(string caso)
    {
        var s=new Store(); var r=new Repo(); var e=new Enriquecedor(); var service=Service(s,r,e); var p=Gps(T);
        if(caso=="salto") p=p with{Latitude=0};
        var a=Anterior(p); if(caso=="identidade") a=a with{Ordem="OUTRO"};
        await service.RegistrarAsync(p,caso=="ausente"?null:a,null,default);
        await service.RecuperarVeiculoAsync(p.Ordem,default); Assert.Empty(s.Itens); Assert.Equal(0,e.Chamadas);
    }
    [Fact] public async Task ExpiracaoELimites_NaoViraramSucessoNemFilaIlimitada()
    {
        var now=T; var opt=new RetryOperacionalGpsOptions{MaxPorVeiculo=1,MaxGlobal=1,TtlSegundos=20}; var s=new Store(opt);
        var r=new Repo(); var e=new Enriquecedor(); var service=Service(s,r,e,opt,()=>now); var p=Gps(now);
        await service.RegistrarAsync(p,Anterior(p),null,default);
        var outro=p with{TimestampGps=p.TimestampGps.AddSeconds(1)};
        await service.RegistrarAsync(outro,Anterior(outro),null,default); Assert.Single(s.Itens);
        now=now.AddSeconds(21); await service.ExecutarCicloAsync(default); Assert.Empty(s.Itens); Assert.Equal(0,r.Chamadas);
    }
    [Fact] public async Task Concorrencia_LeasePorVeiculo_NaoExecutaDuasVezes()
    {
        var s=new Store(); var r=new Repo(); var e=new Enriquecedor{Entrou=new(),Liberar=new()}; var service=Service(s,r,e); var p=Gps(T);
        await service.RegistrarAsync(p,Anterior(p),null,default);
        var primeira=service.RecuperarVeiculoAsync(p.Ordem,default); await e.Entrou.Task;
        await Service(s,r,e).RecuperarVeiculoAsync(p.Ordem,default); Assert.Equal(1,e.Chamadas);
        e.Liberar.TrySetResult(); await primeira; Assert.Equal(1,r.Chamadas); Assert.Empty(s.Itens);
    }
    [Fact] public async Task RedisIndisponivelOuPerdido_NaoPrometeRecuperacao()
    {
        var s=new Store{Falhar=true}; var r=new Repo(); var e=new Enriquecedor(); var service=Service(s,r,e); var p=Gps(T);
        await service.RegistrarAsync(p,Anterior(p),null,default); Assert.Empty(s.Itens);
        Assert.True(await service.TemPendenciaAsync(p.Ordem,default));
        s.Falhar=false; await service.ExecutarCicloAsync(default); Assert.Equal(0,r.Chamadas);
    }
    [Fact] public async Task CaminhoNormalSemPendencia_NaoGravaFilaNemConsultaViagem()
    {
        var s=new Store(); var r=new Repo(); var e=new Enriquecedor(); var service=Service(s,r,e);
        await service.ExecutarCicloAsync(default); await service.RecuperarVeiculoAsync("RETRY",default);
        Assert.False(await service.TemPendenciaAsync("RETRY",default)); Assert.Equal(0,s.Gravacoes); Assert.Equal(0,r.Chamadas);
    }
    [Fact] public async Task FalhasPersistentes_LimiteTentativasELote_NaoReprocessamIndefinidamente()
    {
        var now=T; var opt=new RetryOperacionalGpsOptions{MaxTentativas=1,LotePorCiclo=1};
        var s=new Store(opt); var r=new Repo{Falhar=true}; var e=new Enriquecedor();
        var service=Service(s,r,e,opt,()=>now); var p=Gps(now);
        await service.RegistrarAsync(p,Anterior(p),null,default);
        await service.ExecutarCicloAsync(default); await service.RecuperarVeiculoAsync(p.Ordem,default);
        Assert.Equal(1,r.Chamadas); Assert.Single(s.Itens);
        now=now.AddSeconds(21); await service.ExecutarCicloAsync(default);
        Assert.Equal(1,r.Chamadas); Assert.Empty(s.Itens);
    }
    [Fact] public async Task PendenciasOrdenadas_BackoffDaAntigaImpedeUltrapassagem()
    {
        var now=T; var s=new Store(); var r=new Repo{Confirmar=false}; var e=new Enriquecedor();
        var service=Service(s,r,e,clock:()=>now); var p=Gps(now);
        await service.RegistrarAsync(p,Anterior(p),null,default);
        await service.RecuperarVeiculoAsync(p.Ordem,default);
        var posterior=Gps(now.AddSeconds(1));
        await service.RegistrarAsync(posterior,Anterior(posterior),null,default,aguardandoAnterior:true);
        await service.RecuperarVeiculoAsync(p.Ordem,default); Assert.Equal(1,r.Chamadas);
        now=now.AddSeconds(21); r.Confirmar=true; await service.ExecutarCicloAsync(default);
        Assert.Single(s.Itens); await service.RecuperarVeiculoAsync(p.Ordem,default);
        Assert.Empty(s.Itens); Assert.Equal(3,r.Chamadas);
    }
    [Fact] public async Task ReenriquecimentoDivergente_NaoProcessaOutroGps()
    {
        var s=new Store(); var r=new Repo(); var e=new Enriquecedor{AlterarObservacao=true};
        var service=Service(s,r,e); var p=Gps(T);
        await service.RegistrarAsync(p,Anterior(p),null,default); await service.RecuperarVeiculoAsync(p.Ordem,default);
        Assert.Empty(s.Itens); Assert.Equal(0,r.Chamadas);
    }
    [Fact] public async Task MatchingSemProva_NaoAceitaBooleanAntigoNemEmiteOperacao()
    {
        var s=new Store(); var r=new Repo(); var e=new Enriquecedor{Plausivel=false}; var service=Service(s,r,e);
        var p=Gps(T) with{MatchingOperacionalPlausivel=true};
        await service.RegistrarAsync(p,Anterior(p),null,default); await service.RecuperarVeiculoAsync(p.Ordem,default);
        Assert.Single(s.Itens); Assert.Equal(0,r.Chamadas);
    }
    [Fact] public async Task TimeoutDeRetry_ReagendaSemBloquearIndefinidamente()
    {
        var opt=new RetryOperacionalGpsOptions{TimeoutTentativaSegundos=1}; var s=new Store(opt); var r=new Repo();
        var e=new Enriquecedor{Entrou=new(),Liberar=new()}; var service=Service(s,r,e,opt); var p=Gps(T);
        await service.RegistrarAsync(p,Anterior(p),null,default);
        await service.RecuperarVeiculoAsync(p.Ordem,default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1,Assert.Single(s.Itens.Values).Tentativas); Assert.Equal(0,r.Chamadas);
    }
}
