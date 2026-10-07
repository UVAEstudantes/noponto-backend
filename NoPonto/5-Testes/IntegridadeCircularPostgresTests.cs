using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

// Somente serviços descartáveis. Compilar localmente; executar manualmente após revisão.
public sealed class IntegridadeCircularPostgresTests(ViagemOperacionalFixture db):IClassFixture<ViagemOperacionalFixture>
{
    [Fact]
    public async Task CircularConfiavel_FlagDesligada_PreservaSnapshotsQuentesSemCriarAncora()
    {
        var circular=await db.CriarCircularAsync();
        await using var h=new MudancaOperacionalPontaAPontaTests.Harness(db,enabled:false,checkpointSegundos:60);
        for(var i=0;i<3;i++)
        {
            var ponto=await db.PontoCircularAsync(circular.PadraoVersaoId,.90+i*.01);
            await h.CicloPosicao(i*10,circular.CodigoLinha,ponto.Latitude,ponto.Longitude,ponto.Bearing);
        }
        Assert.Equal(ViagemObservadaStatus.Created,h.Spy.Resultados[0].Status);
        Assert.All(h.Spy.Resultados.Skip(1),r=>Assert.Equal(ViagemObservadaStatus.Updated,r.Status));
        var estado=await h.Estado();
        Assert.Null(estado.Integridade); Assert.Null(estado.Candidato); Assert.Equal(0,estado.Observada.Volta);
        Assert.InRange(estado.Observada.PosicaoNaRotaConfirmada,.919,.921);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CircularFechada_EnriquecimentoReal_DivergenciaWrapBaselineEPassagem(bool batch)
    {
        var circular=await db.CriarCircularAsync();
        await using var h=new MudancaOperacionalPontaAPontaTests.Harness(db,batch:batch);
        async Task Ciclo(int segundos,double p,string? codigo=null)
        {
            var ponto=await db.PontoCircularAsync(circular.PadraoVersaoId,p);
            await h.CicloPosicao(segundos,codigo??circular.CodigoLinha,ponto.Latitude,ponto.Longitude,ponto.Bearing);
        }
        // Coordenadas derivadas da geometria fechada. Nenhuma prova de matching injetada.
        await Ciclo(0,.90); await Ciclo(10,.95);
        var anterior=await h.Estado();
        Assert.NotNull(anterior.Integridade!.Ancora);
        Assert.True(h.Spy.Entradas[^1].MatchingOperacionalPlausivel);
        await Ciclo(20,.97,"VIAGEM3");
        Assert.True(h.Spy.Entradas[^1].MatchingOperacionalPlausivel);
        Assert.NotNull((await h.Estado()).Candidato);
        await Ciclo(30,.02);
        var retorno=await h.Estado();
        Assert.True(h.Spy.Entradas[^1].MatchingOperacionalPlausivel);
        Assert.Equal(anterior.Observada.ViagemId,retorno.Observada.ViagemId);
        Assert.Equal(1,retorno.Observada.Volta); Assert.Null(retorno.Candidato);
        Assert.True(ViagemOperacionalRegra.IdentidadeConfiavel(retorno));
        Assert.Empty(h.Spy.Resultados[^1].OcorrenciasUltrapassadas);
        Assert.Equal(0,await h.Passagens());
        await db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(h.Ordem));
        Assert.Equal(retorno,await h.Estado());
        await Ciclo(50,.21);
        var eventos=await h.Materializar();
        Assert.DoesNotContain(eventos,e=>e.Tipo=="ViagemFinalizada");
        var passagem=Assert.Single(eventos,e=>e.Tipo=="PassagemParada");
        Assert.Equal(1,passagem.Volta); Assert.Equal(anterior.Observada.ViagemId,passagem.ViagemId);
        Assert.Equal(1,await h.Passagens());
    }

    [Fact]
    public async Task FalhaDepoisEstado_ReverteIntegridadeViagemEOutbox()
    {
        var circular=await db.CriarCircularAsync();
        var ordem="CIRC-ROLLBACK-"+Guid.NewGuid().ToString("N"); var stream="teste:circ:"+Guid.NewGuid().ToString("N");
        var t=DateTimeOffset.UtcNow.AddMinutes(-3);
        var falhar=false;
        var repository=new ViagemOperacionalRepository(db.Redis,db.Source,
            Options.Create(new GpsPollingOptions{MudancaOperacionalHabilitada=true,CheckpointViagemSegundos=0}),
            NullLogger<ViagemOperacionalRepository>.Instance){StreamKey=stream,AfterDurableStateWriteAsync=()=>
                falhar?Task.FromException(new InvalidOperationException("Falha de teste após escrita atômica")):Task.CompletedTask};
        async Task<PosicaoVeiculoDto> G(int seconds,double p)
        {
            var point=await db.PontoCircularAsync(circular.PadraoVersaoId,p);
            return new(){Ordem=ordem,CodigoLinha=circular.CodigoLinha,LinhaId=circular.LinhaId,SentidoId=circular.SentidoId,
                PadraoOperacionalId=circular.PadraoOperacionalId,PadraoVersaoId=circular.PadraoVersaoId,TopologiaPadrao="CIRCULAR",
                Latitude=point.Latitude,Longitude=point.Longitude,ComprimentoRotaMetros=point.Comprimento,Bearing=point.Bearing,
                Velocidade=20,TimestampGps=t.AddSeconds(seconds),PosicaoNaRota=p,MatchingOperacionalPlausivel=true};
        }
        async Task<(string Estado,string? Integridade,long Versao,long Outbox,DateTimeOffset Atualizado)> Snapshot()
        {
            await using var command=db.Source.CreateCommand("""
                SELECT "Estado"::text,"IntegridadeCircular"::text,"Versao",
                    (SELECT count(*) FROM "OutboxViagens" WHERE "Payload"->>'ordem_veiculo'=@ordem),"AtualizadoEmUtc"
                FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=@ordem
                """);
            command.Parameters.AddWithValue("ordem",ordem);
            await using var reader=await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            return (reader.GetString(0),reader.IsDBNull(1)?null:reader.GetString(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetFieldValue<DateTimeOffset>(4));
        }
        try
        {
            Assert.Equal(ViagemObservadaStatus.Created,(await repository.TentarAtualizarAsync(await G(0,.95),default)).Status);
            var b=new PosicaoVeiculoDto{Ordem=ordem,CodigoLinha="VIAGEM3",LinhaId=db.Linha,SentidoId=db.S1,
                PadraoOperacionalId=db.P1,PadraoVersaoId=db.R1,TopologiaPadrao="LINEAR",Latitude=-22.9,Longitude=-43.205,
                Bearing=90,Velocidade=20,ComprimentoRotaMetros=2000,PosicaoNaRota=.25,
                TimestampGps=t.AddSeconds(10),MatchingOperacionalPlausivel=true};
            Assert.Equal(ViagemObservadaStatus.Updated,(await repository.TentarAtualizarAsync(b,default)).Status);
            var anterior=await Snapshot();
            falhar=true;
            Assert.Equal(ViagemObservadaStatus.InfrastructureFailure,(await repository.TentarAtualizarAsync(await G(80,.02),default)).Status);
            Assert.Equal(anterior,await Snapshot());
            falhar=false;
            Assert.Equal(ViagemObservadaStatus.Updated,(await repository.TentarAtualizarAsync(await G(80,.02),default)).Status);
            var protegida=(await repository.LerContextoAsync(ordem,default))!.Estado!;
            Assert.Equal(ContinuidadeCircular.Ambigua,protegida.Integridade!.Continuidade);
            Assert.Equal(anterior.Outbox,(await Snapshot()).Outbox);
            await db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(ordem));
            Assert.Equal(protegida,(await repository.LerContextoAsync(ordem,default))!.Estado);
            Assert.Equal(ViagemObservadaStatus.Updated,(await repository.TentarAtualizarAsync(await G(90,.03),default)).Status);
            anterior=await Snapshot();
            falhar=true;
            Assert.Equal(ViagemObservadaStatus.InfrastructureFailure,(await repository.TentarAtualizarAsync(await G(100,.04),default)).Status);
            Assert.Equal(anterior,await Snapshot());
            falhar=false;
            Assert.Equal(ViagemObservadaStatus.Updated,(await repository.TentarAtualizarAsync(await G(100,.04),default)).Status);
            Assert.NotEqual(protegida.Observada.ViagemId,(await repository.LerContextoAsync(ordem,default))!.Observada!.ViagemId);
        }
        finally { await db.Redis.GetDatabase().KeyDeleteAsync([ViagemObservadaRepository.ChaveVeiculoViagem(ordem),stream]); }
    }

    [Fact]
    public async Task RecuperacaoConcorrente_ComFlagDesligada_ApenasUmaExecucaoNova()
    {
        var circular=await db.CriarCircularAsync();
        var ordem="CIRC-CAS-"+Guid.NewGuid().ToString("N"); var stream="teste:circ:"+Guid.NewGuid().ToString("N");
        var t=DateTimeOffset.UtcNow.AddMinutes(-3);
        var opcoes=new GpsPollingOptions{MudancaOperacionalHabilitada=true,CheckpointViagemSegundos=0};
        var repository=new ViagemOperacionalRepository(db.Redis,db.Source,Options.Create(opcoes),NullLogger<ViagemOperacionalRepository>.Instance){StreamKey=stream};
        async Task<PosicaoVeiculoDto> G(int seconds,double p)
        {
            var point=await db.PontoCircularAsync(circular.PadraoVersaoId,p);
            return new(){Ordem=ordem,CodigoLinha=circular.CodigoLinha,LinhaId=circular.LinhaId,SentidoId=circular.SentidoId,
                PadraoOperacionalId=circular.PadraoOperacionalId,PadraoVersaoId=circular.PadraoVersaoId,TopologiaPadrao="CIRCULAR",
                Latitude=point.Latitude,Longitude=point.Longitude,ComprimentoRotaMetros=point.Comprimento,Bearing=point.Bearing,
                Velocidade=20,TimestampGps=t.AddSeconds(seconds),PosicaoNaRota=p,MatchingOperacionalPlausivel=true};
        }
        try
        {
            await repository.TentarAtualizarAsync(await G(0,.95),default);
            var b=new PosicaoVeiculoDto{Ordem=ordem,CodigoLinha="VIAGEM3",LinhaId=db.Linha,SentidoId=db.S1,
                PadraoOperacionalId=db.P1,PadraoVersaoId=db.R1,TopologiaPadrao="LINEAR",Latitude=-22.9,Longitude=-43.205,
                Bearing=90,Velocidade=20,ComprimentoRotaMetros=2000,PosicaoNaRota=.25,
                TimestampGps=t.AddSeconds(10),MatchingOperacionalPlausivel=true};
            await repository.TentarAtualizarAsync(b,default);
            await repository.TentarAtualizarAsync(await G(80,.02),default);
            opcoes.MudancaOperacionalHabilitada=false;
            var novoProcesso=new ViagemOperacionalRepository(db.Redis,db.Source,Options.Create(opcoes),NullLogger<ViagemOperacionalRepository>.Instance){StreamKey=stream};
            await db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(ordem));
            Assert.Equal(ContinuidadeCircular.Ambigua,(await novoProcesso.LerContextoAsync(ordem,default))!.Estado!.Integridade!.Continuidade);
            await novoProcesso.TentarAtualizarAsync(await G(90,.03),default);
            var contexto=(await novoProcesso.LerContextoAsync(ordem,default))!;
            var gps=await G(100,.04);
            var resultados=await Task.WhenAll(repository.TentarAtualizarAsync(gps,contexto,ResultadoProjecaoOperacional.NaoSolicitada(),default),
                novoProcesso.TentarAtualizarAsync(gps,contexto,ResultadoProjecaoOperacional.NaoSolicitada(),default));
            Assert.Single(resultados,x=>x.Status==ViagemObservadaStatus.Updated);
            Assert.Single(resultados,x=>x.Status==ViagemObservadaStatus.RejectedOlderOrEqual);
            await using var count=db.Source.CreateCommand("SELECT count(*) FROM \"OutboxViagens\" WHERE \"Payload\"->>'ordem_veiculo'=@ordem AND \"Tipo\"='ViagemFinalizada'");
            count.Parameters.AddWithValue("ordem",ordem); Assert.Equal(1L,await count.ExecuteScalarAsync());
            var worker=new ViagemOutboxWorker(db.Source,new HistoricoEventoRepository(db.Source),NullLogger<ViagemOutboxWorker>.Instance);
            await worker.ProcessarLoteAsync(await worker.ClaimAsync(default),default);
            await using var passagens=db.Source.CreateCommand("SELECT count(*) FROM \"HistoricoPassagens\" WHERE \"Ordem\"=@ordem");
            passagens.Parameters.AddWithValue("ordem",ordem); Assert.Equal(0L,await passagens.ExecuteScalarAsync());
        }
        finally { await db.Redis.GetDatabase().KeyDeleteAsync([ViagemObservadaRepository.ChaveVeiculoViagem(ordem),stream]); }
    }
}
