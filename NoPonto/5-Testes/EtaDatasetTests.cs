using NoPonto.Application.GPS;
using NoPonto.Domain.Entities;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class EtaDatasetTests
{
    [Fact]
    public void ViagemConfiavelSemAlvoOperacional_NaoAutorizaLabelPeloFallback()
    {
        var(c,e,o)=Caso();
        c.Gps.ProximaOcorrenciaParadaPadraoId=null;
        // Mesmo com viagem/volta e fallback coincidente com a passagem,
        // nao inventar o alvo operacional que esta ausente na observacao.
        Assert.Equal(e.ViagemId,c.Gps.ViagemId);
        Assert.Equal(c.Passagem.OcorrenciaParadaPadraoId,c.Gps.OcorrenciaParadaPadraoId);
        var(a,m)=EtaDataset.Avaliar(c,e,o);
        Assert.Null(a);Assert.Equal("DadosIncompletos",m);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlvoExplicitoComCampoGeralDivergenteOuNulo_RejeitaContratoDaFactory(bool nulo)
    {
        var(c,e,o)=Caso();
        Assert.NotNull(EtaDataset.Avaliar(c,e,o).Amostra);
        // Adulteracao isolada: factory atual copia o alvo explicito neste campo.
        c.Gps.OcorrenciaParadaPadraoId=nulo ? null : Guid.NewGuid();
        Assert.Equal(c.Destino.Id,c.Gps.ProximaOcorrenciaParadaPadraoId);
        Assert.Equal(c.Destino.Id,c.Passagem.OcorrenciaParadaPadraoId);
        var(a,m)=EtaDataset.Avaliar(c,e,o);
        Assert.Null(a);Assert.Equal("IdentidadeEstruturalIncompativel",m);
    }
    [Fact] public void Cutoff_RejeitaViagemParcialMesmoComGpsNovo()
    {
        var(c,e,o)=Caso();
        Assert.Equal("ExecucaoForaIntervalo",EtaDataset.Avaliar(c,e with{Inicio=o.Inicio.AddTicks(-1)},o).Motivo);
        Assert.NotNull(EtaDataset.Avaliar(c,e with{Inicio=o.Inicio},o).Amostra);
        Assert.Equal("ExecucaoForaIntervalo",EtaDataset.Avaliar(c,e with{Fim=o.Fim},o).Motivo);
    }
    [Fact]
    public void PreparacaoPostgres_PassagemSatisfazValidatorProdutivo()
    {
        var ts=new DateTimeOffset(2026,10,7,12,0,0,TimeSpan.Zero);
        var e=EtaDatasetPostgresTests.CriarPassagem(Guid.NewGuid(),"DATASET-LOCAL",Guid.NewGuid(),
            Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),ts);
        EventoViagemValidator.Validar(e);
        Assert.Equal(ts.AddSeconds(60),e.TimestampEvento);
        Assert.Equal(e.TimestampPassagem,e.TimestampEvento);
        Assert.Equal(ts.AddSeconds(70),e.TimestampGps);
        // O formato antigo da preparação deve continuar rejeitado pelo validator produtivo.
        var erro=Assert.Throws<FormatException>(()=>EventoViagemValidator.Validar(
            e with{TimestampEvento=ts.AddSeconds(70)}));
        Assert.Equal("Passagem estrutural incompleta.",erro.Message);
    }
    private static (CandidatoDatasetEta C,ExecucaoDatasetEta E,OpcoesDatasetEta O) Caso()
    {
        var t=new DateTimeOffset(2026,10,10,12,0,0,TimeSpan.Zero);
        var viagem=Guid.NewGuid(); var v=Guid.NewGuid();var sentido=Guid.NewGuid();var linha=Guid.NewGuid();
        var ocorrencia=Guid.NewGuid();var parada=Guid.NewGuid();
        var g=new TelemetriaVeiculoMl{ObservacaoId=new string('a',64),Modal="ONIBUS",Provedor="SPPO",OrdemVeiculo="A1",
            CodigoLinha="292",OrigemPosicao="REAL",ViagemId=viagem,Volta=0,SentidoId=sentido,LinhaId=linha,
            PadraoVersaoId=v,OcorrenciaParadaPadraoId=ocorrencia,ProximaOcorrenciaParadaPadraoId=ocorrencia,
            TimestampGps=t,PosicaoNaRota=.2,ComprimentoRotaMetros=1000,DistanciaProximaParadaMetros=200,
            VelocidadeInstantanea=20,VelocidadeMediaCausal=18};
        var h=new HistoricoPassagem{Ordem="A1",CodigoLinha="292",ViagemId=viagem,Volta=0,SentidoId=sentido,
            PadraoVersaoId=v,OcorrenciaParadaPadraoId=ocorrencia,ParadaId=parada,PosicaoNaRota=.4,
            TimestampPassagem=t.AddSeconds(60),TimestampGps=t.AddSeconds(70)};
        var d=new DestinoDatasetEta(ocorrencia,parada,v,Guid.NewGuid(),linha,sentido,"292","LINEAR",.4);
        var j=new EventoViagem($"passagem:{viagem:D}:{ocorrencia:D}:0","PassagemParada",viagem,"A1","292",sentido,v,
            h.TimestampGps,ocorrencia,parada,1,.4,h.TimestampPassagem,h.TimestampGps,
            PadraoOperacionalId:d.PadraoId,Volta:0,LinhaId:linha);
        var c=new CandidatoDatasetEta(g,h,d,j,200);
        var e=new ExecucaoDatasetEta(viagem,t.AddMinutes(-10),t.AddMinutes(10),
            QualidadeExecucaoDataset.AuditadaSemProtecao,"manifesto-fixture-sintetica",[c]);
        var o=new OpcoesDatasetEta(t.AddDays(-1),t.AddDays(10),t.AddDays(2),t.AddDays(5));
        return(c,e,o);
    }
    [Fact] public void Valida_LabelGpsEHoraFortaleza()
    {
        var(c,e,o)=Caso();var(a,m)=EtaDataset.Avaliar(c,e,o);
        Assert.Null(m);Assert.Equal(60,a!.LabelSegundos);Assert.Equal(200,a.DistanciaMetros);
        Assert.Equal(9,a.HoraDia);Assert.Equal("TRAIN",a.Split);
    }
    [Theory]
    [InlineData("viagem","ExecucaoDiferente")][InlineData("volta","VoltaDiferente")]
    [InlineData("sentido","IdentidadeEstruturalIncompativel")][InlineData("ocorrencia","IdentidadeEstruturalIncompativel")]
    [InlineData("tempo","TempoInvalido")][InlineData("incompleto","DadosIncompletos")]
    [InlineData("distancia","DistanciaNaoConferida")][InlineData("versao","IdentidadeEstruturalIncompativel")]
    [InlineData("atras","DestinoNaoAdianteOuDistanciaInvalida")]
    public void RejeitaIdentidadeTempoEQualidade(string caso,string motivo)
    {
        var(c,e,o)=Caso();
        switch(caso){case "viagem":c.Passagem.ViagemId=Guid.NewGuid();break;
            case "volta":c.Passagem.Volta=1;break;case "sentido":c.Passagem.SentidoId=Guid.NewGuid();break;
            case "ocorrencia":c.Passagem.OcorrenciaParadaPadraoId=Guid.NewGuid();break;
            case "tempo":c.Passagem.TimestampPassagem=c.Gps.TimestampGps.AddSeconds(-1);break;
            case "incompleto":c.Gps.ProximaOcorrenciaParadaPadraoId=null;break;
            case "distancia":c=c with{DistanciaRotaConferidaMetros=2000};break;
            case "versao":c.Passagem.PadraoVersaoId=Guid.NewGuid();break;
            case "atras":c.Gps.PosicaoNaRota=.5;break;}
        // Alterar o histórico exige journal correspondente para isolar a regra de associação GPS.
        c=c with{Journal=c.Journal! with{ViagemId=c.Passagem.ViagemId!.Value,Volta=c.Passagem.Volta,
            SentidoId=c.Passagem.SentidoId!.Value,OcorrenciaParadaPadraoId=c.Passagem.OcorrenciaParadaPadraoId,
            PadraoVersaoId=c.Passagem.PadraoVersaoId!.Value,TimestampPassagem=c.Passagem.TimestampPassagem,
            EventId=$"passagem:{c.Passagem.ViagemId:D}:{c.Passagem.OcorrenciaParadaPadraoId:D}:{c.Passagem.Volta}"}};
        Assert.Equal(motivo,EtaDataset.Avaliar(c,e,o).Motivo);
    }
    [Theory][InlineData(QualidadeExecucaoDataset.NaoVerificada)][InlineData(QualidadeExecucaoDataset.ProtegidaOuAmbigua)]
    public void ProcedenciaOuProtecaoNaoInferidaPorUuid(QualidadeExecucaoDataset qualidade)
    {var(c,e,o)=Caso();Assert.Equal("ProcedenciaNaoAuditadaOuProtegida",EtaDataset.Avaliar(c,e with{Qualidade=qualidade},o).Motivo);}
    [Fact] public void JournalObrigatorio()
    {var(c,e,o)=Caso();Assert.Equal("PassagemSemJournalConferido",EtaDataset.Avaliar(c with{Journal=null},e,o).Motivo);}
    [Fact] public void CircularMesmaVoltaSemWrapArtificial()
    {
        var(c,e,o)=Caso();c=c with{Destino=c.Destino with{Topologia="CIRCULAR"}};
        Assert.NotNull(EtaDataset.Avaliar(c,e,o).Amostra);
        c.Gps.PosicaoNaRota=.95;c.Passagem.PosicaoNaRota=.05;
        c=c with{Destino=c.Destino with{Posicao=.05},Journal=c.Journal! with{PosicaoLinha=.05}};
        Assert.Equal("DestinoNaoAdianteOuDistanciaInvalida",EtaDataset.Avaliar(c,e,o).Motivo);
    }
    [Fact] public void ExecucaoInferidaNovaNaoHerdaLabelsAnteriores()
    {
        var(c,e,o)=Caso();c.Gps.ViagemId=Guid.NewGuid();
        Assert.Equal("ExecucaoDiferente",EtaDataset.Avaliar(c,e,o).Motivo);
        e=e with{ViagemId=c.Gps.ViagemId.Value};c.Passagem.ViagemId=e.ViagemId;
        c=c with{Journal=c.Journal! with{ViagemId=e.ViagemId,EventId=$"passagem:{e.ViagemId:D}:{c.Destino.Id:D}:0"}};
        Assert.NotNull(EtaDataset.Avaliar(c,e,o).Amostra);
    }
    [Fact] public void PurgaViagemQueCruzaFronteiraTemporal()
    {var(c,e,o)=Caso();Assert.Equal("ExecucaoCruzaSplit",EtaDataset.Avaliar(c,e with{Fim=o.FimTreino.AddMinutes(1)},o).Motivo);}
    [Fact] public void FeaturesNaoDependemDaVelocidadeHoraOuDistanciaDaPassagem()
    {
        var(c,e,o)=Caso();var antes=EtaDataset.Avaliar(c,e,o).Amostra!;
        c.Passagem.VelocidadeInstantanea=99;c.Passagem.VelocidadeMedia=98;c.Passagem.HoraDia=23;
        c.Passagem.DiaSemana=0;c.Passagem.DistanciaTrechoMetros=999;c.Passagem.TempoDesdeParadaAnteriorSegundos=999;
        Assert.Equal(antes,EtaDataset.Avaliar(c,e,o).Amostra);
    }
    [Fact] public void VelocidadeInvalidaViraAusenteNaoLabel()
    {var(c,e,o)=Caso();c.Gps.VelocidadeInstantanea=double.NaN;c.Gps.VelocidadeMediaCausal=-1;
        var a=EtaDataset.Avaliar(c,e,o).Amostra!;Assert.Null(a.VelocidadeKmh);Assert.Null(a.VelocidadeMediaCausalKmh);}
    private sealed class Fonte(params PaginaDatasetEta[] paginas):IFonteDatasetEta
    {public int Chamadas;public Task<PaginaDatasetEta> LerPaginaAsync(string? cursor,int limite,CancellationToken ct)
        =>Task.FromResult(paginas[Chamadas++]);}
    [Fact] public async Task PaginaDeduplicacaoCsvEContadores()
    {
        var(c,e,o)=Caso();var outra=e with{ViagemId=Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),Candidatos=[]};
        var f=new Fonte(new([e with{Candidatos=[c,c]}],"2"),new([outra],null));
        using var csv=new StringWriter();var s=await EtaDataset.ExportarCsvAsync(f,csv,o with{ExecucoesPorPagina=1});
        Assert.Equal(2,f.Chamadas);Assert.Equal(2,s.Lidos);Assert.Equal(1,s.Exportados);Assert.Equal(1,s.Descartes["Duplicada"]);
        Assert.Equal(2,csv.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries).Length);
    }
    [Fact] public async Task PaginaRepetidaNaoFragmentaViagem()
    {var(_,e,o)=Caso();using var w=new StringWriter();var f=new Fonte(new([e],"2"),new([e],null));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>EtaDataset.ExportarCsvAsync(f,w,o));}
    [Fact] public async Task LimitesInterrompemExportacaoParcial()
    {var(c,e,o)=Caso();using var w=new StringWriter();var f=new Fonte(new PaginaDatasetEta([e with{Candidatos=[c,c]}],null));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>EtaDataset.ExportarCsvAsync(f,w,o with{MaxCandidatosPorExecucao=1}));}
    [Fact] public async Task CursorSemProgressoRejeitado()
    {var(_,e,o)=Caso();using var w=new StringWriter();var f=new Fonte(new([e],"2"),new([],"2"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>EtaDataset.ExportarCsvAsync(f,w,o));}
    [Fact] public async Task LimiteDePaginasNaoDeclaraSucesso()
    {var(_,e,o)=Caso();using var w=new StringWriter();var f=new Fonte(new PaginaDatasetEta([e],"2"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>EtaDataset.ExportarCsvAsync(f,w,o with{MaxPaginas=1}));}
    [Fact] public void LabelEFuturoNaoEstaoNaAllowlistFeatures()
    {Assert.Empty(EtaDataset.Features.Intersect(EtaDataset.Labels));Assert.DoesNotContain("timestamp_passagem",EtaDataset.Features);
        Assert.DoesNotContain("viagem_id",EtaDataset.Features);Assert.DoesNotContain("split",EtaDataset.Features);}
    [Fact] public void PayloadJournalDivergenteNaoConfirmaPassagem()
    {var(c,e,o)=Caso();Assert.Equal("PassagemSemJournalConferido",EtaDataset.Avaliar(c with{
        Journal=c.Journal! with{TimestampPassagem=c.Passagem.TimestampPassagem!.Value.AddSeconds(1)}},e,o).Motivo);}
    [Theory][InlineData(45.04372787)][InlineData(1000)][InlineData(200)]
    public void DistanciaDiretaNaoSubstituiNemInvalidaTrechoOperacional(double direta)
    {var(c,e,o)=Caso();c.Gps.DistanciaProximaParadaMetros=direta;
        var(a,m)=EtaDataset.Avaliar(c,e,o);Assert.Null(m);Assert.Equal(200,a!.DistanciaMetros);Assert.Equal(60,a.LabelSegundos);}

    [Theory][InlineData(null)][InlineData(0d)][InlineData(-1d)][InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)][InlineData(1011d)]
    public void TrechoInvalidoPermaneceRejeitado(double? trecho)
    {var(c,e,o)=Caso();Assert.Equal("DistanciaNaoConferida",EtaDataset.Avaliar(c with{DistanciaRotaConferidaMetros=trecho},e,o).Motivo);}

    [Fact] public void MatchingDistintoNaoAutorizaAlvoOperacionalIncoerente()
    {var(c,e,o)=Caso();c.Gps.OcorrenciaParadaPadraoId=Guid.NewGuid();
        Assert.Equal("IdentidadeEstruturalIncompativel",EtaDataset.Avaliar(c,e,o).Motivo);
        Assert.Equal("ProcedenciaNaoAuditadaOuProtegida",EtaDataset.Avaliar(c,e with{Qualidade=QualidadeExecucaoDataset.NaoVerificada},o).Motivo);}

    [Fact] public void FracaoNaoSubstituiDistanciaGeograficaConferida()
    {var(c,e,o)=Caso();Assert.Equal("DistanciaNaoConferida",EtaDataset.Avaliar(c with{DistanciaRotaConferidaMetros=null},e,o).Motivo);
        c.Gps.DistanciaProximaParadaMetros=180;
        Assert.Equal(180,EtaDataset.Avaliar(c with{DistanciaRotaConferidaMetros=180},e,o).Amostra!.DistanciaMetros);}
    [Fact] public void PrecisaoTimestampPostgresNaoViraConflitoDePayload()
    {var(c,e,o)=Caso();Assert.NotNull(EtaDataset.Avaliar(c with{Journal=c.Journal! with{
        TimestampPassagem=c.Passagem.TimestampPassagem!.Value.AddTicks(9),TimestampGps=c.Passagem.TimestampGps.AddTicks(9)}},e,o).Amostra);
        Assert.Equal("PassagemSemJournalConferido",EtaDataset.Avaliar(c with{Journal=c.Journal! with{
            TimestampPassagem=c.Passagem.TimestampPassagem!.Value.AddTicks(10)}},e,o).Motivo);}
    [Fact] public async Task DuplicatasComLabelsDivergentesExcluemAmbas()
    {
        var(c,e,o)=Caso();var outra=new HistoricoPassagem{Ordem=c.Passagem.Ordem,CodigoLinha=c.Passagem.CodigoLinha,
            ViagemId=c.Passagem.ViagemId,Volta=0,SentidoId=c.Passagem.SentidoId,PadraoVersaoId=c.Passagem.PadraoVersaoId,
            OcorrenciaParadaPadraoId=c.Destino.Id,ParadaId=c.Destino.ParadaId,PosicaoNaRota=.4,
            TimestampPassagem=c.Passagem.TimestampPassagem!.Value.AddSeconds(1),TimestampGps=c.Passagem.TimestampGps};
        var c2=c with{Passagem=outra,Journal=c.Journal! with{TimestampPassagem=outra.TimestampPassagem}};
        using var w=new StringWriter();var f=new Fonte(new PaginaDatasetEta([e with{Candidatos=[c,c2]}],null));
        var stats=await EtaDataset.ExportarCsvAsync(f,w,o);Assert.Equal(0,stats.Exportados);
        Assert.Equal(2,stats.Descartes["DuplicadaConflitante"]);
    }
}
