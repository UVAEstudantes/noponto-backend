using Microsoft.Extensions.Options;
using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.Radar;
using Xunit;

namespace NoPonto.Tests;

public sealed class RadarPushTextTests
{
    private static readonly DateTimeOffset Now = RadarSamples.Now;

    [Fact]
    public void UmaLinhaUmVeiculoMostraChegadaCurta()
    {
        var (title, body) = RadarPushText.Format(Candidate([Item("838", 4)]), RadarSamples.Session(), Now);
        Assert.Equal("Radar · Fragoso", title);
        Assert.Equal("838 chega em 4 min", body);
    }

    [Fact]
    public void UmaLinhaComTresVeiculosResumeOsOutros()
    {
        var line = Guid.NewGuid();
        var sentido = Guid.NewGuid();
        var body = Body(Item("838", 4, line, sentido), Item("838", 12, line, sentido), Item("838", 27, line, sentido));
        Assert.Equal("838 · Próximo em 4 min\nOutros veículos em 12 e 27 min", body);
    }

    [Fact]
    public void MultiplasSaidasUsamTextoExplicitoDeSaidaPrevista()
    {
        var line = Guid.NewGuid();
        var sentido = Guid.NewGuid();
        var body = Body(Item("838", 4, line, sentido, TiposEventoParada.Departure),
            Item("838", 12, line, sentido, TiposEventoParada.Departure),
            Item("838", 27, line, sentido, TiposEventoParada.Departure));
        Assert.Equal("838 · Saída prevista em 4 min\nOutras saídas em 12 e 27 min", body);
        Assert.DoesNotContain("Próximo", body);
    }

    [Fact]
    public void ChegadaESaidaDaMesmaLinhaNaoSaoAgrupadas()
    {
        var line = Guid.NewGuid();
        var sentido = Guid.NewGuid();
        Assert.Equal("838 em 3 min · 838 saída prevista em 7 min",
            Body(Item("838", 3, line, sentido), Item("838", 7, line, sentido, TiposEventoParada.Departure)));
    }

    [Fact]
    public void VeiculosSimultaneosMistosDiferenciamChegadasESaidas()
    {
        var candidate = Candidate([
            Item("838", 3), Item("855", 3, type: TiposEventoParada.Departure), Item("884", 3)
        ]);
        var body = RadarPushText.Format(candidate, RadarSamples.Session(), Now).Body;
        Assert.Equal("2 chegadas e 1 saída previstas em 3 min\n838 chegada · 855 saída · 884 chegada", body);
    }

    [Fact]
    public void SaidasSimultaneasNaoSaoApresentadasComoOnibusPrevistos()
    {
        var candidate = Candidate([
            Item("838", 3, type: TiposEventoParada.Departure),
            Item("855", 3, type: TiposEventoParada.Departure),
            Item("884", 3, type: TiposEventoParada.Departure)
        ]);
        var body = RadarPushText.Format(candidate, RadarSamples.Session(), Now).Body;
        Assert.Equal("3 saídas previstas em 3 min\n838 · 855 · 884", body);
        Assert.DoesNotContain("ônibus", body);
    }

    [Fact]
    public void VariasLinhasOrdenamEDividemEmDuasLinhasDeTexto()
    {
        Assert.Equal("838 em 3 min · 855 em 7 min\n884 em 11 min",
            Body(Item("838", 3), Item("855", 7), Item("884", 11)));
    }

    [Fact]
    public void MuitasLinhasResumemAsNaoSelecionadasSemListarTodas()
    {
        var candidate = Candidate([Item("838", 3), Item("855", 7), Item("884", 11)], 17, 2);
        var body = RadarPushText.Format(candidate, RadarSamples.Session(), Now).Body;
        Assert.Equal("838 em 3 min · 855 em 7 min\n884 em 11 min\n+2 linhas acompanhadas", body);
    }

    [Fact]
    public void CincoVeiculosNoMesmoHorarioNaoInventamEtaDosNaoSelecionados()
    {
        var candidate = Candidate([Item("838", 3), Item("855", 3), Item("884", 3)], 2, 2);
        var body = RadarPushText.Format(candidate, RadarSamples.Session(), Now).Body;
        Assert.Equal("3 ônibus previstos em 3 min\n838 · 855 · 884 · +2 veículos", body);
        Assert.DoesNotContain("5 ônibus", body);
    }

    [Fact]
    public void PrioridadeMantemOrdemDaSelecaoSemRotuloTecnico()
    {
        var body = Body(Item("855", 3, priority: true), Item("838", 7));
        Assert.Equal("855 em 3 min · 838 em 7 min", body);
        Assert.DoesNotContain("prioritária", body);
    }

    [Fact]
    public void VeiculoSemEtaNaoRecebeTextoInventado()
    {
        var session = RadarSamples.Session();
        var eventWithoutEta = RadarSamples.Event(session.ParadaId, session.LinhasSelecionadas[0]) with
        {
            EstimatedAt = null, ScheduledAt = null, SecondsUntilEvent = null
        };
        var candidate = Select(session, [eventWithoutEta]);
        var body = RadarPushText.Format(candidate, session, Now).Body;
        Assert.Equal("Nenhuma chegada prevista nos próximos 10 min", body);
        Assert.DoesNotContain("Sem previsão", body);
    }

    [Fact]
    public void TerminalSemSaidaConfirmadaMostraApenasSaidaPrevista()
    {
        var session = RadarSamples.Session();
        var departure = RadarSamples.Event(session.ParadaId, session.LinhasSelecionadas[0]) with
        {
            EventType = TiposEventoParada.Departure, StationRole = PapeisEstacaoEvento.Origin,
            NextVehiclesMode = ModosProximosVeiculos.Departures
        };
        var candidate = Select(session, [departure]);
        var body = RadarPushText.Format(candidate, session, Now).Body;
        Assert.Equal("884 saída prevista em 2 min", body);
        Assert.DoesNotContain("aguardando", body);
        Assert.DoesNotContain("saindo", body);
        Assert.False(candidate.PresencaTerminalDisponivel);
    }

    [Fact]
    public void EvidenciaRecenteNaoTransformaPrevisaoEmSaidaConfirmada()
    {
        var session = RadarSamples.Session();
        var departure = RadarSamples.Event(session.ParadaId, session.LinhasSelecionadas[0]) with
        {
            EventType = TiposEventoParada.Departure, StationRole = PapeisEstacaoEvento.Origin,
            NextVehiclesMode = ModosProximosVeiculos.Departures, LastRealtimeEvidenceUtc = Now.AddSeconds(-1)
        };
        var body = RadarPushText.Format(Select(session, [departure]), session, Now).Body;
        Assert.Equal("884 saída prevista em 2 min", body);
        Assert.DoesNotContain("confirmada", body);
        Assert.DoesNotContain("saiu", body);
    }

    [Fact]
    public void SemChegadasUsaJanelaRealDaSessao()
    {
        var session = RadarSamples.Session() with { AntecedenciaMinutos = 15 };
        var body = RadarPushText.Format(Candidate([]), session, Now).Body;
        Assert.Equal("Nenhuma chegada prevista nos próximos 15 min", body);
    }

    [Fact]
    public void SessaoExpiradaNaoGeraNovoTextoNemRenovaAutomaticamente()
    {
        var session = RadarSamples.Session() with { ExpiraEm = Now };
        var candidate = Candidate([Item("838", 4)]) with { ValidoAte = Now.AddMinutes(5) };
        var snapshot = new RadarPushSnapshot(candidate, session, "", "encrypted", "fingerprint", 1, null, 0);
        Assert.Null(RadarPushContent.Criar(snapshot, "ExpoPushToken[synthetic]", Now));
    }

    [Fact]
    public void SentidosDiferentesNaoSaoAgrupadosComoMesmoVeiculo()
    {
        var line = Guid.NewGuid();
        var body = Body(Item("838", 3, line, Guid.NewGuid()), Item("838", 7, line, Guid.NewGuid()));
        Assert.Equal("838 em 3 min · 838 em 7 min", body);
    }

    private static RadarConteudoCandidato Select(RadarSessaoResponse session, EventoParadaDto[] events)
        => new RadarSelector(Options.Create(new PhysicalEtaOptions { MaxAgeSeconds = 90 }))
            .Selecionar(session, "Fragoso", events, Now, Now, 45);

    private static string Body(params RadarItemCandidato[] items)
        => RadarPushText.Format(Candidate(items), RadarSamples.Session(), Now).Body;

    private static RadarConteudoCandidato Candidate(RadarItemCandidato[] items, int adicionais = 0, int linhasAdicionais = 0)
        => new(Guid.NewGuid(), Guid.NewGuid(), 1, RadarSamples.Stop, "Fragoso", Now, Now, Now.AddMinutes(30),
            items, adicionais, LinhasAdicionais: linhasAdicionais);

    private static RadarItemCandidato Item(string code, int minutes, Guid? line = null, Guid? sentido = null,
        string type = TiposEventoParada.Arrival, bool priority = false)
        => new($"event-{code}-{minutes}-{Guid.NewGuid():N}", line ?? Guid.NewGuid(), code,
            $"vehicle-{code}-{minutes}", type, Now.AddMinutes(minutes), minutes * 60L, priority,
            "PhysicalCausal", "estimativa_fisica", Now.AddSeconds(-5), sentido ?? Guid.NewGuid());
}
