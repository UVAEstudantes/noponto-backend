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

        // Segunda leitura: deslocamento minúsculo (~1m, ruído de GPS) e a fonte
        // reenviou o mesmo bearing válido (90°) — o bearing deve ser preservado,
        // não recalculado geometricamente sobre um deslocamento de ruído.
        var segunda = primeira with
        {
            Latitude = primeira.Latitude + 0.00001,
            LatitudeAnterior = primeira.Latitude,
            LongitudeAnterior = primeira.Longitude,
            TimestampAnterior = primeira.TimestampGps,
            TimestampGps = t0.AddSeconds(20),
            TimestampServidor = t0.AddSeconds(20),
            Bearing = 90, // bearing atual válido da fonte, igual ao ciclo anterior
        };

        var resultado2 = await servico.EnriquecerAsync(segunda, default);

        Assert.Equal(resultado1.Bearing, resultado2.Bearing);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Política de precedência de bearing (cenários A–H)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_BearingFonteValido_ComMovimento_BearingGeometricoPrevalece()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(new EnriquecimentoRotaDto
        {
            ItinerarioId = Guid.NewGuid(), PosicaoNaRota = 0.2,
            ComprimentoRotaMetros = 1000, DistanciaARotaMetros = 5,
        });

        var servico = CriarServico(repo);
        var t0 = DateTimeOffset.UtcNow;

        // Bearing da fonte é 90°, mas o deslocamento real (~223m em 20s, ~40km/h —
        // fisicamente plausível, bem abaixo do limiar de salto de 180km/h) aponta
        // para norte (~0°) — o geométrico deve prevalecer, mesmo com fonte válida.
        var posicao = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.9000, Longitude = -43.2000,
            LatitudeAnterior = -22.9020, LongitudeAnterior = -43.2000,
            TimestampAnterior = t0.AddSeconds(-20),
            Bearing = 90,
            TimestampGps = t0, TimestampServidor = t0,
        };

        var resultado = await servico.EnriquecerAsync(posicao, default);

        var bearingGeometricoEsperado = GpsEnriquecimentoService.CalcularBearing(
            -22.9020, -43.2000, -22.9000, -43.2000);

        Assert.Equal(bearingGeometricoEsperado, resultado.Bearing);
        Assert.NotEqual(90, resultado.Bearing);
    }

    [Fact]
    public async Task B_BearingFonteValido_SemMovimento_BearingDaFontePreservado()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(new EnriquecimentoRotaDto
        {
            ItinerarioId = Guid.NewGuid(), PosicaoNaRota = 0.3,
            ComprimentoRotaMetros = 1000, DistanciaARotaMetros = 5,
        });

        var servico = CriarServico(repo);
        var t0 = DateTimeOffset.UtcNow;

        var posicao = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.9000, Longitude = -43.2000,
            LatitudeAnterior = -22.9000, LongitudeAnterior = -43.2000, // sem deslocamento
            TimestampAnterior = t0.AddSeconds(-20),
            Bearing = 123,
            TimestampGps = t0, TimestampServidor = t0,
        };

        var resultado = await servico.EnriquecerAsync(posicao, default);

        Assert.Equal(123, resultado.Bearing);
    }

    [Fact]
    public async Task C_BearingFonteNulo_ComMovimento_BearingGeometricoUsado()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(new EnriquecimentoRotaDto
        {
            ItinerarioId = Guid.NewGuid(), PosicaoNaRota = 0.2,
            ComprimentoRotaMetros = 1000, DistanciaARotaMetros = 5,
        });

        var servico = CriarServico(repo);
        var t0 = DateTimeOffset.UtcNow;

        var posicao = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.9000, Longitude = -43.2000,
            LatitudeAnterior = -22.9020, LongitudeAnterior = -43.2000,
            TimestampAnterior = t0.AddSeconds(-20),
            Bearing = null, // ex.: SPPO, que não envia bearing
            TimestampGps = t0, TimestampServidor = t0,
        };

        var resultado = await servico.EnriquecerAsync(posicao, default);

        var bearingGeometricoEsperado = GpsEnriquecimentoService.CalcularBearing(
            -22.9020, -43.2000, -22.9000, -43.2000);

        Assert.Equal(bearingGeometricoEsperado, resultado.Bearing);
    }

    [Fact]
    public async Task D_BearingFonteNulo_SemMovimento_ComBearingAnteriorConfirmado_UsaAnterior()
    {
        var repo = new FakeGpsItinerarioRepository();
        var rota = new EnriquecimentoRotaDto
        {
            ItinerarioId = Guid.NewGuid(), PosicaoNaRota = 0.3,
            ComprimentoRotaMetros = 1000, DistanciaARotaMetros = 5,
        };
        repo.Respostas.Enqueue(rota);

        var servico = CriarServico(repo);
        var t0 = DateTimeOffset.UtcNow;

        // Primeira leitura confirma bearing = 77° na memória do serviço.
        var primeira = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.9000, Longitude = -43.2000,
            Bearing = 77,
            TimestampGps = t0, TimestampServidor = t0,
        };
        var resultado1 = await servico.EnriquecerAsync(primeira, default);
        Assert.Equal(77, resultado1.Bearing);

        repo.Respostas.Enqueue(rota);

        // Segunda leitura: sem deslocamento e SEM bearing da fonte.
        var segunda = primeira with
        {
            LatitudeAnterior = primeira.Latitude,
            LongitudeAnterior = primeira.Longitude,
            TimestampAnterior = primeira.TimestampGps,
            TimestampGps = t0.AddSeconds(20),
            TimestampServidor = t0.AddSeconds(20),
            Bearing = null,
        };

        var resultado2 = await servico.EnriquecerAsync(segunda, default);

        Assert.Equal(77, resultado2.Bearing); // reutiliza o bearing confirmado
    }

    [Fact]
    public async Task E_BearingFonteNulo_SemMovimento_SemBearingAnterior_PermaneceNull()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(null);

        var servico = CriarServico(repo);
        var t0 = DateTimeOffset.UtcNow;

        var posicao = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.9000, Longitude = -43.2000,
            LatitudeAnterior = -22.9000, LongitudeAnterior = -43.2000,
            TimestampAnterior = t0.AddSeconds(-20),
            Bearing = null,
            TimestampGps = t0, TimestampServidor = t0,
        };

        var resultado = await servico.EnriquecerAsync(posicao, default);

        Assert.Null(resultado.Bearing);
    }

    [Fact]
    public async Task F_PrimeiroCiclo_SemPosicaoAnterior_BearingFontePreservado()
    {
        var repo = new FakeGpsItinerarioRepository();
        var itinerarioId = Guid.NewGuid();
        repo.Respostas.Enqueue(new EnriquecimentoRotaDto
        {
            ItinerarioId = itinerarioId, PosicaoNaRota = 0.1,
            ComprimentoRotaMetros = 1000, DistanciaARotaMetros = 5,
        });

        var servico = CriarServico(repo);

        var posicao = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.9000, Longitude = -43.2000,
            Bearing = 45, // sem LatitudeAnterior/LongitudeAnterior/TimestampAnterior
            TimestampGps = DateTimeOffset.UtcNow, TimestampServidor = DateTimeOffset.UtcNow,
        };

        var resultado = await servico.EnriquecerAsync(posicao, default);

        Assert.Equal(45, resultado.Bearing);
        Assert.Equal(itinerarioId, resultado.ItinerarioId); // enriquecimento ocorreu normalmente
    }

    [Fact]
    public async Task G_BearingInvalidoDaFonte_NuncaViraBearingValidoPorNormalizacao()
    {
        // GpsBrtClient já rejeita "965" e produz Bearing=null antes de chegar
        // aqui (0..360 é a única validação, sem módulo). Este teste garante que,
        // ao chegar null no enriquecimento, nada o "conserta" via módulo/clamp —
        // só é substituído por bearing geométrico real (se houver movimento) ou
        // pelo bearing confirmado anteriormente.
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(null);

        var servico = CriarServico(repo);
        var t0 = DateTimeOffset.UtcNow;

        var posicao = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "100",
            Latitude = -22.9000, Longitude = -43.2000,
            LatitudeAnterior = -22.9000, LongitudeAnterior = -43.2000,
            TimestampAnterior = t0.AddSeconds(-20),
            Bearing = null, // equivalente ao resultado de GpsBrtClient para "965"
            TimestampGps = t0, TimestampServidor = t0,
        };

        var resultado = await servico.EnriquecerAsync(posicao, default);

        Assert.Null(resultado.Bearing); // nunca 965 % 360 = 245 nem qualquer "correção"
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

    // ── Correção 2.1: estado anterior não confirma a posição GPS atual ────────

    private static PosicaoVeiculoDto PosicaoInicial21() => new()
    {
        Ordem = "V21", CodigoLinha = "100",
        Latitude = -22.9, Longitude = -43.2, Bearing = 90, Velocidade = 30,
        TimestampGps = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        TimestampServidor = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    private static EnriquecimentoRotaDto RotaInicial21() => new()
    {
        ItinerarioId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        PosicaoNaRota = 0.20, ComprimentoRotaMetros = 5000,
        DistanciaARotaMetros = 5, BearingLocal = 90,
        LatitudeProjetada = -22.9, LongitudeProjetada = -43.2,
        ProximaParadaNome = "Parada anterior", DistanciaProximaParadaMetros = 120,
    };

    private static void AssertGpsAtualSemMatching21(
        PosicaoVeiculoDto esperado, PosicaoVeiculoDto resultado)
    {
        Assert.Equal(esperado.Latitude, resultado.Latitude);
        Assert.Equal(esperado.Longitude, resultado.Longitude);
        Assert.Equal(esperado.TimestampGps, resultado.TimestampGps);
        Assert.Equal(esperado.TimestampServidor, resultado.TimestampServidor);
        Assert.Null(resultado.ItinerarioId);
        Assert.Null(resultado.PosicaoNaRota);
        Assert.Null(resultado.ComprimentoRotaMetros);
        Assert.Null(resultado.ProximaParadaNome);
        Assert.Null(resultado.DistanciaProximaParadaMetros);
    }

    [Fact]
    public async Task RotaConfirmadaSeguidaDeOffRoute_PublicaGpsAtualSemMatching()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(RotaInicial21());
        repo.Respostas.Enqueue(null);
        var servico = CriarServico(repo);
        var inicial = PosicaoInicial21();
        Assert.Equal(0.20, (await servico.EnriquecerAsync(inicial, default)).PosicaoNaRota);
        var atual = inicial with
        {
            Latitude = -22.901, Longitude = -43.201,
            TimestampGps = inicial.TimestampGps.AddSeconds(20),
            TimestampServidor = inicial.TimestampServidor.AddSeconds(20),
        };
        var resultado = await servico.EnriquecerAsync(atual, default);
        Assert.Equal(2, repo.ChamadasBuscarEnriquecimento);
        AssertGpsAtualSemMatching21(atual, resultado);
    }

    [Fact]
    public async Task BearingFonteNulo_ReutilizaBearingConfirmado_SemReutilizarMatching()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(RotaInicial21());
        repo.Respostas.Enqueue(null);
        var servico = CriarServico(repo);
        var inicial = PosicaoInicial21();
        await servico.EnriquecerAsync(inicial, default);
        var atual = inicial with
        {
            Latitude = inicial.Latitude + 0.00001, Bearing = null,
            LatitudeAnterior = inicial.Latitude, LongitudeAnterior = inicial.Longitude,
            TimestampAnterior = inicial.TimestampGps,
            TimestampGps = inicial.TimestampGps.AddSeconds(20),
            TimestampServidor = inicial.TimestampServidor.AddSeconds(20),
        };
        var resultado = await servico.EnriquecerAsync(atual, default);
        Assert.Equal(90, resultado.Bearing);
        Assert.Equal(2, repo.ChamadasBuscarEnriquecimento);
        AssertGpsAtualSemMatching21(atual, resultado);
    }

    [Fact]
    public async Task SaltoImplausivel_PreservaSomenteBearingAnterior_SemMatchingAntigo()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(RotaInicial21());
        var servico = CriarServico(repo);
        var inicial = PosicaoInicial21();
        await servico.EnriquecerAsync(inicial, default);
        var atual = inicial with
        {
            Latitude = -23.35,
            LatitudeAnterior = inicial.Latitude, LongitudeAnterior = inicial.Longitude,
            TimestampAnterior = inicial.TimestampGps,
            TimestampGps = inicial.TimestampGps.AddSeconds(10),
            TimestampServidor = inicial.TimestampServidor.AddSeconds(10),
        };
        var resultado = await servico.EnriquecerAsync(atual, default);
        Assert.Equal(1, repo.ChamadasBuscarEnriquecimento);
        Assert.Equal(90, resultado.Bearing);
        AssertGpsAtualSemMatching21(atual, resultado);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CiclosSemMatching_AvancamERemovemEstadoNoLimiteExistente(bool salto)
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(RotaInicial21());
        var opcoes = new GpsPollingOptions();
        Assert.Equal(3, opcoes.MaxCiclosSemRota);
        var servico = CriarServico(repo, opcoes);
        var inicial = PosicaoInicial21();
        await servico.EnriquecerAsync(inicial, default);
        // Observa contador/remoção sem adicionar API de diagnóstico em produção.
        var estados = (System.Collections.IDictionary)typeof(GpsEnriquecimentoService)
            .GetField("_itinerarioAtual",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(servico)!;
        for (var ciclo = 1; ciclo <= opcoes.MaxCiclosSemRota; ciclo++)
        {
            var atual = inicial with
            {
                Latitude = salto ? -23.35 : -22.901,
                LatitudeAnterior = salto ? inicial.Latitude : null,
                LongitudeAnterior = salto ? inicial.Longitude : null,
                TimestampAnterior = salto ? inicial.TimestampGps : null,
                TimestampGps = inicial.TimestampGps.AddSeconds(ciclo * 10),
                TimestampServidor = inicial.TimestampServidor.AddSeconds(ciclo * 10),
            };
            AssertGpsAtualSemMatching21(atual, await servico.EnriquecerAsync(atual, default));
            if (ciclo < opcoes.MaxCiclosSemRota)
            {
                Assert.True(estados.Contains(inicial.Ordem));
                var estado = estados[inicial.Ordem]!;
                Assert.Equal(ciclo, (int)estado.GetType()
                    .GetProperty("CiclosSemRota")!.GetValue(estado)!);
            }
            else Assert.False(estados.Contains(inicial.Ordem));
        }
        Assert.Equal(salto ? 1 : 4, repo.ChamadasBuscarEnriquecimento);
        var semBearing = inicial with { Bearing = null, TimestampGps = inicial.TimestampGps.AddMinutes(1) };
        var aposRemocao = await servico.EnriquecerAsync(semBearing, default);
        Assert.Null(aposRemocao.Bearing);
        AssertGpsAtualSemMatching21(semBearing, aposRemocao);
    }

    [Fact]
    public async Task CandidatoAtualNoMesmoItinerario_PublicaProgressoAtual()
    {
        var repo = new FakeGpsItinerarioRepository();
        var rota = RotaInicial21();
        repo.Respostas.Enqueue(rota);
        repo.Respostas.Enqueue(new EnriquecimentoRotaDto
        {
            ItinerarioId = rota.ItinerarioId, PosicaoNaRota = 0.24,
            ComprimentoRotaMetros = rota.ComprimentoRotaMetros,
            DistanciaARotaMetros = 7, ProximaParadaNome = "Parada atual",
            DistanciaProximaParadaMetros = 80,
        });
        var servico = CriarServico(repo);
        var inicial = PosicaoInicial21();
        await servico.EnriquecerAsync(inicial, default);
        var atual = inicial with { Latitude = -22.901, TimestampGps = inicial.TimestampGps.AddSeconds(20) };
        var resultado = await servico.EnriquecerAsync(atual, default);
        Assert.Equal(rota.ItinerarioId, resultado.ItinerarioId);
        Assert.Equal(0.24, resultado.PosicaoNaRota);
        Assert.Equal(rota.ComprimentoRotaMetros, resultado.ComprimentoRotaMetros);
        Assert.Equal("Parada atual", resultado.ProximaParadaNome);
        Assert.Equal(80, resultado.DistanciaProximaParadaMetros);
        Assert.Equal(atual.Latitude, resultado.Latitude);
    }

    // Correção 2.2: orçamento temporal em metros sobre o último matching confirmado.
    private static EnriquecimentoRotaDto Rota22(double progresso, double comprimento = 10000,
        Guid? id = null, double distancia = 5) => new()
    {
        ItinerarioId = id ?? RotaInicial21().ItinerarioId,
        PosicaoNaRota = progresso, ComprimentoRotaMetros = comprimento,
        DistanciaARotaMetros = distancia, BearingLocal = 90,
        ProximaParadaNome = "Parada 22", DistanciaProximaParadaMetros = 50,
    };

    private static PosicaoVeiculoDto Posicao22(double segundos) => PosicaoInicial21() with
    {
        // GPS praticamente parado: um salto de projeção não é salto geográfico.
        Latitude = PosicaoInicial21().Latitude + 0.00001,
        TimestampGps = PosicaoInicial21().TimestampGps.AddSeconds(segundos),
        TimestampServidor = PosicaoInicial21().TimestampServidor.AddSeconds(segundos),
    };

    private static System.Collections.IDictionary Estados22(GpsEnriquecimentoService servico) =>
        (System.Collections.IDictionary)typeof(GpsEnriquecimentoService)
            .GetField("_itinerarioAtual", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.GetValue(servico)!;

    private static void AssertReferencia22(GpsEnriquecimentoService servico,
        double progresso, double segundos, int ciclos = 0)
    {
        var estado = Estados22(servico)[PosicaoInicial21().Ordem]!;
        var tipo = estado.GetType();
        var rota = (EnriquecimentoRotaDto)tipo.GetProperty("Rota")!.GetValue(estado)!;
        Assert.Equal(progresso, rota.PosicaoNaRota);
        Assert.Equal(Posicao22(segundos).TimestampGps,
            (DateTimeOffset?)tipo.GetProperty("TimestampGpsConfirmado")!.GetValue(estado));
        Assert.Equal(ciclos, (int)tipo.GetProperty("CiclosSemRota")!.GetValue(estado)!);
    }

    [Theory]
    [InlineData(0.20, 0.21, 20, 10000, true)] // Avanço normal.
    [InlineData(0.20, 0.85, 20, 10000, false)] // Salto adiante.
    [InlineData(0.20, 0.198, 1, 10000, true)] // Pequeno retrocesso.
    [InlineData(0.85, 0.20, 20, 10000, false)] // Grande retrocesso.
    [InlineData(0.20, 0.204, 0.1, 10000, true)] // Oscilação <50m.
    [InlineData(0.20, 0.85, 1, 10000, false)] // Auto-interseção simulada, GPS próximo.
    [InlineData(0.98, 0.02, 20, 10000, false)] // Sem wrap-around.
    [InlineData(0.20, 0.85, 200, 10000, true)] // Intervalo longo.
    [InlineData(0.98, 0.02, 200, 10000, true)] // Plausível, sem afirmar nova volta.
    [InlineData(0, 0.5, 1, 200, true)] // Distância == limite (100m).
    [InlineData(0, 0.5001, 1, 200, false)] // Acima do limite.
    [InlineData(0.20, 0.21, 0, 10000, false)] // Tempo igual.
    [InlineData(0.20, 0.21, -1, 10000, false)] // Tempo negativo.
    public async Task ProgressoTemporal22_AplicaOrcamentoAbsoluto(
        double anterior, double atual, double segundos, double comprimento, bool aceito)
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(anterior, comprimento));
        repo.Respostas.Enqueue(Rota22(atual, comprimento));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        var gps = Posicao22(segundos);
        var resultado = await servico.EnriquecerAsync(gps, default);
        Assert.Equal(gps.Bearing, resultado.Bearing);
        if (aceito)
        {
            Assert.Equal(atual, resultado.PosicaoNaRota);
            AssertReferencia22(servico, atual, segundos);
        }
        else
        {
            AssertGpsAtualSemMatching21(gps, resultado);
            AssertReferencia22(servico, anterior, 0, 1);
        }
    }

    [Fact]
    public async Task Recuperacao22_ComparaComT0_EUsaTodoIntervalo()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(0.20));
        repo.Respostas.Enqueue(Rota22(0.85));
        repo.Respostas.Enqueue(Rota22(0.22));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        AssertGpsAtualSemMatching21(Posicao22(4), await servico.EnriquecerAsync(Posicao22(4), default));
        AssertReferencia22(servico, 0.20, 0, 1);
        // 200m em 5s desde T0 passa; em 1s desde T1 falharia.
        Assert.Equal(0.22, (await servico.EnriquecerAsync(Posicao22(5), default)).PosicaoNaRota);
        AssertReferencia22(servico, 0.22, 5);
    }

    [Fact]
    public async Task TresRejeicoes22_RemovemReferencia_EPermitemNovaInicializacao()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(0.20));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        for (var ciclo = 1; ciclo <= 3; ciclo++)
        {
            repo.Respostas.Enqueue(Rota22(0.85));
            AssertGpsAtualSemMatching21(Posicao22(ciclo),
                await servico.EnriquecerAsync(Posicao22(ciclo), default));
            if (ciclo < 3) AssertReferencia22(servico, 0.20, 0, ciclo);
        }
        Assert.False(Estados22(servico).Contains(PosicaoInicial21().Ordem));
        repo.Respostas.Enqueue(Rota22(0.85));
        Assert.Equal(0.85, (await servico.EnriquecerAsync(Posicao22(4), default)).PosicaoNaRota);
        AssertReferencia22(servico, 0.85, 4);
    }

    [Fact]
    public async Task Histerese22_PreservaTimestampAntigo_EComparacaoR1UsaT0()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(0.20));
        repo.Respostas.Enqueue(Rota22(0.90, id: Guid.NewGuid(), distancia: 4));
        repo.Respostas.Enqueue(Rota22(0.29));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        Assert.Equal(0.20, (await servico.EnriquecerAsync(Posicao22(19), default)).PosicaoNaRota);
        AssertReferencia22(servico, 0.20, 0);
        Assert.Equal(0.29, (await servico.EnriquecerAsync(Posicao22(20), default)).PosicaoNaRota);
        AssertReferencia22(servico, 0.29, 20);
    }

    [Fact]
    public async Task TrocaPermitida22_NaoComparaFracoesDeR1ER2()
    {
        var repo = new FakeGpsItinerarioRepository();
        var novoId = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota22(0.98, distancia: 150));
        repo.Respostas.Enqueue(Rota22(0.02, id: novoId));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        var resultado = await servico.EnriquecerAsync(Posicao22(1), default);
        Assert.Equal(novoId, resultado.ItinerarioId);
        AssertReferencia22(servico, 0.02, 1);
    }

    [Fact]
    public async Task ComprimentoAlterado22_ReiniciaReferenciaSemCompararGeometrias()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(0.20));
        repo.Respostas.Enqueue(Rota22(0.85, 20000));
        repo.Respostas.Enqueue(Rota22(0.20, 20000));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        Assert.Equal(0.85, (await servico.EnriquecerAsync(Posicao22(1), default)).PosicaoNaRota);
        AssertReferencia22(servico, 0.85, 1);
        AssertGpsAtualSemMatching21(Posicao22(2), await servico.EnriquecerAsync(Posicao22(2), default));
        AssertReferencia22(servico, 0.85, 1, 1);
    }

    [Theory]
    [InlineData(double.NaN, 10000)]
    [InlineData(double.PositiveInfinity, 10000)]
    [InlineData(double.NegativeInfinity, 10000)]
    [InlineData(-0.01, 10000)]
    [InlineData(1.01, 10000)]
    [InlineData(0.2, 0)]
    [InlineData(0.2, -1)]
    [InlineData(0.2, double.NaN)]
    [InlineData(0.2, double.PositiveInfinity)]
    [InlineData(0.2, double.NegativeInfinity)]
    public async Task ValoresInvalidos22_NaoConfirmamPrimeiraRotaNemContaminamReferencia(
        double progresso, double comprimento)
    {
        var repo = new FakeGpsItinerarioRepository();
        var servico = CriarServico(repo);
        repo.Respostas.Enqueue(Rota22(progresso, comprimento));
        AssertGpsAtualSemMatching21(Posicao22(0), await servico.EnriquecerAsync(Posicao22(0), default));
        var estado = Estados22(servico)[PosicaoInicial21().Ordem]!;
        Assert.Null(estado.GetType().GetProperty("Rota")!.GetValue(estado));
        repo.Respostas.Enqueue(Rota22(0.20));
        await servico.EnriquecerAsync(Posicao22(1), default);
        repo.Respostas.Enqueue(Rota22(progresso, comprimento));
        AssertGpsAtualSemMatching21(Posicao22(2), await servico.EnriquecerAsync(Posicao22(2), default));
        AssertReferencia22(servico, 0.20, 1, 1);
    }

    [Fact]
    public async Task TimestampConfirmadoAusente22_InicializaApenasComCandidatoValido()
    {
        var repo = new FakeGpsItinerarioRepository();
        var servico = CriarServico(repo);
        var tipo = typeof(GpsEnriquecimentoService).GetNestedType("ItinerarioConfirmado",
            System.Reflection.BindingFlags.NonPublic)!;
        Estados22(servico)[PosicaoInicial21().Ordem] = Activator.CreateInstance(tipo,
            new object?[] { Rota22(0.20), 90.0, 0, null });
        repo.Respostas.Enqueue(Rota22(0.85));
        Assert.Equal(0.85, (await servico.EnriquecerAsync(Posicao22(1), default)).PosicaoNaRota);
        AssertReferencia22(servico, 0.85, 1);
    }

    [Fact]
    public async Task Orcamento22_RespeitaConfiguracao_EOscilacoesConsecutivas()
    {
        Assert.Equal(50, new GpsPollingOptions().ToleranciaProjecaoMetros);
        var repo = new FakeGpsItinerarioRepository();
        var servico = CriarServico(repo, new GpsPollingOptions
        {
            VelocidadeMaximaKmh = 36, ToleranciaProjecaoMetros = 10,
        });
        repo.Respostas.Enqueue(Rota22(0, 100));
        await servico.EnriquecerAsync(Posicao22(0), default);
        repo.Respostas.Enqueue(Rota22(0.25, 100)); // 25m <=20m/s +10m.
        Assert.Equal(0.25, (await servico.EnriquecerAsync(Posicao22(1), default)).PosicaoNaRota);
        repo.Respostas.Enqueue(Rota22(0.1, 100)); // 15m >2m +10m.
        AssertGpsAtualSemMatching21(Posicao22(1.1), await servico.EnriquecerAsync(Posicao22(1.1), default));

        repo = new FakeGpsItinerarioRepository();
        servico = CriarServico(repo);
        var valores = new[] { 0.20, 0.204, 0.20, 0.203, 0.199 };
        for (var i = 0; i < valores.Length; i++)
        {
            repo.Respostas.Enqueue(Rota22(valores[i]));
            Assert.Equal(valores[i], (await servico.EnriquecerAsync(Posicao22(i * 0.1), default)).PosicaoNaRota);
        }
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
