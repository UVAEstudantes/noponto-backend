using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite;
using NoPonto.API.Hubs;
using NoPonto.API.LegacyCompatibility.Controllers;
using NoPonto.Application.DTOs.Modais;
using NoPonto.Application.GPS;
using NoPonto.Application.LegacyCompatibility.DTOs;
using NoPonto.Application.LegacyCompatibility.Services;
using NoPonto.Application.Services;
using NoPonto.Data.Interfaces;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests;

public sealed class FrontendLegacyCompatibilityTests
{
    [Fact]
    public async Task Routing_EndpointsCompiladosSaoUnicosEResolvemSemAmbiguousMatch()
    {
        var version = Guid.NewGuid();
        var service = new FakeMapService(version);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IFrontendLegacyMapaService>(service);
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(FrontendLegacyMapaController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();

        try
        {
            var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
            var expected = new[]
            {
                "linhas/{linhaId:guid}/detalhes",
                "itinerarios/por-linha/{linhaId:guid}/mapa",
                "itinerarios/itinerario/{itinerarioId:guid}/mapa"
            };
            foreach (var route in expected)
            {
                var matches = dataSource.Endpoints.OfType<RouteEndpoint>()
                    .Where(endpoint => string.Equals(
                        endpoint.RoutePattern.RawText?.TrimStart('/'), route,
                        StringComparison.OrdinalIgnoreCase))
                    .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?
                        .HttpMethods.Contains(HttpMethods.Get, StringComparer.OrdinalIgnoreCase) == true)
                    .ToArray();
                Assert.Single(matches);
            }

            var addresses = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses;
            var address = Assert.Single(addresses!);
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var unknown = Guid.NewGuid();
            Assert.Equal(System.Net.HttpStatusCode.NotFound,
                (await client.GetAsync($"/linhas/{unknown}/detalhes")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NotFound,
                (await client.GetAsync($"/itinerarios/por-linha/{unknown}/mapa?incluirParadas=true")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK,
                (await client.GetAsync($"/itinerarios/itinerario/{version}/mapa?incluirParadas=true")).StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Modais_AdicionaBrtVirtualSemPersistir()
    {
        var busId = Guid.NewGuid();
        var repository = new FakeModalRepository([new() { Id = busId, Nome = "Ônibus" }]);
        var result = await new ModalService(repository, NullLogger<ModalService>.Instance).ListarAsync(default);
        Assert.Contains(result, x => x.Id == busId && x.Nome == "Ônibus");
        Assert.Contains(result, x => x.Id == FrontendLegacyModalIds.Brt && x.Nome == "BRT");
        Assert.Single(repository.Persisted);
    }

    [Fact]
    public void Multipattern_EscolhePorChaveEIdDeFormaDeterministica()
    {
        var geometry = NtsGeometryServices.Instance.CreateGeometryFactory(4326)
            .CreateLineString([new(-43, -22), new(-43.1, -22.1)]);
        var direction = Guid.NewGuid();
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var source = new[]
        {
            new FrontendLegacyMapaService.Candidate(direction, "IDA (1)", Guid.NewGuid(), "z", Guid.NewGuid(), 1, geometry),
            new FrontendLegacyMapaService.Candidate(direction, "IDA (1)", firstId, "a", Guid.NewGuid(), 1, geometry)
        };
        var expected = Assert.Single(FrontendLegacyMapaService.SelectRepresentatives(source));
        for (var i = 0; i < 5; i++)
            Assert.Equal(expected.VersionId,
                Assert.Single(FrontendLegacyMapaService.SelectRepresentatives(source.Reverse())).VersionId);
        Assert.Equal(firstId, expected.PatternId);

        var secondIda = source[0] with { DirectionId = Guid.NewGuid(), PatternId = Guid.NewGuid() };
        Assert.Single(FrontendLegacyMapaService.SelectRepresentatives(source.Append(secondIda)));
    }

    [Fact]
    public void SignalR_AliasEhAditivoEHubMantemMetodos()
    {
        var version = Guid.NewGuid();
        var dto = FrontendLegacyPosicaoSignalRDto.From(new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "006", TipoRota = "brt", PadraoVersaoId = version,
            TimestampGps = DateTimeOffset.UtcNow, TimestampServidor = DateTimeOffset.UtcNow
        });
        Assert.Equal(version, dto.PadraoVersaoId);
        Assert.Equal(version, dto.ItinerarioId);
        Assert.Equal("brt", dto.TipoRota);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"padraoVersaoId\"", json);
        Assert.Contains("\"itinerarioId\"", json);
        Assert.Contains("\"tipoRota\":\"brt\"", json);
        Assert.NotNull(typeof(GpsHub).GetMethod("InscreverseLinha"));
        Assert.NotNull(typeof(GpsHub).GetMethod("CancelarLinha"));
    }

    [Fact]
    public async Task Controller_InterpretaItinerarioIdComoPadraoVersaoId()
    {
        var version = Guid.NewGuid();
        var service = new FakeMapService(version);
        var controller = new FrontendLegacyMapaController(service);
        var result = Assert.IsType<OkObjectResult>(await controller.MapaItinerario(version, true));
        var dto = Assert.IsType<FrontendLegacyItinerarioMapaDto>(result.Value);
        Assert.Equal(version, dto.ItinerarioId);
        Assert.Equal(version, dto.PadraoVersaoId);
        Assert.Equal(version, service.RequestedVersion);
    }

    [Fact]
    public async Task Postgis_BuscaBrtMapaOcorrenciasEPublicacaoAtual()
    {
        var connection = Environment.GetEnvironmentVariable("ESTRUTURA_V2_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var options = new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql(connection, x => x.UseNetTopologySuite()).Options;
        await using var db = new TransporteDbContext(options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        var ids = await SeedAsync(db);
        var before = await CountsAsync(db);

        var repository = new EstruturaLeituraV2Repository(db);
        var byCode = await repository.ListarLinhasAsync(null, "b42", null, null, null, 1, 10, default);
        var brt = Assert.Single(byCode.Itens);
        Assert.Equal(ids.BusModal, brt.ModalId);
        Assert.Equal("Ônibus", brt.Modal);
        Assert.Equal("brt", brt.TipoRota);
        var byName = await repository.ListarLinhasAsync(null, "terminal teste", null, null, null, 1, 1, default);
        Assert.Equal(1, byName.TotalRegistros);
        Assert.Equal(1, byName.TamanhoPagina);
        var firstPage = await repository.ListarLinhasAsync(null, null, null, null, null, 1, 1, default);
        Assert.Equal(7, firstPage.TotalRegistros);
        Assert.Single(firstPage.Itens);
        var regular = Assert.Single((await repository.ListarLinhasAsync(null, "r10", null, null, null, 1, 10, default)).Itens);
        Assert.Equal(ids.BusModal, regular.ModalId);
        Assert.Equal("Deodoro", Assert.Single((await repository.ListarLinhasAsync(null, "deodoro",
            ids.TremModal, null, null, 1, 10, default)).Itens).Nome);
        Assert.Equal("Santa Cruz", Assert.Single((await repository.ListarLinhasAsync(null, "santa",
            ids.TremModal, null, null, 1, 10, default)).Itens).Nome);
        Assert.Equal("884", Assert.Single((await repository.ListarLinhasAsync(null, "sepetiba",
            ids.BusModal, null, "brt", 1, 10, default)).Itens).Codigo);
        Assert.Equal("10", Assert.Single((await repository.ListarLinhasAsync(null, "10",
            ids.BusModal, "brt", null, 1, 10, default)).Itens).Codigo);
        Assert.DoesNotContain((await repository.ListarLinhasAsync(null, "10", ids.BusModal,
            null, "brt", 1, 10, default)).Itens, x => x.TipoRota == "brt");

        var service = new FrontendLegacyMapaService(db);
        var map = await service.BuscarMapaLinhaAsync(ids.BrtLine, true, default);
        var itinerary = Assert.Single(map!.Itinerarios);
        Assert.Equal(ids.CurrentVersion, itinerary.ItinerarioId);
        Assert.Equal(ids.CurrentVersion, itinerary.PadraoVersaoId);
        Assert.NotEqual(ids.HistoricalVersion, itinerary.ItinerarioId);
        Assert.Equal(new[] { -22.0, -22.1 }, itinerary.Geometria.Select(x => x.Latitude));
        Assert.Equal(new[] { -43.0, -43.1 }, itinerary.Geometria.Select(x => x.Longitude));
        var stops = Assert.IsAssignableFrom<IReadOnlyList<FrontendLegacyParadaDto>>(itinerary.Paradas);
        Assert.Equal(new[] { 1, 2, 3 }, stops.Select(x => x.Ordem));
        Assert.Equal(stops[0].ParadaId, stops[2].ParadaId);
        Assert.Equal(.5, stops[1].PosicaoLinha, 6);
        Assert.Equal(ids.PreferredPatternVersion, itinerary.ItinerarioId);
        var tremMap = await service.BuscarMapaLinhaAsync(ids.TremLine, true, default);
        var tremItinerary = Assert.Single(tremMap!.Itinerarios);
        Assert.Equal(ids.TremCurrentVersion, tremItinerary.ItinerarioId);
        Assert.Equal("Estação Teste", Assert.Single(tremItinerary.Paradas!).Nome);
        Assert.False(await db.Modais.AnyAsync(x => x.Id == FrontendLegacyModalIds.Brt));
        Assert.Equal(before, await CountsAsync(db));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), x => x.State != EntityState.Unchanged);
    }

    private static async Task<SeedIds> SeedAsync(TransporteDbContext db)
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(4326);
        var modal = new Modal { Id = Guid.NewGuid(), Nome = "Ônibus" };
        var tremModal = new Modal { Id = Guid.NewGuid(), Nome = "Trem" };
        var brt = new Linha { Id = Guid.NewGuid(), Codigo = "B42", Nome = "Terminal Teste",
            ModalId = modal.Id, TipoRota = "brt" };
        var regular = new Linha { Id = Guid.NewGuid(), Codigo = "R10", Nome = "Regular",
            ModalId = modal.Id, TipoRota = "regular" };
        var trem = new Linha { Id = Guid.NewGuid(), Codigo = "TREM-DEODORO", Nome = "Deodoro",
            ModalId = tremModal.Id, TipoRota = "train" };
        var santa = new Linha { Id = Guid.NewGuid(), Codigo = "TREM-SANTA-CRUZ", Nome = "Santa Cruz",
            ModalId = tremModal.Id, TipoRota = "train" };
        var bus884 = new Linha { Id = Guid.NewGuid(), Codigo = "884", Nome = "Sepetiba - Terminal Campo Grande",
            ModalId = modal.Id, TipoRota = "regular" };
        var brt10 = new Linha { Id = Guid.NewGuid(), Codigo = "10", Nome = "Santa Cruz - Terminal Alvorada",
            ModalId = modal.Id, TipoRota = "brt" };
        var direction = new Sentido { Id = Guid.NewGuid(), LinhaId = brt.Id, Nome = "IDA (1)" };
        var preferred = new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = direction.Id,
            Chave = "a-principal", TipoServico = "brt" };
        var other = new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = direction.Id,
            Chave = "z-variante", TipoServico = "brt" };
        var tremDirection = new Sentido { Id = Guid.NewGuid(), LinhaId = trem.Id, Nome = "IDA (1)" };
        var tremPattern = new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = tremDirection.Id,
            Chave = "principal", TipoServico = "train" };
        var historical = Version(preferred.Id, 1, factory, "old");
        var current = Version(preferred.Id, 2, factory, "current");
        var otherVersion = Version(other.Id, 1, factory, "other");
        var tremVersion = Version(tremPattern.Id, 1, factory, "trem");
        var repeated = new Parada { Id = Guid.NewGuid(), Codigo = "P1", Nome = "Terminal",
            Localizacao = factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(-43, -22)), TipoLocal = TiposLocalParada.Estacao };
        var middle = new Parada { Id = Guid.NewGuid(), Codigo = "P2", Nome = "Meio",
            Localizacao = factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(-43.05, -22.05)), TipoLocal = TiposLocalParada.Estacao };
        var station = new Parada { Id = Guid.NewGuid(), Codigo = "T1", Nome = "Estação Teste",
            Localizacao = factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(-43, -22)), TipoLocal = TiposLocalParada.Estacao };
        db.AddRange(modal, tremModal, brt, regular, trem, santa, bus884, brt10, direction, tremDirection, preferred,
            other, tremPattern, historical, current, otherVersion, tremVersion, repeated, middle, station);
        await db.SaveChangesAsync();
        preferred.VersaoAtualId = current.Id;
        other.VersaoAtualId = otherVersion.Id;
        tremPattern.VersaoAtualId = tremVersion.Id;
        db.OcorrenciasParadasPadroes.AddRange(
            Occurrence(current.Id, repeated.Id, 1, 0),
            Occurrence(current.Id, middle.Id, 2, .5),
            Occurrence(current.Id, repeated.Id, 3, 1),
            Occurrence(tremVersion.Id, station.Id, 1, 0));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(modal.Id, tremModal.Id, brt.Id, historical.Id, current.Id, current.Id, trem.Id, tremVersion.Id);
    }

    private static PadraoVersao Version(Guid pattern, int number,
        NetTopologySuite.Geometries.GeometryFactory factory, string hash) => new()
    {
        Id = Guid.NewGuid(), PadraoOperacionalId = pattern, Numero = number,
        Geometria = factory.CreateLineString([new(-43, -22), new(-43.1, -22.1)]),
        Topologia = TopologiasPadrao.Linear, ComprimentoMetros = 1000,
        HashEstrutural = hash, MetodoConstrucao = "TEST", Confianca = 1,
        AlgoritmoVersao = "TEST", ResultadoValidacao = ResultadosValidacaoPadrao.Valida,
        CriadoEmUtc = DateTimeOffset.UtcNow, PublicadoEmUtc = DateTimeOffset.UtcNow
    };

    private static OcorrenciaParadaPadrao Occurrence(Guid version, Guid stop, int order, double position) => new()
    {
        Id = Guid.NewGuid(), PadraoVersaoId = version, ParadaId = stop, Ordem = order,
        PosicaoTracado = position, DistanciaAcumuladaMetros = position * 1000,
        DistanciaDaLinhaMetros = 0
    };

    private static async Task<(int Lines, int Modals, int Patterns, int Versions, int Occurrences)> CountsAsync(
        TransporteDbContext db) => (await db.Linhas.CountAsync(), await db.Modais.CountAsync(),
        await db.PadroesOperacionais.CountAsync(), await db.PadroesVersoes.CountAsync(),
        await db.OcorrenciasParadasPadroes.CountAsync());

    private sealed record SeedIds(Guid BusModal, Guid TremModal, Guid BrtLine, Guid HistoricalVersion,
        Guid CurrentVersion, Guid PreferredPatternVersion, Guid TremLine, Guid TremCurrentVersion);

    private sealed class FakeModalRepository(IReadOnlyList<ModalConsultaDTO> persisted) : IModalRepository
    {
        public IReadOnlyList<ModalConsultaDTO> Persisted { get; } = persisted;
        public Task<IReadOnlyList<ModalConsultaDTO>> ListarAsync(CancellationToken cancellationToken)
            => Task.FromResult(Persisted);
    }

    private sealed class FakeMapService(Guid version) : IFrontendLegacyMapaService
    {
        public Guid? RequestedVersion { get; private set; }
        public Task<FrontendLegacyLinhaDetalhesDto?> BuscarDetalhesLinhaAsync(Guid linhaId, CancellationToken ct)
            => Task.FromResult<FrontendLegacyLinhaDetalhesDto?>(null);
        public Task<FrontendLegacyItinerarioMapaLinhaDto?> BuscarMapaLinhaAsync(Guid linhaId, bool incluirParadas, CancellationToken ct)
            => Task.FromResult<FrontendLegacyItinerarioMapaLinhaDto?>(null);
        public Task<FrontendLegacyItinerarioMapaDto?> BuscarMapaVersaoAsync(Guid padraoVersaoId, bool incluirParadas, CancellationToken ct)
        {
            RequestedVersion = padraoVersaoId;
            return Task.FromResult<FrontendLegacyItinerarioMapaDto?>(new(version, version, Guid.NewGuid(),
                "Linha", "IDA", [], []));
        }
    }
}
