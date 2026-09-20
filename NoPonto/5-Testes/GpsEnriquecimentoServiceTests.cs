using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

/// <summary>
/// Fake em memória de IGpsItinerarioRepository — evita dependência de Postgres/PostGIS
/// real para testar a lógica de estabilização de GpsEnriquecimentoService. O teste de
/// matching real é exercido separadamente em GpsItinerarioRepositoryPostgisTests.
/// </summary>
internal sealed class FakeGpsItinerarioRepository : IGpsItinerarioRepository
{
    public Queue<EnriquecimentoRotaDto?> Respostas { get; } = new();
    public int ChamadasBuscarEnriquecimento { get; private set; }
    public int ChamadasGlobaisSimples { get; private set; }
    public int ChamadasMatchingCombinado { get; private set; }
    public int ChamadasBatchGlobal { get; private set; }
    public int ChamadasBatchCombinado { get; private set; }
    public int ChamadasBatchDirecionado { get; private set; }
    public bool InverterResultadosBatch { get; set; }
    public bool FalharGlobalProtegidoComInfraestrutura { get; set; }
    public int ChamadasBatchGlobalProtegido { get; private set; }
    public int ChamadasBatchCombinadoProtegido { get; private set; }
    public int ChamadasBatchDirecionadoProtegido { get; private set; }
    public int TentativasPostgresProtegidas { get; private set; }
    public Queue<ResultadoMatchingCombinado> RespostasCombinadas { get; } = new();
    public Queue<ResultadoBuscaItinerario> RespostasDirecionadas { get; } = new();
    public List<FaixaProjecao?> FaixasDirecionadas { get; } = new();
    public List<FaixaProjecao> FaixasCombinadas { get; } = new();
    public List<SolicitacaoProjecaoOperacional?> SolicitacoesOperacionais { get; } = new();
    public Queue<ResultadoProjecaoOperacional> RespostasOperacionais { get; } = new();
    private EnriquecimentoRotaDto? _globalAtual;
    public List<(string Linha, Guid Id, double Lat, double Lon, double Bearing, double Limite)>
        ChamadasDirecionadas { get; } = new();

    public Task<ResultadoBuscaItinerario> BuscarEnriquecimentoDoItinerarioAsync(
        string codigoLinha, Guid itinerarioId, double latitude, double longitude, double bearing,
        double distanciaMaximaMetros, CancellationToken cancellationToken = default,
        FaixaProjecao? faixa = null)
    {
        ChamadasDirecionadas.Add((codigoLinha, itinerarioId, latitude, longitude, bearing, distanciaMaximaMetros));
        FaixasDirecionadas.Add(faixa);
        // Nos testes anteriores de 2.2, o DTO programado representa também a resposta
        // direcionada do mesmo itinerário. Respostas explícitas têm prioridade na 2.5.
        return Task.FromResult(RespostasDirecionadas.Count > 0
            ? RespostasDirecionadas.Dequeue()
            : _globalAtual?.ItinerarioId == itinerarioId
                ? ResultadoBuscaItinerario.Found(_globalAtual)
                : ResultadoBuscaItinerario.InfrastructureFailure());
    }

    public Task<EnriquecimentoRotaDto?> BuscarEnriquecimentoAsync(
        string codigoLinha, double latitude, double longitude, double bearing,
        double distanciaMaximaMetros, CancellationToken cancellationToken = default)
    {
        ChamadasBuscarEnriquecimento++;
        ChamadasGlobaisSimples++;
        var resposta = Respostas.Count > 0 ? Respostas.Dequeue() : null;
        _globalAtual = resposta;
        return Task.FromResult(resposta);
    }

    public Task<ResultadoMatchingCombinado> BuscarMatchingCombinadoAsync(
        string codigoLinha, Guid? itinerarioAnteriorId, double latitude, double longitude,
        double bearing, double distanciaMaximaMetros, FaixaProjecao? faixa,
        SolicitacaoProjecaoOperacional? projecaoOperacional = null,
        CancellationToken cancellationToken = default)
    {
        ChamadasMatchingCombinado++;
        ChamadasBuscarEnriquecimento++;
        if (faixa is { } faixaValida)
            FaixasCombinadas.Add(faixaValida);
        SolicitacoesOperacionais.Add(projecaoOperacional);
        if (RespostasCombinadas.Count > 0)
            return Task.FromResult(RespostasCombinadas.Dequeue());
        var global = Respostas.Count > 0 ? Respostas.Dequeue() : null;
        _globalAtual = global;
        var resultadoGlobal = global is null
            ? ResultadoBuscaItinerario.NotEligible()
            : ResultadoBuscaItinerario.Found(global);
        var anterior = itinerarioAnteriorId.HasValue && global?.ItinerarioId == itinerarioAnteriorId.Value
            ? RespostasDirecionadas.Count > 0
                ? RespostasDirecionadas.Dequeue()
                : ResultadoBuscaItinerario.Found(global)
            : ResultadoBuscaItinerario.NotEligible();
        var operacional = projecaoOperacional is null
            ? ResultadoProjecaoOperacional.NaoSolicitada()
            : RespostasOperacionais.Count > 0
                ? RespostasOperacionais.Dequeue()
                : ResultadoProjecaoOperacional.Inelegivel();
        return Task.FromResult(new ResultadoMatchingCombinado(resultadoGlobal, anterior, operacional));
    }

    public Task<string?> BuscarGeometriaGeoJsonAsync(
        Guid itinerarioId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Não utilizado nestes testes.");

    public Task<ResultadoMatchingLote<ResultadoMatchingGlobalLote>> BuscarGlobaisEmLoteAsync(
        IReadOnlyList<EntradaMatchingGlobalLote> entradas, int tamanhoChunk = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChamadasBatchGlobal++;
        var resultados = entradas.Select(x =>
        {
            var rota = Respostas.Count > 0 ? Respostas.Dequeue() : null;
            _globalAtual = rota;
            return new ResultadoMatchingGlobalLote(x.InputId, rota is null
                ? ResultadoBuscaItinerario.NotEligible()
                : ResultadoBuscaItinerario.Found(rota));
        }).ToArray();
        return Task.FromResult(new ResultadoMatchingLote<ResultadoMatchingGlobalLote>(
            InverterResultadosBatch ? resultados.Reverse().ToArray() : resultados,
            Metricas(TipoBatchMatching.GlobalSimples, entradas.Count)));
    }

    public Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>> BuscarCombinadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingCombinadoLote> entradas, int tamanhoChunk = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChamadasBatchCombinado++;
        var resultados = entradas.Select(x =>
        {
            var global = Respostas.Count > 0 ? Respostas.Dequeue() : null;
            _globalAtual = global;
            var resposta = RespostasCombinadas.Count > 0
                ? RespostasCombinadas.Dequeue()
                : new ResultadoMatchingCombinado(
                    global is null ? ResultadoBuscaItinerario.NotEligible() : ResultadoBuscaItinerario.Found(global),
                    x.ItinerarioAnteriorId.HasValue && global?.ItinerarioId == x.ItinerarioAnteriorId
                        ? ResultadoBuscaItinerario.Found(global)
                        : ResultadoBuscaItinerario.NotEligible(),
                    x.ProjecaoOperacional.HasValue
                        ? RespostasOperacionais.Count > 0
                            ? RespostasOperacionais.Dequeue()
                            : ResultadoProjecaoOperacional.Inelegivel()
                        : ResultadoProjecaoOperacional.NaoSolicitada());
            return new ResultadoMatchingCombinadoLote(x.InputId, resposta);
        }).ToArray();
        return Task.FromResult(new ResultadoMatchingLote<ResultadoMatchingCombinadoLote>(
            InverterResultadosBatch ? resultados.Reverse().ToArray() : resultados,
            Metricas(TipoBatchMatching.Combinado, entradas.Count)));
    }

    public Task<ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>> BuscarDirecionadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingDirecionadoLote> entradas, int tamanhoChunk = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChamadasBatchDirecionado++;
        var resultados = entradas.Select(x => new ResultadoMatchingDirecionadoLote(x.InputId,
            RespostasDirecionadas.Count > 0
                ? RespostasDirecionadas.Dequeue()
                : _globalAtual?.ItinerarioId == x.ItinerarioId
                    ? ResultadoBuscaItinerario.Found(_globalAtual)
                    : ResultadoBuscaItinerario.InfrastructureFailure())).ToArray();
        return Task.FromResult(new ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>(
            InverterResultadosBatch ? resultados.Reverse().ToArray() : resultados,
            Metricas(TipoBatchMatching.Direcionado, entradas.Count)));
    }

    public Task<ResultadoMatchingLote<ResultadoMatchingGlobalLote>> BuscarGlobaisEmLoteAsync(
        IReadOnlyList<EntradaMatchingGlobalLote> entradas, int tamanhoChunk,
        CancellationToken cancellationToken, MatchingBatchStageProtection protecao)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChamadasBatchGlobalProtegido++;
        if (!FalharGlobalProtegidoComInfraestrutura)
            return BuscarGlobaisEmLoteAsync(entradas, tamanhoChunk, cancellationToken);

        TentativasPostgresProtegidas++;
        var acao = protecao.RegistrarFalha(TipoBatchMatching.GlobalSimples,
            CategoriaFalhaMatchingBatch.Connectivity);
        Assert.Equal(AcaoFalhaMatchingBatch.ExecutarSonda, acao);
        TentativasPostgresProtegidas++;
        Assert.True(protecao.ConcluirSonda(TipoBatchMatching.GlobalSimples, infraestrutura: true));
        return Task.FromResult(new ResultadoMatchingLote<ResultadoMatchingGlobalLote>(
            entradas.Select(x => new ResultadoMatchingGlobalLote(x.InputId,
                ResultadoBuscaItinerario.InfrastructureFailure())).ToArray(),
            new(entradas.Count,
            [
                new(TipoBatchMatching.GlobalSimples, OrigemComandoMatchingLote.Batch,
                    entradas.Count, TimeSpan.FromMilliseconds(1)),
                new(TipoBatchMatching.GlobalSimples, OrigemComandoMatchingLote.FallbackIndividual,
                    1, TimeSpan.FromMilliseconds(1)),
            ])));
    }

    public Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>> BuscarCombinadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingCombinadoLote> entradas, int tamanhoChunk,
        CancellationToken cancellationToken, MatchingBatchStageProtection protecao)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChamadasBatchCombinadoProtegido++;
        if (!protecao.CircuitoAberto)
            return BuscarCombinadosEmLoteAsync(entradas, tamanhoChunk, cancellationToken);

        protecao.RegistrarPulo(entradas.Count);
        return Task.FromResult(new ResultadoMatchingLote<ResultadoMatchingCombinadoLote>(
            entradas.Select(x => new ResultadoMatchingCombinadoLote(x.InputId, new(
                ResultadoBuscaItinerario.InfrastructureFailure(),
                ResultadoBuscaItinerario.InfrastructureFailure(),
                x.ProjecaoOperacional.HasValue
                    ? ResultadoProjecaoOperacional.Falha()
                    : ResultadoProjecaoOperacional.NaoSolicitada()))).ToArray(),
            new(entradas.Count, [])));
    }

    public Task<ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>> BuscarDirecionadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingDirecionadoLote> entradas, int tamanhoChunk,
        CancellationToken cancellationToken, MatchingBatchStageProtection protecao)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChamadasBatchDirecionadoProtegido++;
        if (!protecao.CircuitoAberto)
            return BuscarDirecionadosEmLoteAsync(entradas, tamanhoChunk, cancellationToken);

        protecao.RegistrarPulo(entradas.Count);
        return Task.FromResult(new ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>(
            entradas.Select(x => new ResultadoMatchingDirecionadoLote(x.InputId,
                ResultadoBuscaItinerario.InfrastructureFailure())).ToArray(),
            new(entradas.Count, [])));
    }

    private static MetricasMatchingLote Metricas(TipoBatchMatching tipo, int quantidade) =>
        new(quantidade, quantidade == 0 ? [] :
            [new(tipo, OrigemComandoMatchingLote.Batch, quantidade, TimeSpan.FromMilliseconds(1))]);
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
    public async Task Histerese22_RecalculoImpossivelPreservaTimestampAntigo_EComparacaoR1UsaT0()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(0.20));
        repo.Respostas.Enqueue(Rota22(0.90, id: Guid.NewGuid(), distancia: 4));
        repo.Respostas.Enqueue(Rota22(0.29));
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(Rota22(0.85)));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        AssertGpsAtualSemMatching21(Posicao22(19), await servico.EnriquecerAsync(Posicao22(19), default));
        AssertReferencia22(servico, 0.20, 0, 1);
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
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(Rota22(0.98, distancia: 150)));
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

    // Correção 2.3: distância de ambas as rotas medida no GPS atual.
    [Theory]
    [InlineData(5, 140, 20, 30, true)]
    [InlineData(80, 10, 20, 30, false)]
    [InlineData(80, 15, 12, 30, false)]
    [InlineData(5, 50, 20, 30, false)] // Melhoria exatamente 30.
    [InlineData(5, 50.01, 20, 30, true)]
    [InlineData(5, 120, 20, 0, false)] // Parado, exatamente 100.
    [InlineData(5, 120.01, 20, 0, true)]
    public async Task Histerese23_UsaDistanciasAtuais_EPreservaThresholds(
        double distanciaAntiga, double distanciaR1, double distanciaR2, double velocidade, bool troca)
    {
        var repo = new FakeGpsItinerarioRepository();
        var r2 = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota22(0.20, distancia: distanciaAntiga));
        repo.Respostas.Enqueue(Rota22(0.90, id: r2, distancia: distanciaR2));
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(Rota22(0.21, distancia: distanciaR1)));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0) with { Velocidade = velocidade }, default);
        var gps = Posicao22(20) with { Velocidade = velocidade, CodigoLinha = "100" };
        var resultado = await servico.EnriquecerAsync(gps, default);
        Assert.Equal(gps.Latitude, resultado.Latitude);
        Assert.Equal(gps.Longitude, resultado.Longitude);
        Assert.Equal(gps.TimestampGps, resultado.TimestampGps);
        Assert.Equal(troca ? r2 : RotaInicial21().ItinerarioId, resultado.ItinerarioId);
        Assert.Equal(troca ? 0.90 : 0.21, resultado.PosicaoNaRota);
        AssertReferencia22(servico, troca ? 0.90 : 0.21, 20);
        Assert.Equal("Parada 22", resultado.ProximaParadaNome);
        var chamada = Assert.Single(repo.ChamadasDirecionadas);
        Assert.Equal((gps.CodigoLinha, RotaInicial21().ItinerarioId, gps.Latitude, gps.Longitude, 90.0, 250.0), chamada);
        Assert.Null(Assert.Single(repo.FaixasDirecionadas));
        var faixa = Assert.Single(repo.FaixasCombinadas);
        Assert.Equal(0.095, faixa.Min, 12);
        Assert.Equal(0.305, faixa.Max, 12);
    }

    [Theory]
    [InlineData("distancia")]
    [InlineData("bearing")]
    public async Task R1Inelegivel23_NaoBloqueiaR2(string motivo)
    {
        // Motivo espacial é comprovado na SQL real; o service recebe o status explícito.
        Assert.NotEmpty(motivo);
        var repo = new FakeGpsItinerarioRepository();
        var r2 = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota22(0.20));
        repo.Respostas.Enqueue(Rota22(0.85, id: r2));
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.NotEligible());
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        var resultado = await servico.EnriquecerAsync(Posicao22(1), default);
        Assert.Equal(r2, resultado.ItinerarioId);
        AssertReferencia22(servico, 0.85, 1);
    }

    [Fact]
    public async Task FalhaDirecionada23_PreservaT0_ContaCiclos_ERecupera()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(0.20));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        for (var ciclo = 1; ciclo <= 2; ciclo++)
        {
            repo.Respostas.Enqueue(Rota22(0.90, id: Guid.NewGuid()));
            repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.InfrastructureFailure());
            AssertGpsAtualSemMatching21(Posicao22(ciclo), await servico.EnriquecerAsync(Posicao22(ciclo), default));
            AssertReferencia22(servico, 0.20, 0, ciclo);
        }
        repo.Respostas.Enqueue(Rota22(0.22));
        Assert.Equal(0.22, (await servico.EnriquecerAsync(Posicao22(5), default)).PosicaoNaRota);
        AssertReferencia22(servico, 0.22, 5);
    }

    [Fact]
    public async Task R1RecalculadoImpossivel23_NaoPublicaAntigoNemR2_RecuperaComT0()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(0.20));
        repo.Respostas.Enqueue(Rota22(0.10, id: Guid.NewGuid(), distancia: 12));
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(Rota22(0.85, distancia: 15)));
        repo.Respostas.Enqueue(Rota22(0.22));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        AssertGpsAtualSemMatching21(Posicao22(4), await servico.EnriquecerAsync(Posicao22(4), default));
        AssertReferencia22(servico, 0.20, 0, 1);
        Assert.Equal(0.22, (await servico.EnriquecerAsync(Posicao22(5), default)).PosicaoNaRota);
    }

    [Fact]
    public async Task SemGlobal23_NaoConsultaDirecionada_MesmoItinerario25UsaFaixa()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(0.20));
        repo.Respostas.Enqueue(null);
        repo.Respostas.Enqueue(Rota22(0.21));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        AssertGpsAtualSemMatching21(Posicao22(1), await servico.EnriquecerAsync(Posicao22(1), default));
        Assert.Empty(repo.ChamadasDirecionadas);
        Assert.Equal(0.21, (await servico.EnriquecerAsync(Posicao22(3), default)).PosicaoNaRota);
        Assert.Empty(repo.ChamadasDirecionadas);
        Assert.Equal(2, repo.FaixasCombinadas.Count);
    }

    [Theory]
    [InlineData(0.25, 0.27, 10, 0.195, 0.305)]
    [InlineData(0.25, 0.24, 10, 0.195, 0.305)]
    [InlineData(0.01, 0.02, 10, 0, 0.065)]
    [InlineData(0.99, 0.98, 10, 0.935, 1)]
    [InlineData(0.25, 0.75, 1000, 0, 1)]
    public async Task Continuidade25_PublicaRestrito_ClampaFaixa_EPermiteRegressao(
        double anterior, double restrito, double segundos, double min, double max)
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(anterior));
        repo.Respostas.Enqueue(Rota22(0.85)); // Global nunca pode ser publicado neste ciclo.
        var dtoRestrito = new EnriquecimentoRotaDto
        {
            ItinerarioId = RotaInicial21().ItinerarioId, PosicaoNaRota = restrito,
            ComprimentoRotaMetros = 10000, DistanciaARotaMetros = 12,
            ProximaParadaNome = "Parada restrita", DistanciaProximaParadaMetros = 75,
        };
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(dtoRestrito));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        var gps = Posicao22(segundos);
        var resultado = await servico.EnriquecerAsync(gps, default);
        Assert.Equal(restrito, resultado.PosicaoNaRota);
        Assert.Equal("Parada restrita", resultado.ProximaParadaNome);
        Assert.Equal(75, resultado.DistanciaProximaParadaMetros);
        Assert.Equal(gps.Latitude, resultado.Latitude);
        Assert.Equal(gps.Longitude, resultado.Longitude);
        Assert.Equal(gps.TimestampGps, resultado.TimestampGps);
        AssertReferencia22(servico, restrito, segundos);
        Assert.Empty(repo.FaixasDirecionadas);
        var faixa = Assert.Single(repo.FaixasCombinadas);
        Assert.Equal(min, faixa.Min, 12);
        Assert.Equal(max, faixa.Max, 12);
        var estado = Estados22(servico)[gps.Ordem]!;
        Assert.Same(dtoRestrito, estado.GetType().GetProperty("Rota")!.GetValue(estado));
    }

    [Theory]
    [InlineData(false, StatusBuscaItinerario.NotEligible)]
    [InlineData(false, StatusBuscaItinerario.InfrastructureFailure)]
    [InlineData(true, StatusBuscaItinerario.NotEligible)]
    [InlineData(true, StatusBuscaItinerario.InfrastructureFailure)]
    public async Task Continuidade25_FalhaRestritaRespeita2_1E2_3(
        bool outroItinerario, StatusBuscaItinerario status)
    {
        var repo = new FakeGpsItinerarioRepository();
        var r2 = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota22(0.25));
        repo.Respostas.Enqueue(Rota22(0.75, id: outroItinerario ? r2 : null));
        repo.RespostasDirecionadas.Enqueue(status == StatusBuscaItinerario.NotEligible
            ? ResultadoBuscaItinerario.NotEligible() : ResultadoBuscaItinerario.InfrastructureFailure());
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        var gps = Posicao22(5);
        var resultado = await servico.EnriquecerAsync(gps, default);
        Assert.Equal(new FaixaProjecao(0.22, 0.28), Assert.Single(repo.FaixasCombinadas));
        if (outroItinerario)
        {
            Assert.Null(Assert.Single(repo.FaixasDirecionadas));
            Assert.Equal(RotaInicial21().ItinerarioId, Assert.Single(repo.ChamadasDirecionadas).Id);
        }
        else
        {
            Assert.Empty(repo.FaixasDirecionadas);
            Assert.Empty(repo.ChamadasDirecionadas);
        }
        if (outroItinerario && status == StatusBuscaItinerario.NotEligible)
        {
            Assert.Equal(r2, resultado.ItinerarioId);
            AssertReferencia22(servico, 0.75, 5);
        }
        else
        {
            AssertGpsAtualSemMatching21(gps, resultado);
            AssertReferencia22(servico, 0.25, 0, 1);
        }
    }

    [Fact]
    public async Task Continuidade25_ResultadoRestritoAindaPassaPela2_2()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(0.25));
        repo.Respostas.Enqueue(Rota22(0.26));
        // Resposta inconsistente de infraestrutura/fake: a defesa temporal continua obrigatória.
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(Rota22(0.85)));
        var servico = CriarServico(repo);
        await servico.EnriquecerAsync(Posicao22(0), default);
        AssertGpsAtualSemMatching21(Posicao22(5), await servico.EnriquecerAsync(Posicao22(5), default));
        AssertReferencia22(servico, 0.25, 0, 1);
        Assert.Empty(repo.FaixasDirecionadas);
        Assert.Single(repo.FaixasCombinadas);
    }

    [Theory]
    [InlineData(double.NaN, 10000, true)]
    [InlineData(0.25, 0, true)]
    [InlineData(0.25, double.PositiveInfinity, true)]
    [InlineData(0.25, 10000, false)]
    public async Task Continuidade25_ReferenciaInvalidaNaoEnviaFaixa(
        double progresso, double comprimento, bool temTimestamp)
    {
        var repo = new FakeGpsItinerarioRepository();
        var servico = CriarServico(repo);
        var tipo = typeof(GpsEnriquecimentoService).GetNestedType("ItinerarioConfirmado",
            System.Reflection.BindingFlags.NonPublic)!;
        Estados22(servico)[PosicaoInicial21().Ordem] = Activator.CreateInstance(tipo,
            new object?[] { Rota22(progresso, comprimento), 90.0, 0,
                temTimestamp ? Posicao22(0).TimestampGps : null });
        repo.Respostas.Enqueue(Rota22(0.27));
        Assert.Equal(0.27, (await servico.EnriquecerAsync(Posicao22(5), default)).PosicaoNaRota);
        Assert.Empty(repo.FaixasDirecionadas);
        AssertReferencia22(servico, 0.27, 5);
    }

    // ── 8. Mudança de itinerário ──────────────────────────────────────────────

    [Fact]
    public async Task TrocaDeItinerarioBloqueada_MantemMatchingAtualDoAnterior()
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
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(new EnriquecimentoRotaDto
        {
            ItinerarioId = itinerarioA, PosicaoNaRota = 0.5,
            ComprimentoRotaMetros = 2000, DistanciaARotaMetros = 5,
        }));

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
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(new EnriquecimentoRotaDto
        {
            ItinerarioId = itinerarioA, PosicaoNaRota = 0.5,
            ComprimentoRotaMetros = 2000, DistanciaARotaMetros = 50,
        }));
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

    [Fact]
    public async Task MatchingCombinado_ContinuidadeUsaUmComandoESemDirecionadoAntigo()
    {
        var repo = new FakeGpsItinerarioRepository();
        var id = RotaInicial21().ItinerarioId;
        repo.Respostas.Enqueue(Rota22(.20));
        repo.Respostas.Enqueue(Rota22(.21));
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(Rota22(.205)));
        var servico = CriarServico(repo);

        await servico.EnriquecerAsync(Posicao22(0), default);
        var resultado = await servico.EnriquecerAsync(Posicao22(5), default);

        Assert.Equal(id, resultado.ItinerarioId);
        Assert.Equal(.205, resultado.PosicaoNaRota);
        Assert.Equal(1, repo.ChamadasMatchingCombinado);
        Assert.Empty(repo.ChamadasDirecionadas);
        Assert.Single(repo.FaixasCombinadas);
    }

    [Fact]
    public async Task MatchingCombinado_TrocaDescartaAnteriorRestritoEUsaDirecionadoAntigoSemFaixa()
    {
        var repo = new FakeGpsItinerarioRepository();
        var novo = Guid.NewGuid();
        repo.Respostas.Enqueue(Rota22(.20, distancia: 80));
        repo.Respostas.Enqueue(Rota22(.80, id: novo, distancia: 10));
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(Rota22(.21, distancia: 60)));
        var servico = CriarServico(repo);

        await servico.EnriquecerAsync(Posicao22(0), default);
        var resultado = await servico.EnriquecerAsync(Posicao22(5), default);

        Assert.Equal(novo, resultado.ItinerarioId);
        Assert.Equal(1, repo.ChamadasMatchingCombinado);
        Assert.Single(repo.ChamadasDirecionadas);
        Assert.Null(Assert.Single(repo.FaixasDirecionadas));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MatchingCombinado_StatusPreservaFailClosed(int caso)
    {
        var repo = new FakeGpsItinerarioRepository();
        var anterior = Rota22(.20);
        repo.Respostas.Enqueue(anterior);
        repo.RespostasCombinadas.Enqueue(caso switch
        {
            0 => new(ResultadoBuscaItinerario.Found(Rota22(.21)),
                ResultadoBuscaItinerario.NotEligible()),
            1 => new(ResultadoBuscaItinerario.NotEligible(),
                ResultadoBuscaItinerario.Found(Rota22(.21))),
            _ => new(ResultadoBuscaItinerario.InfrastructureFailure(),
                ResultadoBuscaItinerario.InfrastructureFailure()),
        });
        var servico = CriarServico(repo);

        await servico.EnriquecerAsync(Posicao22(0), default);
        var resultado = await servico.EnriquecerAsync(Posicao22(5), default);

        Assert.Null(resultado.ItinerarioId);
        Assert.Null(resultado.PosicaoNaRota);
        Assert.Equal(1, repo.ChamadasMatchingCombinado);
        Assert.Empty(repo.ChamadasDirecionadas);
    }

    [Fact]
    public async Task MatchingCombinado_ComprimentoIncompativelPreservaGlobalSemDirecionado()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(.20, 10_000));
        repo.Respostas.Enqueue(Rota22(.21, 20_000));
        var servico = CriarServico(repo);

        await servico.EnriquecerAsync(Posicao22(0), default);
        var resultado = await servico.EnriquecerAsync(Posicao22(5), default);

        Assert.Equal(.21, resultado.PosicaoNaRota);
        Assert.Equal(20_000, resultado.ComprimentoRotaMetros);
        Assert.Equal(1, repo.ChamadasMatchingCombinado);
        Assert.Empty(repo.ChamadasDirecionadas);
    }

    [Fact]
    public async Task MatchingCombinado_SemHistoricoUsaSomenteGlobalSimples()
    {
        var repo = new FakeGpsItinerarioRepository();
        repo.Respostas.Enqueue(Rota22(.20));
        var servico = CriarServico(repo);

        var resultado = await servico.EnriquecerAsync(Posicao22(0), default);

        Assert.Equal(.20, resultado.PosicaoNaRota);
        Assert.Equal(1, repo.ChamadasGlobaisSimples);
        Assert.Equal(0, repo.ChamadasMatchingCombinado);
        Assert.Empty(repo.ChamadasDirecionadas);
    }

    [Fact]
    public async Task ProjecaoOperacional_GlobalB_EAInterno_SaemDaMesmaChamadaSemContaminarDto()
    {
        var repo = new FakeGpsItinerarioRepository();
        var servico = CriarServico(repo);
        var itinerarioA = Guid.NewGuid();
        var itinerarioB = Guid.NewGuid();
        var linhaA = Guid.NewGuid();
        var sentidoA = Guid.NewGuid();
        var gps = Posicao22(5) with { CodigoLinha = "414" };
        var observada = new ViagemObservadaState(Guid.NewGuid(), gps.Ordem, itinerarioA,
            Posicao22(0).TimestampGps, Posicao22(0).TimestampGps, .20);
        var contexto = new ContextoOperacional([], observada,
            new(observada, "313", linhaA, sentidoA));

        repo.Respostas.Enqueue(Rota22(.70, id: itinerarioB, distancia: 3));
        repo.RespostasOperacionais.Enqueue(ResultadoProjecaoOperacional.Encontrada(
            new(itinerarioA, .21, 4, 10_000)));

        var resultado = await servico.EnriquecerComContextoAsync(gps, contexto, default, null);

        Assert.Equal(itinerarioB, resultado.Posicao.ItinerarioId);
        Assert.Equal("414", resultado.Posicao.CodigoLinha);
        Assert.Equal(.70, resultado.Posicao.PosicaoNaRota);
        Assert.Equal(StatusProjecaoOperacional.Encontrada, resultado.ProjecaoOperacional.Status);
        Assert.Equal(itinerarioA, resultado.ProjecaoOperacional.Projecao!.ItinerarioId);
        Assert.Equal(.21, resultado.ProjecaoOperacional.Projecao.PosicaoNaRota);
        Assert.Equal(1, repo.ChamadasMatchingCombinado);
        Assert.Equal(0, repo.ChamadasGlobaisSimples);
        Assert.Empty(repo.ChamadasDirecionadas);
        Assert.Equal(itinerarioA,
            Assert.Single(repo.SolicitacoesOperacionais)!.Value.ItinerarioId);
    }

    [Fact]
    public async Task GlobalA_ComHisteresePublicandoB_ReutilizaAParaOperacaoSemRamoRedundante()
    {
        var repo = new FakeGpsItinerarioRepository();
        var servico = CriarServico(repo);
        var itinerarioA = Guid.NewGuid();
        var itinerarioB = Guid.NewGuid();
        var primeira = Posicao22(0);
        repo.Respostas.Enqueue(Rota22(.50, id: itinerarioB, distancia: 4));
        await servico.EnriquecerAsync(primeira, default);

        var observada = new ViagemObservadaState(Guid.NewGuid(), primeira.Ordem, itinerarioA,
            primeira.TimestampGps, primeira.TimestampGps, .20);
        var contexto = new ContextoOperacional([], observada,
            new(observada, "313", Guid.NewGuid(), Guid.NewGuid()));
        repo.Respostas.Enqueue(Rota22(.21, id: itinerarioA, distancia: 3));
        repo.RespostasDirecionadas.Enqueue(ResultadoBuscaItinerario.Found(
            Rota22(.51, id: itinerarioB, distancia: 4)));

        var resultado = await servico.EnriquecerComContextoAsync(
            Posicao22(5), contexto, default, null);

        Assert.Equal(itinerarioB, resultado.Posicao.ItinerarioId);
        Assert.Equal(.51, resultado.Posicao.PosicaoNaRota);
        Assert.Equal(StatusProjecaoOperacional.Encontrada,
            resultado.ProjecaoOperacional.Status);
        Assert.Equal(itinerarioA, resultado.ProjecaoOperacional.Projecao!.ItinerarioId);
        Assert.Equal(.21, resultado.ProjecaoOperacional.Projecao.PosicaoNaRota);
        Assert.Single(repo.SolicitacoesOperacionais);
        Assert.Empty(repo.RespostasOperacionais);
    }
}
