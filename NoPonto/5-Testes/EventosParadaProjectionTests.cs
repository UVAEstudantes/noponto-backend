using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;
using Xunit;
using System.Text.Json;

namespace NoPonto.Tests;

public sealed class EventosParadaProjectionTests
{
    [Fact]
    public void EtaDaProximaOcorrenciaTemPrecedencia()
    {
        var occurrence = Guid.NewGuid();
        var position = new PosicaoVeiculoDto { Ordem = "A", CodigoLinha = "1",
            ProximaOcorrenciaParadaPadraoId = occurrence, EtaProximaParadaSegundos = 90,
            Velocidade = 30, TimestampGps = DateTimeOffset.UtcNow };
        var stop = Context(occurrence, 2);
        Assert.Equal(90, EventosParadaService.EstimateRoadSeconds(position, stop, 1000));
    }

    [Fact]
    public void EstimativaRodoviariaUsaVelocidadeMediaSemMl()
    {
        var position = new PosicaoVeiculoDto { Ordem = "A", CodigoLinha = "1",
            Velocidade = 30, VelocidadeMedia = 36, TimestampGps = DateTimeOffset.UtcNow };
        Assert.Equal(100, EventosParadaService.EstimateRoadSeconds(position, Context(Guid.NewGuid(), 2), 1000));
    }

    [Fact]
    public void OrdenaPorEstimadoDepoisProgramadoEIdentidadeEhDeterministica()
    {
        var now = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
        var later = Event("road:A:occ", now.AddMinutes(5), null);
        var sooner = Event("rail:run:stop", null, now.AddMinutes(2));
        Assert.Equal(["rail:run:stop", "road:A:occ"],
            EventosParadaService.Order([later, sooner]).Select(x => x.EventId));
    }

    [Fact]
    public void CasoReal855_ProximaOcorrenciaGeraArrival()
    {
        var lineId = Guid.Parse("b84c0ff5-0f52-48d1-ac06-f80a9a629ffc");
        var directionId = Guid.Parse("8679948b-7527-4c90-9b06-629ab877a669");
        var patternId = Guid.Parse("b442243e-ba30-4240-9161-1ba53cd06c62");
        var versionId = Guid.Parse("d132cdfa-f416-4a61-b5b6-a97956700d97");
        var occurrenceId = Guid.Parse("4deab83d-5716-4ad4-a2aa-66c9774b6047");
        var stopId = Guid.Parse("f1375428-9209-45ce-84cf-d224c5686b47");
        var stop = new EventosParadaService.StopContext(occurrenceId, stopId, 33,
            0.3175856322171168, 11186.65, versionId, 35214.60075556618,
            patternId, directionId, lineId, "855", "Ônibus", "regular");
        var vehicle = new PosicaoVeiculoDto
        {
            Ordem = "D86238", CodigoLinha = "855", LinhaId = lineId,
            SentidoId = directionId, PadraoOperacionalId = patternId,
            PadraoVersaoId = versionId, PosicaoNaRota = 0.31281942032297155,
            ComprimentoRotaMetros = 35214.60075556618,
            ProximaOcorrenciaParadaPadraoId = occurrenceId,
            ProximaParadaNome = "Hospital da Mulher Mariska Ribeiro",
            Velocidade = 20, TimestampGps = DateTimeOffset.Parse("2026-10-04T12:00:00Z")
        };

        var result = EventosParadaService.ProjectRoadEvents(
            [stop], [vehicle], DateTimeOffset.Parse("2026-10-04T12:00:05Z"));

        var arrival = Assert.Single(result);
        Assert.Equal(TiposEventoParada.Arrival, arrival.EventType);
        Assert.Equal("D86238", arrival.VehicleId);
        Assert.Equal(occurrenceId, arrival.OcorrenciaParadaPadraoId);
    }

    [Fact]
    public void CasoReal855_PosicaoDepoisOuVersaoDiferenteNaoMistura()
    {
        var versionId = Guid.Parse("d132cdfa-f416-4a61-b5b6-a97956700d97");
        var occurrenceId = Guid.Parse("4deab83d-5716-4ad4-a2aa-66c9774b6047");
        var stop = new EventosParadaService.StopContext(occurrenceId, Guid.NewGuid(), 33,
            0.3175856322171168, 11186.65, versionId, 35214.60075556618,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "855", "Ônibus", "regular");
        PosicaoVeiculoDto Vehicle(double position, Guid version) => new()
        {
            Ordem = "D86238", CodigoLinha = "855", PadraoVersaoId = version,
            PosicaoNaRota = position, Velocidade = 20, TimestampGps = DateTimeOffset.UtcNow
        };

        Assert.Empty(EventosParadaService.ProjectRoadEvents(
            [stop], [Vehicle(0.32, versionId)], DateTimeOffset.UtcNow));
        Assert.Empty(EventosParadaService.ProjectRoadEvents(
            [stop], [Vehicle(0.312819, Guid.NewGuid())], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void LeituraOficialRecuperaPayloadRecenteComIdsEstruturais()
    {
        var versionId = Guid.Parse("d132cdfa-f416-4a61-b5b6-a97956700d97");
        var occurrenceId = Guid.Parse("4deab83d-5716-4ad4-a2aa-66c9774b6047");
        var source = new PosicaoVeiculoDto
        {
            Ordem = "D86238", CodigoLinha = "855", PadraoVersaoId = versionId,
            PosicaoNaRota = 0.31281942032297155,
            ProximaOcorrenciaParadaPadraoId = occurrenceId,
            LinhaId = Guid.Parse("b84c0ff5-0f52-48d1-ac06-f80a9a629ffc"),
            SentidoId = Guid.Parse("8679948b-7527-4c90-9b06-629ab877a669"),
            TimestampGps = DateTimeOffset.UtcNow
        };
        var recent = JsonSerializer.Serialize(source);

        var decoded = VeiculosLinhaRuntimeReader.Decode(
            ["D86238"], [null], [0], [recent]);

        var vehicle = Assert.Single(decoded);
        Assert.Equal(StatusVeiculo.SemSinal, vehicle.Status);
        Assert.Equal(versionId, vehicle.PadraoVersaoId);
        Assert.Equal(occurrenceId, vehicle.ProximaOcorrenciaParadaPadraoId);
        Assert.Equal(source.PosicaoNaRota, vehicle.PosicaoNaRota);
    }

    [Fact]
    public void ChavesDoCaso855SaoCodigoDaLinhaEOrdem()
    {
        Assert.Equal("linha:855:veiculos", GpsPollingService.ChaveLinha("855"));
        Assert.Equal("veiculo:D86238:ativo", GpsPollingService.ChaveVeiculoAtivo("D86238"));
        Assert.Equal("veiculo:D86238:recente", GpsPollingService.ChaveVeiculoRecente("D86238"));
    }

    [Theory]
    [InlineData(1, 20, PapeisEstacaoEvento.Origin, ModosProximosVeiculos.Departures)]
    [InlineData(10, 20, PapeisEstacaoEvento.Intermediate, ModosProximosVeiculos.Arrivals)]
    [InlineData(20, 20, PapeisEstacaoEvento.Destination, ModosProximosVeiculos.Arrivals)]
    public void PapelDaEstacaoEhRelativoASequenciaDoExpectedRun(int sequence, int last,
        string expectedRole, string expectedMode)
    {
        var role = EventosParadaService.ResolveStationRole(sequence, last);
        Assert.Equal(expectedRole, role);
        Assert.Equal(expectedMode, role == PapeisEstacaoEvento.Origin
            ? ModosProximosVeiculos.Departures : ModosProximosVeiculos.Arrivals);
    }

    [Fact]
    public void ShortStartUsaSuaPrimeiraOcorrenciaComoOrigem()
    {
        Assert.Equal(PapeisEstacaoEvento.Origin,
            EventosParadaService.ResolveStationRole(1, 8));
    }

    [Fact]
    public void DestinoFinalNaoEhExpostoComoFalsaPassagem()
    {
        Assert.True(EventosParadaService.ShouldExposeRailEvent(PapeisEstacaoEvento.Origin));
        Assert.True(EventosParadaService.ShouldExposeRailEvent(PapeisEstacaoEvento.Intermediate));
        Assert.False(EventosParadaService.ShouldExposeRailEvent(PapeisEstacaoEvento.Destination));
    }

    private static EventosParadaService.StopContext Context(Guid id, int order) => new(id,
        Guid.NewGuid(), order, .5, 500, Guid.NewGuid(), 1000, Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), "1", "Ônibus", "regular");

    private static EventoParadaDto Event(string id, DateTimeOffset? estimated, DateTimeOffset? scheduled) =>
        new(id, TiposEventoParada.Arrival, Guid.NewGuid(), "1", "onibus", "onibus",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "A",
            null, null, "A", null, scheduled, estimated, null, "RealtimeEstimated",
            "OperationalEstimate", DateTimeOffset.UtcNow, true);
}
