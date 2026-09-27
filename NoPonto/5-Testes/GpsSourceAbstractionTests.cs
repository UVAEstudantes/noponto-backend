using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsSourceAbstractionTests
{
    [Fact]
    public async Task ZirixAdapter_NormalizaContratoEPreservaBearing()
    {
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(-2);
        var json = $"[{{\"id_veiculo\":\"A1\",\"servico\":\"838\",\"latitude\":\"-22.9\",\"longitude\":\"-43.2\",\"velocidade\":\"21\",\"direcao\":\"360\",\"datetime\":\"{timestamp:O}\",\"datetime_servidor\":\"{timestamp:O}\"}}]";
        var client = new GpsSppoClient(Http(json, "https://zirix.test/gps"),
            NullLogger<GpsSppoClient>.Instance);
        var source = (IWindowedGpsSource)new ZirixGpsSource(client);

        var result = await source.GetPositionsAsync(timestamp.AddMinutes(-1), DateTimeOffset.UtcNow,
            CancellationToken.None);
        var observation = Assert.Single(result.Observations);

        Assert.Equal("A1", observation.VehicleId);
        Assert.Equal("838", observation.ServiceCode);
        Assert.Equal(0, observation.Bearing);
        Assert.Equal(GpsSourceNames.ZirixDirect, observation.Source);
        Assert.Equal("ZIRIX", observation.Provider);
    }

    [Fact]
    public void DatarioAdapter_PreservaIdentidadeEstruturalQualidadeENulls()
    {
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(-1);
        var observation = DatarioGpsSource.Map(new GpsDatarioVehicleDto
        {
            IdRegistro = "record-1", IdVeiculo = "bus-1", Fornecedor = "maxtrack",
            Servico = "006", Latitude = -22.9, Longitude = -43.2, Velocidade = null,
            Direcao = 90, RouteId = "route", DirectionId = "1", ShapeId = "shape", TripId = "trip",
            Datetime = timestamp.ToString("O"), QualidadeSinal = "GOOD", QuantidadeSatelites = 8,
            Hdop = 1.2, FontePosicao = "GPS", FonteVelocidade = null,
        });

        Assert.NotNull(observation);
        Assert.Equal(GpsSourceNames.Datario, observation.Source);
        Assert.Equal("MAXTRACK", observation.Provider);
        Assert.Equal("record-1", observation.SourceRecordId);
        Assert.Equal("route", observation.RouteId);
        Assert.Equal("1", observation.DirectionId);
        Assert.Equal(0, observation.SpeedKmh);
        Assert.Equal(8, observation.Quality!.Satellites);
        Assert.Null(observation.Quality.SpeedSource);
    }

    [Fact]
    public void DatarioAdapter_RejeitaRegistroSemCamposMinimos()
        => Assert.Null(DatarioGpsSource.Map(new GpsDatarioVehicleDto()));

    [Fact]
    public async Task BrtAdapter_EntregaMesmoContratoSemMudarCliente()
    {
        var unix = DateTimeOffset.UtcNow.AddSeconds(-2).ToUnixTimeMilliseconds();
        var json = $"{{\"veiculos\":[{{\"codigo\":\"42\",\"linha\":\"50\",\"latitude\":-22.9,\"longitude\":-43.2,\"dataHora\":{unix},\"velocidade\":12,\"direcao\":180}}]}}";
        var source = new BrtCurrentGpsSource(new GpsBrtClient(Http(json, "https://brt.test/"),
            NullLogger<GpsBrtClient>.Instance));

        var observation = Assert.Single(await source.GetPositionsAsync(CancellationToken.None));
        Assert.Equal("BRT-42", observation.VehicleId);
        Assert.Equal("50", observation.ServiceCode);
        Assert.Equal(180, observation.Bearing);
        Assert.Equal(GpsSourceNames.BrtCurrent, observation.Source);
        Assert.Equal("BRT_RIO", observation.Provider);
    }

    [Theory]
    [InlineData(GpsModalNames.Bus, GpsSourceNames.ZirixDirect)]
    [InlineData(GpsModalNames.Brt, GpsSourceNames.BrtCurrent)]
    public void Resolver_SelecionaPrimariasPadrao(string modal, string expected)
    {
        var resolver = Resolver();
        Assert.Equal(expected, resolver.GetPrimary(modal).Name);
        Assert.Equal(GpsSourceNames.Datario, Assert.Single(resolver.GetShadows(modal)).Name);
    }

    [Fact]
    public void Resolver_FonteDesconhecidaProduzErroClaro()
    {
        var resolver = Resolver(new GpsSourcesOptions { BusPrimarySource = "NAO_EXISTE" });
        var error = Assert.Throws<InvalidOperationException>(() => resolver.GetPrimary(GpsModalNames.Bus));
        Assert.Contains("NAO_EXISTE", error.Message);
        Assert.Contains("não está registrada", error.Message);
    }

    [Fact]
    public void PollingEColetor_NaoDependemDeClientesHttpConcretos()
    {
        var dependencies = typeof(GpsPollingService).GetConstructors().Single().GetParameters()
            .Select(x => x.ParameterType).ToArray();
        var collectorDependencies = typeof(GpsSppoCollectorService).GetConstructors().Single().GetParameters()
            .Select(x => x.ParameterType).ToArray();

        Assert.Contains(typeof(IGpsSourceResolver), dependencies);
        Assert.Contains(typeof(IGpsSourceResolver), collectorDependencies);
        Assert.DoesNotContain(typeof(GpsSppoClient), dependencies);
        Assert.DoesNotContain(typeof(GpsBrtClient), dependencies);
        Assert.DoesNotContain(typeof(GpsSppoClient), collectorDependencies);
    }

    private static GpsSourceResolver Resolver(GpsSourcesOptions? options = null)
        => new([new StubSource(GpsSourceNames.ZirixDirect), new StubSource(GpsSourceNames.BrtCurrent),
                new StubSource(GpsSourceNames.Datario)],
            Options.Create(options ?? new GpsSourcesOptions()));

    private static HttpClient Http(string json, string baseAddress) => new(new Handler(json))
    {
        BaseAddress = new Uri(baseAddress), Timeout = Timeout.InfiniteTimeSpan,
    };

    private sealed class StubSource(string name) : IGpsSource
    {
        public string Name => name;
        public Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GpsObservation>>([]);
    }

    private sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
