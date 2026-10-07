using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

// SOMENTE serviços descartáveis; reutiliza fixture existente. Não executar nas rodadas locais.
public sealed class RetryOperacionalGpsRedisIntegracaoTests(ViagemOperacionalFixture db):IClassFixture<ViagemOperacionalFixture>
{
    private static PendenciaOperacionalGps P(string ordem,DateTimeOffset ts,DateTimeOffset now)
    {
        var gps=new PosicaoVeiculoDto{Ordem=ordem,CodigoLinha="VIAGEM3",TimestampGps=ts,
            Latitude=-22.9,Longitude=-43.2,ModalFonte="BUS",ProvedorFonte="TEST",MatchingOperacionalPlausivel=true};
        return new(TelemetriaMlContrato.ObservacaoId(gps.ModalFonte,gps.ProvedorFonte,ordem,ts),gps,now,now.AddSeconds(180));
    }
    [Fact]
    public async Task Lua_OrdenacaoLimitesLeaseCasEBackoff_PreservamPayload()
    {
        var prefix="teste:retry:lua:"+Guid.NewGuid().ToString("N")+":";
        var ordem="RETRY-"+Guid.NewGuid().ToString("N"); var outra=ordem+"B";
        var opts=new RetryOperacionalGpsOptions{MaxPorVeiculo=2,MaxGlobal=2};
        var store=new PendenciaOperacionalGpsRepository(db.Redis,Options.Create(opts)){Prefixo=prefix};
        var now=DateTimeOffset.UtcNow; var primeiro=P(ordem,now.AddSeconds(-20),now);
        var segundo=P(ordem,now.AddSeconds(-10),now);
        try
        {
            Assert.Equal("CRIADA",await store.AdicionarAsync(primeiro,default));
            Assert.Equal("EXISTENTE",await store.AdicionarAsync(primeiro with{CriadaEm=now.AddSeconds(1)},default));
            Assert.Equal("CONFLITO_PAYLOAD",await store.AdicionarAsync(primeiro with{Gps=primeiro.Gps with{Latitude=0}},default));
            Assert.Equal("CRIADA",await store.AdicionarAsync(segundo,default));
            Assert.Equal("DESCARTADA_LIMITE_GLOBAL",await store.AdicionarAsync(P(outra,now,now),default));
            opts.MaxGlobal=10;
            Assert.Equal("DESCARTADA_LIMITE_VEICULO",await store.AdicionarAsync(P(ordem,now,now),default));
            var claims=await Task.WhenAll(store.ClaimAsync(ordem,now,default),store.ClaimAsync(ordem,now,default));
            var lease=Assert.Single(claims,x=>x is not null)!;
            Assert.Equal(primeiro.Id,lease.Pendencia.Id); Assert.False(lease.Pendencia.Gps.MatchingOperacionalPlausivel);
            Assert.False(await store.ConcluirAsync(lease with{Token="OUTRO"},default));
            Assert.True(await store.ReagendarAsync(lease,now.AddSeconds(30),default));
            Assert.Null(await store.ClaimAsync(ordem,now,default)); // segundo não ultrapassa cabeça em backoff
            var retry=(await store.ClaimAsync(ordem,now.AddSeconds(31),default))!;
            Assert.Equal(1,retry.Pendencia.Tentativas); Assert.Equal(primeiro.Id,retry.Pendencia.Id);
            Assert.False(await store.ConcluirAsync(lease,default)); // token antigo não remove dono atual
            Assert.True(await store.ConcluirAsync(retry,default));
            var posterior=(await store.ClaimAsync(ordem,now.AddSeconds(31),default))!;
            Assert.Equal(segundo.Id,posterior.Pendencia.Id); Assert.True(await store.ConcluirAsync(posterior,default));
            Assert.False(await store.TemPendenciaAsync(ordem,default));
        }
        finally {await db.Redis.GetDatabase().KeyDeleteAsync([prefix+"dados",prefix+"prazos",prefix+"veiculo:"+ordem,
            prefix+"lease:"+ordem,prefix+"veiculo:"+outra,prefix+"lease:"+outra]);}
    }

    [Fact]
    public async Task Lua_PendenciaExpirada_EncerraSemChamarOperacao()
    {
        var prefix="teste:retry:expiry:"+Guid.NewGuid().ToString("N")+":";
        var ordem="RETRY-"+Guid.NewGuid().ToString("N"); var opts=Options.Create(new RetryOperacionalGpsOptions());
        var store=new PendenciaOperacionalGpsRepository(db.Redis,opts){Prefixo=prefix};
        var repo=new ViagemOperacionalRepository(db.Redis,db.Source,Options.Create(new GpsPollingOptions()),NullLogger<ViagemOperacionalRepository>.Instance);
        var enriq=new GpsEnriquecimentoService(new GpsPadraoRepository(db.Source,NullLogger<GpsPadraoRepository>.Instance),
            Options.Create(new GpsPollingOptions()),NullLogger<GpsEnriquecimentoService>.Instance);
        var retry=new RetryOperacionalGpsService(store,new(repo,NullLogger<ViagemObservadaService>.Instance),enriq,
            opts,Options.Create(new GpsPollingOptions()),NullLogger<RetryOperacionalGpsService>.Instance);
        var now=DateTimeOffset.UtcNow;
        try
        {
            await store.AdicionarAsync(P(ordem,now.AddSeconds(-30),now.AddSeconds(-10)) with{ExpiraEm=now.AddSeconds(-1)},default);
            await retry.ExecutarCicloAsync(default); Assert.False(await store.TemPendenciaAsync(ordem,default));
            Assert.Null((await repo.LerDuravelParaRetryAsync(ordem,default))!.Observada);
            Assert.Null(await db.Redis.GetDatabase().KeyTimeToLiveAsync(prefix+"veiculo:"+ordem));
        }
        finally {await db.Redis.GetDatabase().KeyDeleteAsync([prefix+"dados",prefix+"prazos",prefix+"veiculo:"+ordem,prefix+"lease:"+ordem]);}
    }
}
