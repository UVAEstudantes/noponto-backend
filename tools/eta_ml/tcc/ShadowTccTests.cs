using System.Net.Http.Json;
using System.Text.Json;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

// Explicitly compiled by Lab.targets; never part of normal application/test builds.
public sealed class ShadowTccTests
{
    static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");
    [Fact]
    public async Task ActualPhysicalAndShadowAdapter_OneObservation_ActualFastApi()
    {
        var now = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
        var fixture = PhysicalEtaMvpTests.Fixture();
        var gps = fixture.Posicao with { LinhaId=Id(1), SentidoId=Id(2), PadraoOperacionalId=Id(3),
            PadraoVersaoId=Id(4), ProximaOcorrenciaParadaPadraoId=Id(5),
            ModalFonte="ONIBUS", ProvedorFonte="SYNTHETIC", Ordem="fixture-only" };
        fixture = fixture with { Posicao=gps, Diagnostico=fixture.Diagnostico! with {
            PadraoOperacionalId=Id(3), PadraoVersaoId=Id(4), SentidoId=Id(2) } };
        var physical = PhysicalEta.Apply(fixture, now, new() { Enabled=true }).Posicao;
        Assert.Equal(100, physical.EtaProximaParadaSegundos);
        var state = new ViagemObservadaState(Id(7), gps.Ordem, Id(4), now.AddMinutes(-1), now, .2,
            PadraoOperacionalId:Id(3));
        var target = new OcorrenciaParada(Id(5), Id(4), Id(6), 2, .3);
        var trip = new ViagemObservadaResultado(ViagemObservadaStatus.Updated, state) {
            EstadoOperacional=new(state, gps.CodigoLinha, Id(1), Id(2)), ProximaOcorrenciaOperacional=target };
        var observation = HistoricalEtaAdapter.Capture(physical, trip, now)!;
        Assert.NotNull(observation);
        Assert.Equal(100, observation.PublicEta);
        var geometry = new HistoricalEtaGeometry(Id(5), Id(6), Id(4), Id(3), Id(1), Id(2),
            gps.CodigoLinha, "LINEAR", .3, 1000);
        var payload = HistoricalEtaAdapter.Payload(observation, geometry)!;
        Assert.NotNull(payload);
        Assert.Null(HistoricalEtaAdapter.Payload(observation, geometry with { VersionId=Id(99) }));
        Assert.Null(HistoricalEtaAdapter.Payload(observation, geometry with { DirectionId=Id(99) }));
        Assert.Null(HistoricalEtaAdapter.Payload(observation, geometry with { TargetId=Id(99) }));
        using var http = new HttpClient { Timeout=TimeSpan.FromSeconds(10) };
        var url = Environment.GetEnvironmentVariable("TCC_ETA_URL")!;
        Assert.StartsWith("http://127.0.0.1:", url);
        using var response = await http.PostAsJsonAsync(url + "/eta/batch", new[] { payload });
        response.EnsureSuccessStatusCode();
        Assert.Equal("synthetic", response.Headers.GetValues("X-Model-Data-Kind").Single());
        var rows = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Equal(observation.RequestId, rows![0].GetProperty("shadow_request_id").GetString());
        Assert.True(rows[0].GetProperty("eta_segundos").GetDouble() > 0);
        // ML failure cannot mutate immutable physical DTO, even with an actual unavailable route.
        using var failed = await http.PostAsJsonAsync(url + "/missing", new[] { payload });
        Assert.False(failed.IsSuccessStatusCode);
        Assert.Equal(100, physical.EtaProximaParadaSegundos);
        File.WriteAllText(Environment.GetEnvironmentVariable("TCC_BRIDGE_RECORD")!, JsonSerializer.Serialize(new {
            data_kind="synthetic", real_performance_claim=false,
            observation.ObservationId, observation.RequestId, observation.TripId, observation.Lap,
            timestamp_gps=gps.TimestampGps, predicted_at=now, target_id=target.Id, stop_id=target.ParadaId,
            version_id=Id(4), direction_id=Id(2), features=payload, physical_seconds=observation.PublicEta,
            extra_trees_seconds=rows[0].GetProperty("eta_segundos").GetDouble(),
            model_sha256=response.Headers.GetValues("X-Model-Version").Single()
        }));
    }
}
