using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NoPonto.Tests;

public sealed class ViagemOperacionalRegraTests
{
    [Fact]
    public void Modelo_SnapshotSincronizado()
    {
        using var context = new TransporteDbContext(new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql("Host=localhost;Database=teste;Username=teste;Password=teste", p=>p.UseNetTopologySuite()).Options);
        var snapshot=context.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var initialized=context.GetService<IModelRuntimeInitializer>().Initialize(snapshot,designTime:true);
        var differences=context.GetService<IMigrationsModelDiffer>().GetDifferences(initialized.GetRelationalModel(),context.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.True(differences.Count==0,string.Join("; ",differences.Select(d=>d.GetType().Name+":"+d.GetType().GetProperty("Name")?.GetValue(d)+":"+d.GetType().GetProperty("Table")?.GetValue(d))));
    }
    private readonly Guid _r1 = Guid.NewGuid(), _r2 = Guid.NewGuid(), _linha = Guid.NewGuid(), _s1 = Guid.NewGuid(), _s2 = Guid.NewGuid();
    private readonly DateTimeOffset _t = DateTimeOffset.UtcNow.AddMinutes(-5);
    private EstruturaViagem E(bool novo = false, bool ambiguo = false) => new(novo ? _r2 : _r1, _linha, novo ? _s2 : _s1, "L3", !ambiguo);
    private PosicaoVeiculoDto G(int segundos, double p = .8, bool novo = false) => new() {
        Ordem = "REGRA3", CodigoLinha = "L3", ItinerarioId = novo ? _r2 : _r1, TimestampGps = _t.AddSeconds(segundos), PosicaoNaRota = p, Velocidade = 20 };
    private OcorrenciaParada P(int ordem = 10, double p = .8) => new(Guid.NewGuid(), _r1, Guid.NewGuid(), ordem, p);
    private static TransicaoParadas T(OcorrenciaParada? cursor = null, IReadOnlyList<OcorrenciaParada>? passagens = null, OcorrenciaParada? terminal = null) =>
        new(ViagemObservadaStatus.Updated, cursor?.Id ?? Guid.Empty, cursor?.Ordem ?? 0, passagens ?? [], null, terminal);
    private ViagemOperacionalState Inicial() => ViagemOperacionalRegra.Decidir(null, E(), G(0, .2), T(), Guid.NewGuid()).Estado;
    private ViagemOperacionalState Possivel()
    {
        var terminal = P();
        return ViagemOperacionalRegra.Decidir(Inicial(), E(), G(10), T(terminal, [terminal], terminal), Guid.NewGuid()).Estado;
    }
    private ViagemOperacionalState Finalizada()
    {
        var s = Possivel();
        var p = new OcorrenciaParada(s.Observada.UltimaParadaItinerarioId, _r1, Guid.NewGuid(), 10, .8);
        s = ViagemOperacionalRegra.Decidir(s, E(), G(20), T(p, terminal:p), Guid.NewGuid()).Estado;
        return ViagemOperacionalRegra.Decidir(s, E(), G(30), T(p, terminal:p), Guid.NewGuid()).Estado;
    }

    [Fact]
    public void Baseline_CriaAtiva_EmiteSomenteInicio()
    {
        var terminal = P();
        var d = ViagemOperacionalRegra.Decidir(null, E(), G(0), T(terminal, terminal:terminal), Guid.NewGuid());
        Assert.Equal(EstadoViagem.Ativa, d.Estado.Estado);
        Assert.Equal(terminal.Id, d.Estado.Observada.UltimaParadaItinerarioId);
        Assert.Equal("ViagemIniciada", Assert.Single(d.Eventos).Tipo);
    }

    [Fact]
    public void Terminal_T0T1T2_FinalizaSemV2_ReconstrucaoCodecPreservaContador()
    {
        var initial = Inicial();
        var terminal = P(p:.6);
        var d0 = ViagemOperacionalRegra.Decidir(initial, E(), G(10,.7), T(terminal,[terminal],terminal), Guid.NewGuid());
        Assert.Equal(EstadoViagem.PossivelFim, d0.Estado.Estado);
        Assert.Equal(0, d0.Estado.ConfirmacoesPosTerminal);
        Assert.Equal("PassagemParada", Assert.Single(d0.Eventos).Tipo);
        ViagemOperacionalState Restart(ViagemOperacionalState s) => ViagemOperacionalCodec.Decode(
            ViagemOperacionalCodec.Names.Zip(ViagemOperacionalCodec.Encode(s)).ToDictionary(x=>x.First,x=>x.Second), "REGRA3");
        var d1 = ViagemOperacionalRegra.Decidir(Restart(d0.Estado), E(), G(20,.71), T(terminal,terminal:terminal), Guid.NewGuid());
        Assert.Equal(EstadoViagem.PossivelFim,d1.Estado.Estado);
        Assert.Equal(1,d1.Estado.ConfirmacoesPosTerminal);
        Assert.Empty(d1.Eventos);
        var d2 = ViagemOperacionalRegra.Decidir(Restart(d1.Estado), E(), G(30,.72), T(terminal,terminal:terminal), Guid.NewGuid());
        Assert.Equal(EstadoViagem.Finalizada,d2.Estado.Estado);
        Assert.Equal(G(30).TimestampGps,d2.Estado.TimestampFim);
        Assert.Equal("ViagemFinalizada",Assert.Single(d2.Eventos).Tipo);
    }

    [Fact]
    public void PrimeiroR2_CancelaTerminal_SemFinalizacaoPrematura()
    {
        var v1 = Possivel();
        var primeiro = ViagemOperacionalRegra.Decidir(v1,E(true),G(20,.1,true),T(),Guid.NewGuid());
        Assert.Equal(v1.Observada.ViagemId,primeiro.Estado.Observada.ViagemId);
        Assert.Equal(EstadoViagem.Ativa,primeiro.Estado.Estado);
        Assert.Equal(0,primeiro.Estado.ConfirmacoesPosTerminal);
        Assert.Null(primeiro.Estado.Candidato);
        Assert.Empty(primeiro.Eventos);
    }

    [Fact]
    public void Finalizada_PrimeiroR2_Candidato_SegundoR2_CriaV2SemRetroativos()
    {
        var primeiro = ViagemOperacionalRegra.Decidir(Finalizada(),E(true),G(40,.1,true),T(),Guid.NewGuid());
        var nova = Guid.NewGuid();
        var segundo = ViagemOperacionalRegra.Decidir(primeiro.Estado,E(true),G(50,.15,true),T(),nova);
        Assert.Equal(nova,segundo.Estado.Observada.ViagemId);
        Assert.Equal(_r2,segundo.Estado.Observada.ItinerarioId);
        Assert.Equal(EstadoViagem.Ativa,segundo.Estado.Estado);
        Assert.Null(segundo.Estado.Candidato);
        Assert.Equal("ViagemIniciada",Assert.Single(segundo.Eventos).Tipo);
        Assert.Equal(G(50).TimestampGps,segundo.Estado.Observada.TimestampObservacaoInicial);
    }

    [Theory]
    [InlineData(180,true)]
    [InlineData(181,false)]
    public void Candidato_Janela180Segundos(int intervalo,bool confirma)
    {
        var first = ViagemOperacionalRegra.Decidir(Finalizada(),E(true),G(40,.1,true),T(),Guid.NewGuid());
        Assert.Empty(first.Eventos);
        var second = ViagemOperacionalRegra.Decidir(first.Estado,E(true),G(40+intervalo,.2,true),T(),Guid.NewGuid());
        Assert.Equal(confirma,second.Estado.Estado==EstadoViagem.Ativa);
        Assert.Equal(confirma?1:0,second.Eventos.Count);
    }

    [Theory]
    [InlineData("linha")]
    [InlineData("sentido")]
    [InlineData("ambiguo")]
    [InlineData("r1")]
    public void CandidatoIncompativel_NaoCriaELimpa(string caso)
    {
        var first = ViagemOperacionalRegra.Decidir(Finalizada(),E(true),G(40,.1,true),T(),Guid.NewGuid());
        var estrutura = E(true);
        var gps = G(50,.2,true);
        estrutura = caso switch { "linha" => estrutura with { LinhaId=Guid.NewGuid() },
            "sentido"=>estrutura with{SentidoId=_s1},"ambiguo"=>estrutura with{SentidoInequivoco=false}, _=>E() };
        if(caso=="r1") gps=G(50);
        var d=ViagemOperacionalRegra.Decidir(first.Estado,estrutura,gps,T(),Guid.NewGuid());
        Assert.Equal(EstadoViagem.Finalizada,d.Estado.Estado);
        Assert.Equal(first.Estado.Observada.ViagemId,d.Estado.Observada.ViagemId);
        Assert.Null(d.Estado.Candidato);
        Assert.Empty(d.Eventos);
    }

    [Fact]
    public void MultiplasOcorrencias_ParadaFisicaRepetida_PosicaoIgual_OrdenacaoETimestamps()
    {
        var stop = Guid.NewGuid();
        var a=P(1,.4) with{ParadaId=stop}; var b=P(2,.4) with{ParadaId=stop}; var c=P(3,.6);
        var d=ViagemOperacionalRegra.Decidir(Inicial(),E(),G(100,.8),T(c,[c,b,a],c),Guid.NewGuid());
        Assert.Equal(new[]{1,2,3},d.Eventos.Select(e=>e.Ordem!.Value));
        Assert.Equal(3,d.Eventos.Select(e=>e.EventId).Distinct().Count());
        Assert.Equal(d.Eventos[0].TimestampPassagem,d.Eventos[1].TimestampPassagem);
        Assert.Equal(_t.AddTicks((long)Math.Round(100*TimeSpan.TicksPerSecond/3.0)),d.Eventos[0].TimestampPassagem);
        Assert.True(d.Eventos[1].TimestampPassagem<=d.Eventos[2].TimestampPassagem);
        Assert.All(d.Eventos,EventoViagemValidator.Validar);
    }

    [Theory]
    [InlineData(181,.8,.4)]
    [InlineData(10,.1,.4)]
    [InlineData(10,.2,.4)]
    [InlineData(10,.8,.9)]
    public void InterpolacaoInvalida_UsaGps(int segundos,double p,double parada) =>
        Assert.Equal(G(segundos).TimestampGps,ViagemOperacionalRegra.TimestampPassagem(Inicial().Observada,G(segundos,p),parada));

    [Fact]
    public void Wrap_NaoCriaViagem_NaoZeraCursor()
    {
        var s=Possivel();
        var d=ViagemOperacionalRegra.Decidir(s,E(),G(20,.05),T(new(s.Observada.UltimaParadaItinerarioId,_r1,Guid.NewGuid(),10,.8)),Guid.NewGuid());
        Assert.Equal(s.Observada.ViagemId,d.Estado.Observada.ViagemId);
        Assert.Equal(10,d.Estado.Observada.UltimaParadaOrdem);
        Assert.Equal(EstadoViagem.Ativa,d.Estado.Estado);
        Assert.Empty(d.Eventos);
    }

    [Fact]
    public void SaidaTerminal_Reseta_RetornoExigeNovoCiclo_EventoFinalUnico()
    {
        var s = Possivel();
        var terminal = new OcorrenciaParada(s.Observada.UltimaParadaItinerarioId,_r1,Guid.NewGuid(),10,.8);
        s = ViagemOperacionalRegra.Decidir(s,E(),G(20),T(terminal,terminal:terminal),Guid.NewGuid()).Estado;
        Assert.Equal(1,s.ConfirmacoesPosTerminal);
        var fora = T(terminal,terminal:terminal) with { Proxima = P(11,.9) };
        s = ViagemOperacionalRegra.Decidir(s,E(),G(30),fora,Guid.NewGuid()).Estado;
        Assert.Equal(EstadoViagem.Ativa,s.Estado);
        Assert.Equal(0,s.ConfirmacoesPosTerminal);
        s = ViagemOperacionalRegra.Decidir(s,E(),G(40),T(terminal,terminal:terminal),Guid.NewGuid()).Estado;
        Assert.Equal(EstadoViagem.PossivelFim,s.Estado);
        Assert.Equal(0,s.ConfirmacoesPosTerminal);
        s = ViagemOperacionalRegra.Decidir(s,E(),G(50),T(terminal,terminal:terminal),Guid.NewGuid()).Estado;
        Assert.Equal(1,s.ConfirmacoesPosTerminal);
        Assert.Equal(EstadoViagem.PossivelFim,s.Estado);
        var fim = ViagemOperacionalRegra.Decidir(s,E(),G(60),T(terminal,terminal:terminal),Guid.NewGuid());
        Assert.Equal("ViagemFinalizada",Assert.Single(fim.Eventos).Tipo);
        Assert.Empty(ViagemOperacionalRegra.Decidir(fim.Estado,E(),G(70),T(terminal,terminal:terminal),Guid.NewGuid()).Eventos);
    }
}
