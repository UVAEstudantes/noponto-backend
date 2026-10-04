using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;
using Xunit;

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

    private static EventosParadaService.StopContext Context(Guid id, int order) => new(id,
        Guid.NewGuid(), order, .5, 500, Guid.NewGuid(), 1000, Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), "1", "Ônibus", "regular");

    private static EventoParadaDto Event(string id, DateTimeOffset? estimated, DateTimeOffset? scheduled) =>
        new(id, TiposEventoParada.Arrival, Guid.NewGuid(), "1", "onibus", "onibus",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "A",
            null, null, "A", null, scheduled, estimated, null, "RealtimeEstimated",
            "OperationalEstimate", DateTimeOffset.UtcNow, true);
}
