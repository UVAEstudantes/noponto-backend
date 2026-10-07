using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class CancelamentoCandidatoCursorTests
{
    [Theory]
    [InlineData(.25,2)]
    [InlineData(.45,3)]
    public void AlvoMlAntesDoCandidato_BaselineSemRetroativos_PrimeiroEventoUmaOuDuasOrdensDepois(
        double retorno,int primeiraOrdem)
    {
        var paradas=Enumerable.Range(1,4).Select(n=>new OcorrenciaParada(
            Guid.NewGuid(),A.PadraoVersaoId,Guid.NewGuid(),n,n*.2)).ToArray();
        var g0=G(A,0,.1);
        var inicial=ViagemOperacionalRegra.Decidir(null,A,g0,
            new(ViagemObservadaStatus.Updated,Guid.Empty,0,[],paradas[0]),Guid.NewGuid());
        var resultado=new ViagemObservadaResultado(ViagemObservadaStatus.Created,inicial.Estado.Observada)
            {EstadoOperacional=inicial.Estado,ProximaOcorrenciaOperacional=paradas[0]};
        var ml=EventoTelemetriaMlFactory.Criar(g0,resultado,T);
        Assert.Equal(paradas[0].Id,ml.ProximaOcorrenciaParadaPadraoId);
        var divergente=G(B,10,.1);
        var avaliacao=ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(
            new(){MudancaOperacionalHabilitada=true},inicial.Estado,B,divergente)!;
        var candidato=ViagemOperacionalRegra.AplicarAvaliacaoMudanca(inicial.Estado,avaliacao,divergente)!.Estado;
        var volta=G(A,20,retorno);
        Assert.Equal(StatusMudancaOperacional.CandidatoCancelado,
            ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(
                new(){MudancaOperacionalHabilitada=true},candidato,A,volta)!.Status);
        var posicaoBaseline=ViagemOperacionalRepository.PosicaoInicialTransicao(
            candidato,A.PadraoVersaoId,retorno,true,true);
        // Modelo de retorno da consulta baseline: SQL real nao e executado neste teste.
        var cursor=paradas.Last(p=>p.PosicaoLinha<=posicaoBaseline);
        var proxima=paradas.First(p=>p.Ordem>cursor.Ordem);
        var adotada=ViagemOperacionalRegra.Decidir(candidato,A,volta,
            new(ViagemObservadaStatus.Updated,cursor.Id,cursor.Ordem,[],proxima),Guid.NewGuid(),true);
        Assert.Empty(adotada.Eventos);
        Assert.Equal(inicial.Estado.Observada.ViagemId,adotada.Estado.Observada.ViagemId);
        var mlDepois=EventoTelemetriaMlFactory.Criar(volta,
            new ViagemObservadaResultado(ViagemObservadaStatus.Updated,adotada.Estado.Observada)
                {EstadoOperacional=adotada.Estado,ProximaOcorrenciaOperacional=proxima},T.AddSeconds(20));
        Assert.Equal(proxima.Id,mlDepois.ProximaOcorrenciaParadaPadraoId);
        Assert.NotEqual(ml.ProximaOcorrenciaParadaPadraoId,mlDepois.ProximaOcorrenciaParadaPadraoId);
        var futura=ViagemOperacionalRegra.Decidir(adotada.Estado,A,
            G(A,30,proxima.PosicaoLinha+.01),
            new(ViagemObservadaStatus.Updated,proxima.Id,proxima.Ordem,[proxima]),Guid.NewGuid());
        var evento=Assert.Single(futura.Eventos);
        Assert.Equal(primeiraOrdem,evento.Ordem);
        Assert.Equal("PassagemParada",evento.Tipo);
        Assert.Equal(ml.ViagemId,evento.ViagemId);
    }
    private static readonly DateTimeOffset T = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly EstruturaViagem A = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "A", true, Guid.NewGuid());
    private static readonly EstruturaViagem B = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "B", true, Guid.NewGuid());
    private static readonly Guid[] Stops = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
    private static PosicaoVeiculoDto G(EstruturaViagem e, int segundos, double p) => new()
    {
        Ordem="CURSOR", CodigoLinha=e.CodigoLinha, LinhaId=e.LinhaId, SentidoId=e.SentidoId,
        PadraoVersaoId=e.PadraoVersaoId, PadraoOperacionalId=e.PadraoOperacionalId,
        TopologiaPadrao=e.Topologia, PosicaoNaRota=p, ComprimentoRotaMetros=1000,
        TimestampGps=T.AddSeconds(segundos), Latitude=-22.9, Longitude=-43.2,
        MatchingOperacionalPlausivel=true
    };
    private static ViagemOperacionalState Candidato(string topologia="LINEAR", int volta=0)
    {
        var a=A with { Topologia=topologia };
        var s=ViagemOperacionalRegra.Decidir(null,a,G(a,0,.25),
            new(ViagemObservadaStatus.Updated,Stops[0],1,[],Volta:volta),Guid.NewGuid()).Estado;
        var g=G(B,10,.1);
        var avaliacao=ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(
            new(){MudancaOperacionalHabilitada=true},s,B,g)!;
        return ViagemOperacionalRegra.AplicarAvaliacaoMudanca(s,avaliacao,g)!.Estado;
    }

    [Theory]
    [InlineData(.25,1)]
    [InlineData(.39,1)]
    [InlineData(.55,2)]
    [InlineData(.65,3)]
    public void Cancelamento_ConsultaBaselineNaPosicaoAdotada(double retorno,int ordem)
    {
        var s=Candidato(); var g=G(A,20,retorno);
        var a=ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(new(){MudancaOperacionalHabilitada=true},s,A,g)!;
        Assert.Equal(StatusMudancaOperacional.CandidatoCancelado,a.Status);
        // Verifica o argumento REAL da consulta. PostgreSQL incorpora posições <= @anterior.
        var inicio=ViagemOperacionalRepository.PosicaoInicialTransicao(s,A.PadraoVersaoId,retorno,true,true);
        Assert.Equal(retorno,inicio);
        // Modelo lógico do baseline; execução SQL é coberta pelo teste da fixture real.
        var incorporadas=new[]{.2,.4,.6}.Count(p=>p<=inicio);
        Assert.Equal(ordem,incorporadas);
        var d=ViagemOperacionalRegra.Decidir(s,A,g,
            new(ViagemObservadaStatus.Updated,Stops[incorporadas-1],incorporadas,[]),Guid.NewGuid(),true);
        Assert.Equal(retorno,d.Estado.Observada.PosicaoNaRotaConfirmada);
        Assert.Equal(Stops[ordem-1],d.Estado.Observada.OcorrenciaCursorId);
        Assert.Equal(ordem,d.Estado.Observada.OrdemCursor);
        Assert.Equal(s.Observada.ViagemId,d.Estado.Observada.ViagemId);
        Assert.Null(d.Estado.Candidato);
        Assert.Empty(d.Eventos);
        var nova=G(A,30,(ordem+1)*.2+.01);
        if(ordem<3)
        {
            var parada=new OcorrenciaParada(Stops[ordem],A.PadraoVersaoId,Guid.NewGuid(),ordem+1,(ordem+1)*.2);
            var futura=ViagemOperacionalRegra.Decidir(d.Estado,A,nova,
                new(ViagemObservadaStatus.Updated,parada.Id,parada.Ordem,[parada]),Guid.NewGuid());
            Assert.Single(futura.Eventos);
            Assert.Equal(parada.Id,futura.Eventos[0].OcorrenciaParadaPadraoId);
            Assert.Equal(d.Estado.Observada.ViagemId,futura.Eventos[0].ViagemId);
        }
    }

    [Fact]
    public void FlagDesligadaOuSemBaseline_PreservaPosicaoAnterior()
    {
        var s=Candidato();
        Assert.Equal(.25,ViagemOperacionalRepository.PosicaoInicialTransicao(s,A.PadraoVersaoId,.55,true,false));
        Assert.Equal(.25,ViagemOperacionalRepository.PosicaoInicialTransicao(s,A.PadraoVersaoId,.55,false,true));
        Assert.Equal(.25,ViagemOperacionalRepository.PosicaoInicialTransicao(s with { Candidato=null },A.PadraoVersaoId,.55,true,true));
    }

    [Fact]
    public void CircularBaseline_PreservaVoltaEIdentidade()
    {
        var s=Candidato("CIRCULAR",2); var e=A with { Topologia="CIRCULAR" };
        var p=ViagemOperacionalRepository.PosicaoInicialTransicao(s,A.PadraoVersaoId,.95,true,true);
        Assert.Equal(.95,p);
        var d=ViagemOperacionalRegra.Decidir(s,e,G(e,20,p),
            new(ViagemObservadaStatus.Updated,Stops[2],3,[],Volta:2),Guid.NewGuid(),true);
        Assert.Equal(2,d.Estado.Observada.Volta);
        Assert.Equal(2950,d.Estado.Observada.ProgressoAbsolutoMetros);
        Assert.Empty(d.Eventos);
        var parada=new OcorrenciaParada(Stops[0],A.PadraoVersaoId,Guid.NewGuid(),1,.2,Volta:3);
        var volta=ViagemOperacionalRegra.Decidir(d.Estado,e,G(e,30,.21),
            new(ViagemObservadaStatus.Updated,parada.Id,1,[parada],Volta:3,HouveWrap:true),Guid.NewGuid());
        Assert.Equal(3,volta.Estado.Observada.Volta);
        Assert.Single(volta.Eventos);
        Assert.Equal(3,volta.Eventos[0].Volta);
    }

    [Fact]
    public void RetornoVersaoGeometrica_UsaPosicaoDaProjecaoOperacional()
    {
        var s=Candidato(); var nova=A with { PadraoVersaoId=Guid.NewGuid() };
        var a=ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(new(){MudancaOperacionalHabilitada=true},s,nova,G(nova,20,.8))!;
        Assert.Equal(StatusMudancaOperacional.CandidatoCancelado,a.Status);
        // Repositório resolve versão divergente pela projeção na operação antiga antes da consulta.
        Assert.Equal(.55,ViagemOperacionalRepository.PosicaoInicialTransicao(s,A.PadraoVersaoId,.55,true,true));
    }

    [Fact]
    public void DuasDivergencias_RetornoCancelaCandidatoSubstituido()
    {
        var s=Candidato(); var c=B with { PadraoVersaoId=Guid.NewGuid(),LinhaId=Guid.NewGuid() };
        var g=G(c,20,.3);
        var a=ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(new(){MudancaOperacionalHabilitada=true},s,c,g)!;
        Assert.Equal(StatusMudancaOperacional.CandidatoSubstituido,a.Status);
        s=ViagemOperacionalRegra.AplicarAvaliacaoMudanca(s,a,g)!.Estado;
        Assert.Equal(StatusMudancaOperacional.CandidatoCancelado,
            ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(new(){MudancaOperacionalHabilitada=true},s,A,G(A,30,.55))!.Status);
        Assert.Equal(.55,ViagemOperacionalRepository.PosicaoInicialTransicao(s,A.PadraoVersaoId,.55,true,true));
    }
}
