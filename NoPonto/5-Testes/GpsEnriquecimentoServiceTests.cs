using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

/// <summary>
/// Fake em memória de IGpsItinerarioRepository — evita dependência de Postgres/PostGIS
/// real para testar a lógica de estabilização de GpsEnriquecimentoService. O teste de
/// matching real contra geometria (ruas paralelas, loops) precisa de um banco PostGIS
/// de fato e está fora do alcance deste ambiente (ver observações finais).
/// </summary>
internal sealed class FakeGpsItinerarioRepository : IGpsItinerarioRepository
{
    public Queue<EnriquecimentoRotaDto?> Respostas { get; } = new();
    public int ChamadasBuscarEnriquecimento { get; private set; }

    public Task<EnriquecimentoRotaDto?> BuscarEnriquecimentoAsync(
        string codigoLinha, double latitude, double longitude, double bearing,
        double distanciaMaximaMetros, CancellationToken cancellationToken = default)
    {
        ChamadasBuscarEnriquecimento++;
        var resposta = Respostas.Count > 0 ? Respostas.Dequeue() : null;
        return Task.FromResult(resposta);
    }

    public Task<string?> BuscarGeometriaGeoJsonAsync(
        Guid itinerarioId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Não utilizado nestes testes.");
}

public class GpsEnriquecimentoServiceTests
{
    private static GpsEnriquecimentoService CriarServico(
        IGpsItinerarioRepository repo, GpsPollingOptions? opcoes = null)
    {
        opcoes ??= new GpsPollingOptions();
        return new GpsEnriquecimentoService(
            repo,
            Options.Create(opcoes),
            NullLogger<GpsEnriquecimentoService>.Instance);
    }

    // ── 4. Circularidade de bearing ──────────────────────────────────────────

    [Theory]
    [InlineData(359, 1, 2)]
    [InlineData(1, 359, 2)]
    [InlineData(0, 180, 180)]
    [InlineData(10, 20, 10)]
    [InlineData(0, 0, 0)]
    public void DiferencaAngular_ConsideraCircularidade(double a, double b, double esperado)
        => Assert.Equal(esperado, GpsEnriquecimentoService.DiferencaAngular(a, b), 3);

    // ── 2/10. Fora da rota — não fabrica PosicaoNaRota, coerência entre campos ──

    [Fact]
    public async Task GpsForaDaRota_NaoFabricaCamposDerivados()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(null); // sem candidato dentro da distância/bearing

        var servico = CriarServico(repo);

        var posicao = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.9, Longitude = -43.2,
            Bearing = 90,
            TimestampGps = DateTimeOffset.UtcNow,
            TimestampServidor = DateTimeOffset.UtcNow,
        };

        var resultado = await servico.EnriquecerAsync(posicao, default);

        // Coerência: todos os campos dependentes de rota ficam nulos juntos,
        // nunca parcialmente preenchidos.
        Assert.Null(resultado.PosicaoNaRota);
        Assert.Null(resultado.ItinerarioId);
        Assert.Null(resultado.ComprimentoRotaMetros);
        Assert.Null(resultado.ProximaParadaNome);
        Assert.Null(resultado.DistanciaProximaParadaMetros);
    }

    // ── 1/3. Matching correto e PosicaoNaRota em [0,1] ──────────────────────

    [Fact]
    public async Task GpsValidoProximoDaRota_UsaPosicaoNaRotaRetornadaPeloRepositorio()
    {
        var repo = new FakeGpsItinerarioRepository();
        var itinerarioId = Guid.NewGuid();
        repo.Respostas.Enqueue(new EnriquecimentoRotaDto
        {
            ItinerarioId = itinerarioId,
            PosicaoNaRota = 0.37,
            ComprimentoRotaMetros = 3000,
            DistanciaARotaMetros = 12,
        });

        var servico = CriarServico(repo);

        var posicao = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.9, Longitude = -43.2, Bearing = 45,
            TimestampGps = DateTimeOffset.UtcNow,
            TimestampServidor = DateTimeOffset.UtcNow,
        };

        var resultado = await servico.EnriquecerAsync(posicao, default);

        Assert.Equal(itinerarioId, resultado.ItinerarioId);
        Assert.InRange(resultado.PosicaoNaRota!.Value, 0.0, 1.0);
        Assert.Equal(0.37, resultado.PosicaoNaRota);
    }

    // ── 5. Veículo parado — bearing não oscila ───────────────────────────────

    [Fact]
    public async Task VeiculoParado_BearingNaoOscilaArtificialmente()
    {
        var repo = new FakeGpsItinerarioRepository();
        var rotaConfirmada = new EnriquecimentoRotaDto
        {
            ItinerarioId = Guid.NewGuid(),
            PosicaoNaRota = 0.4,
            ComprimentoRotaMetros = 5000,
            DistanciaARotaMetros = 5,
        };
        repo.Respostas.Enqueue(rotaConfirmada);

        var servico = CriarServico(repo);

        var t0 = DateTimeOffset.UtcNow;
        var primeira = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.9000, Longitude = -43.2000,
            Bearing = 90,
            TimestampGps = t0, TimestampServidor = t0,
        };

        var resultado1 = await servico.EnriquecerAsync(primeira, default);
        Assert.Equal(90, resultado1.Bearing);

        repo.Respostas.Enqueue(rotaConfirmada);

        // Segunda leitura: deslocamento minúsculo (~1m, ruído de GPS), simulando
        // que o pipeline (GpsPollingService.MontarComHistorico) já propagou o
        // bearing confirmado anteriormente para esta nova leitura.
        var segunda = primeira with
        {
            Latitude = primeira.Latitude + 0.00001,
            LatitudeAnterior = primeira.Latitude,
            LongitudeAnterior = primeira.Longitude,
            TimestampAnterior = primeira.TimestampGps,
            TimestampGps = t0.AddSeconds(20),
            TimestampServidor = t0.AddSeconds(20),
            Bearing = resultado1.Bearing, // carregado pelo pipeline real
        };

        var resultado2 = await servico.EnriquecerAsync(segunda, default);

        Assert.Equal(resultado1.Bearing, resultado2.Bearing);
    }

    // ── 6. Salto geográfico impossível ───────────────────────────────────────

    [Fact]
    public void EhSaltoImplausivel_DetectaVelocidadeImpossivel()
    {
        // ~50km em 10s => ~18.000 km/h
        var implausivel = GpsEnriquecimentoService.EhSaltoImplausivel(
            latAnterior: -22.90, lonAnterior: -43.20,
            latNova: -23.35, lonNova: -43.20,
            deltaSegundos: 10,
            velocidadeMaximaKmh: 90);

        Assert.True(implausivel);
    }

    [Fact]
    public void EhSaltoImplausivel_NaoAcusaDeslocamentoNormal()
    {
        // ~200m em 20s = 36 km/h — plausível para ônibus urbano
        var implausivel = GpsEnriquecimentoService.EhSaltoImplausivel(
            latAnterior: -22.9000, lonAnterior: -43.2000,
            latNova: -22.9018, lonNova: -43.2000,
            deltaSegundos: 20,
            velocidadeMaximaKmh: 90);

        Assert.False(implausivel);
    }

    [Fact]
    public async Task SaltoImplausivel_NaoConsultaRotaNesteCiclo()
    {
        var repo = new FakeGpsItinerarioRepository();
        var servico = CriarServico(repo);

        var t0 = DateTimeOffset.UtcNow;
        var anterior = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.90, Longitude = -43.20,
            Bearing = 90, TimestampGps = t0, TimestampServidor = t0,
        };
        repo.Respostas.Enqueue(new EnriquecimentoRotaDto
        {
            ItinerarioId = Guid.NewGuid(), PosicaoNaRota = 0.1,
            ComprimentoRotaMetros = 1000, DistanciaARotaMetros = 5,
        });
        await servico.EnriquecerAsync(anterior, default);

        var salto = anterior with
        {
            Latitude = -23.35, Longitude = -43.20,
            LatitudeAnterior = anterior.Latitude, LongitudeAnterior = anterior.Longitude,
            TimestampAnterior = anterior.TimestampGps,
            TimestampGps = t0.AddSeconds(10), TimestampServidor = t0.AddSeconds(10),
            Bearing = 90,
        };

        var chamadasAntes = repo.ChamadasBuscarEnriquecimento;
        await servico.EnriquecerAsync(salto, default);

        Assert.Equal(chamadasAntes, repo.ChamadasBuscarEnriquecimento);
    }

    // ── 8. Mudança de itinerário ──────────────────────────────────────────────

    [Fact]
    public async Task TrocaDeItinerarioBloqueada_MantemDtoAnteriorIntacto()
    {
        var repo = new FakeGpsItinerarioRepository();
        var servico = CriarServico(repo);

        var itinerarioA = Guid.NewGuid();
        var itinerarioB = Guid.NewGuid();

        repo.Respostas.Enqueue(new EnriquecimentoRotaDto
        {
            ItinerarioId = itinerarioA, PosicaoNaRota = 0.5,
            ComprimentoRotaMetros = 2000, DistanciaARotaMetros = 5,
        });

        var t0 = DateTimeOffset.UtcNow;
        var primeira = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.90, Longitude = -43.20, Bearing = 90,
            Velocidade = 30,
            TimestampGps = t0, TimestampServidor = t0,
        };
        var resultado1 = await servico.EnriquecerAsync(primeira, default);
        Assert.Equal(itinerarioA, resultado1.ItinerarioId);

        // Melhoria de distância pequena (1m) — insuficiente para justificar a troca.
        repo.Respostas.Enqueue(new EnriquecimentoRotaDto
        {
            ItinerarioId = itinerarioB, PosicaoNaRota = 0.9,
            ComprimentoRotaMetros = 3000, DistanciaARotaMetros = 4,
        });

        var segunda = primeira with
        {
            LatitudeAnterior = primeira.Latitude, LongitudeAnterior = primeira.Longitude,
            TimestampAnterior = primeira.TimestampGps,
            TimestampGps = t0.AddSeconds(20), TimestampServidor = t0.AddSeconds(20),
            Bearing = 90,
        };
        var resultado2 = await servico.EnriquecerAsync(segunda, default);

        // Nunca mistura ItinerarioId antigo com PosicaoNaRota da rota nova.
        Assert.Equal(itinerarioA, resultado2.ItinerarioId);
        Assert.Equal(0.5, resultado2.PosicaoNaRota);
        Assert.Equal(2000, resultado2.ComprimentoRotaMetros);
    }

    [Fact]
    public async Task TrocaDeItinerarioPermitida_RecalculaTudoDaNovaRota()
    {
        var repo = new FakeGpsItinerarioRepository();
        var servico = CriarServico(repo);

        var itinerarioA = Guid.NewGuid();
        var itinerarioB = Guid.NewGuid();

        repo.Respostas.Enqueue(new EnriquecimentoRotaDto
        {
            ItinerarioId = itinerarioA, PosicaoNaRota = 0.5,
            ComprimentoRotaMetros = 2000, DistanciaARotaMetros = 50,
        });

        var t0 = DateTimeOffset.UtcNow;
        var primeira = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.90, Longitude = -43.20, Bearing = 90,
            Velocidade = 30,
            TimestampGps = t0, TimestampServidor = t0,
        };
        await servico.EnriquecerAsync(primeira, default);

        // Melhoria de distância grande (45m) — justifica a troca.
        repo.Respostas.Enqueue(new EnriquecimentoRotaDto
        {
            ItinerarioId = itinerarioB, PosicaoNaRota = 0.1,
            ComprimentoRotaMetros = 4000, DistanciaARotaMetros = 5,
        });

        var segunda = primeira with
        {
            LatitudeAnterior = primeira.Latitude, LongitudeAnterior = primeira.Longitude,
            TimestampAnterior = primeira.TimestampGps,
            TimestampGps = t0.AddSeconds(20), TimestampServidor = t0.AddSeconds(20),
            Bearing = 90,
        };
        var resultado = await servico.EnriquecerAsync(segunda, default);

        Assert.Equal(itinerarioB, resultado.ItinerarioId);
        Assert.Equal(0.1, resultado.PosicaoNaRota);
        Assert.Equal(4000, resultado.ComprimentoRotaMetros);
    }
}