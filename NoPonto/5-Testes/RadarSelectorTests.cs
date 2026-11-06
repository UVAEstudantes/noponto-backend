using Microsoft.Extensions.Options;
using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.Radar;
using Xunit;

namespace NoPonto.Tests;

public sealed class RadarSelectorTests
{
    private readonly RadarSelector selector = new(Options.Create(new PhysicalEtaOptions { MaxAgeSeconds = 90 }));

    [Fact]
    public void PrioridadeReservaVagaSemEsconderChegadaMaisProxima()
    {
        var s = RadarSamples.Session(RadarSamples.Stop, RadarSamples.Lines) with { LinhasPrioritarias = [RadarSamples.Lines[1]] };
        var output = Select(s, [Event(0, 120), Event(2, 180), Event(3, 240), Event(1, 600)]);
        Assert.Equal(new[] { RadarSamples.Lines[0], RadarSamples.Lines[2], RadarSamples.Lines[1] }, output.Itens.Select(i => i.LinhaId));
        Assert.Equal(1, output.Adicionais);
        Assert.True(output.Itens.Single(i => i.LinhaId == RadarSamples.Lines[1]).Prioritaria);
    }

    [Fact]
    public void TresPrioridadesPreservamAlternativaMuitoProxima()
    {
        var s = RadarSamples.Session(RadarSamples.Stop, RadarSamples.Lines) with { LinhasPrioritarias = RadarSamples.Lines[1..] };
        var output = Select(s, [Event(3, 420), Event(2, 360), Event(1, 300), Event(0, 120)]);
        Assert.Equal(new[] { 120L, 300L, 360L }, output.Itens.Select(i => i.EtaSegundos));
        Assert.False(output.Itens[0].Prioritaria);
    }

    [Fact]
    public void VariasPrioridadesNaoEliminamTodasAsAlternativasMaisDistantes()
    {
        var s = RadarSamples.Session(RadarSamples.Stop, RadarSamples.Lines) with { LinhasPrioritarias = RadarSamples.Lines[..3] };
        var output = Select(s, [Event(0, 120), Event(1, 180), Event(2, 240), Event(3, 300)]);
        Assert.Equal(new[] { 120L, 180L, 300L }, output.Itens.Select(i => i.EtaSegundos));
        Assert.Contains(output.Itens, i => !i.Prioritaria);
    }

    [Fact]
    public void VeiculosDiferentesDaMesmaLinhaPermanecemEDuplicadosSaoRemovidos()
    {
        var s = RadarSamples.Session();
        var a = Event(0, 120) with { EventId = "a", VehicleId = "bus-a" };
        var b = Event(0, 180) with { EventId = "b", VehicleId = "bus-b" };
        var duplicatedVehicle = a with { EventId = "another-a", EstimatedAt = RadarSamples.Now.AddSeconds(300) };
        var output = Select(s, [b, a, a, duplicatedVehicle]);
        Assert.Equal(new[] { "bus-a", "bus-b" }, output.Itens.Select(i => i.VeiculoId));
        Assert.Equal(0, output.Adicionais);
    }

    [Theory]
    [InlineData("ausente")] [InlineData("passado")] [InlineData("gpsAntigo")]
    [InlineData("gpsFuturo")] [InlineData("indisponivel")] [InlineData("qualityAntiga")]
    [InlineData("semEvidencia")] [InlineData("foraJanela")] [InlineData("duracaoNegativa")]
    [InlineData("sentido")] [InlineData("versao")] [InlineData("padrao")] [InlineData("parada")]
    [InlineData("presenca")] [InlineData("linhaNaoSelecionada")] [InlineData("fonteDesconhecida")]
    public void EtaInvalidoOuContextoIncompativelNaoGeraChegada(string partition)
    {
        var e = Event(0, 120);
        e = partition switch
        {
            "ausente" => e with { EstimatedAt = null, ScheduledAt = null, SecondsUntilEvent = null },
            "passado" => e with { EstimatedAt = RadarSamples.Now.AddSeconds(-1) },
            "gpsAntigo" => e with { LastRealtimeEvidenceUtc = RadarSamples.Now.AddSeconds(-91) },
            "gpsFuturo" => e with { LastRealtimeEvidenceUtc = RadarSamples.Now.AddSeconds(1) },
            "indisponivel" => e with { Quality = "previsao_indisponivel" },
            "qualityAntiga" => e with { Quality = "StalePrediction" },
            "semEvidencia" => e with { LastRealtimeEvidenceUtc = null },
            "foraJanela" => e with { EstimatedAt = RadarSamples.Now.AddSeconds(601) },
            "duracaoNegativa" => e with { EstimatedAt = null, SecondsUntilEvent = -1 },
            "sentido" => e with { SentidoId = Guid.NewGuid() },
            "versao" => e with { PadraoVersaoId = Guid.NewGuid() },
            "padrao" => e with { PadraoOperacionalId = Guid.NewGuid() },
            "parada" => e with { ParadaId = Guid.NewGuid() },
            "presenca" => e with { EventType = "PRESENCE" },
            "linhaNaoSelecionada" => e with { LinhaId = RadarSamples.Lines[1] },
            _ => e with { Source = "Unknown" }
        };
        Assert.Empty(Select(RadarSamples.Session(), [e]).Itens);
    }

    [Fact]
    public void ConsultaVencidaNaoProduzCandidatos()
    {
        var result = selector.Selecionar(RadarSamples.Session(), "Test", [Event(0, 120)],
            RadarSamples.Now.AddSeconds(-301), RadarSamples.Now, 45);
        Assert.Empty(result.Itens);
    }

    [Fact]
    public void EventoDuplicadoNaoEstendeValidadeDaEvidenciaEscolhida()
    {
        var first = Event(0, 120) with { LastRealtimeEvidenceUtc = RadarSamples.Now.AddSeconds(-80) };
        var duplicate = first with { LastRealtimeEvidenceUtc = RadarSamples.Now.AddSeconds(-1), EstimatedAt = RadarSamples.Now.AddMinutes(3) };
        var content = Select(RadarSamples.Session(), [first, duplicate]);
        Assert.Equal(RadarSamples.Now.AddSeconds(10), content.ValidoAte);
        Assert.Equal(first.LastRealtimeEvidenceUtc, Assert.Single(content.Itens).EvidenciaEm);
    }

    [Fact]
    public void TerminalSemPrevisaoDeSaidaNaoViraChegada()
    {
        var departure = Event(0, 120) with { EventType = TiposEventoParada.Departure,
            StationRole = PapeisEstacaoEvento.Origin, NextVehiclesMode = ModosProximosVeiculos.Departures };
        var noForecast = departure with { EstimatedAt = null, ScheduledAt = null, SecondsUntilEvent = null };
        Assert.Empty(Select(RadarSamples.Session(), [noForecast]).Itens);
        var item = Assert.Single(Select(RadarSamples.Session(), [departure]).Itens);
        Assert.Equal(TiposEventoParada.Departure, item.TipoEvento);
        Assert.False(Select(RadarSamples.Session(), [departure]).PresencaTerminalDisponivel);
    }

    [Fact]
    public void ProgramacaoFerroviariaMantemOrigemEQualidadeSemFabricarEvidencia()
    {
        var scheduled = Event(0, 120) with { VehicleId = null, RailVehicleId = Guid.NewGuid(),
            Source = "ScheduledEstimated", Quality = "ScheduleOnly", LastRealtimeEvidenceUtc = null,
            EstimatedAt = null, ScheduledAt = RadarSamples.Now.AddMinutes(2) };
        var item = Assert.Single(Select(RadarSamples.Session(), [scheduled]).Itens);
        Assert.Equal("ScheduledEstimated", item.Source);
        Assert.Equal("ScheduleOnly", item.Quality);
        Assert.Null(item.EvidenciaEm);
    }

    [Fact]
    public void DuracaoExistenteUsaInstanteDaConsultaEValidadeNaoUltrapassaEvidencia()
    {
        var e = Event(0, 120) with { EstimatedAt = null, LastRealtimeEvidenceUtc = RadarSamples.Now.AddSeconds(-70) };
        var result = selector.Selecionar(RadarSamples.Session(), "Test", [e], RadarSamples.Now,
            RadarSamples.Now.AddSeconds(10), 45);
        Assert.Equal(110, Assert.Single(result.Itens).EtaSegundos);
        Assert.Equal(RadarSamples.Now.AddSeconds(20), result.ValidoAte);
        Assert.Equal(RadarSamples.Now.AddSeconds(120), result.Itens[0].PrevistoEm);
    }

    [Fact]
    public void DiversidadeDesempataTemposIguaisSemDeduplicarLinha()
    {
        var s = RadarSamples.Session(RadarSamples.Stop, RadarSamples.Lines[..2]);
        var output = Select(s, [Event(0, 120), Event(0, 180) with { EventId = "a", VehicleId = "second" },
            Event(0, 180) with { EventId = "b", VehicleId = "third" }, Event(1, 180) with { EventId = "z" }]);
        Assert.Contains(output.Itens, i => i.LinhaId == RadarSamples.Lines[1]);
        Assert.Equal(3, output.Itens.Length);
    }

    private RadarConteudoCandidato Select(RadarSessaoResponse session, EventoParadaDto[] events)
        => selector.Selecionar(session, "Bom Repouso sintético", events, RadarSamples.Now, RadarSamples.Now, 45);
    private static EventoParadaDto Event(int line, int seconds) => RadarSamples.Event(RadarSamples.Stop, RadarSamples.Lines[line], seconds);
}

internal static class RadarSamples
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T18:00:00Z");
    public static readonly Guid Stop = Guid.Parse("20000000-0000-0000-0000-000000000002");
    public static readonly Guid[] Lines = Enumerable.Range(1, 4).Select(n => Guid.Parse($"10000000-0000-0000-0000-{n:000000000000}")).ToArray();
    public static RadarContexto Context(Guid line) => new(line, line, line, line);
    public static RadarSessaoResponse Session(Guid? stop = null, Guid[]? lines = null)
    {
        lines ??= [Lines[0]];
        return new(Guid.NewGuid(), Guid.NewGuid(), stop ?? Stop, lines.Select(Context).ToArray(), lines, [],
            10, "ativa", Now, Now.AddHours(1), 1);
    }
    public static EventoParadaDto Event(Guid stop, Guid line, int seconds = 120) => new("event-" + line,
        TiposEventoParada.Arrival, line, "884", "onibus", "onibus", line, line, line, stop, Guid.NewGuid(),
        "vehicle-" + line, null, null, null, null, null, Now.AddSeconds(seconds), seconds,
        "PhysicalCausal", "estimativa_fisica", Now.AddSeconds(-5), true);
}
