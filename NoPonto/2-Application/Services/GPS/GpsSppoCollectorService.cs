using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed class GpsSppoCollectorService : BackgroundService
{
    private readonly IGpsSource _source;
    private readonly GpsSppoSnapshotStore _snapshot;
    private readonly IOptionsMonitor<GpsSppoCollectorOptions> _opcoesMonitor;
    private readonly ILogger<GpsSppoCollectorService> _logger;
    private readonly GpsSppoCollectorMetrics _metrics;
    private readonly SemaphoreSlim _coletaEmAndamento = new(1, 1);
    private DateTimeOffset? _watermarkConfirmado;

    public GpsSppoCollectorService(
        IGpsSourceResolver sourceResolver,
        GpsSppoSnapshotStore snapshot,
        IOptionsMonitor<GpsSppoCollectorOptions> opcoesMonitor,
        ILogger<GpsSppoCollectorService> logger,
        GpsSppoCollectorMetrics? metrics = null)
    {
        _source = sourceResolver.GetPrimary(GpsModalNames.Bus);
        if (_source is not IWindowedGpsSource and not ISnapshotGpsSource)
            throw new InvalidOperationException("A fonte GPS BUS precisa suportar janela ou snapshot explicito.");
        _snapshot = snapshot;
        _opcoesMonitor = opcoesMonitor;
        _logger = logger;
        _metrics = metrics ?? new GpsSppoCollectorMetrics();
    }

    internal DateTimeOffset? WatermarkConfirmado => _watermarkConfirmado;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var iniciais = _opcoesMonitor.CurrentValue;
        _logger.LogInformation(
            "GpsSppoCollectorService iniciado: timeout {timeout}s, janela inicial {janela}s, " +
            "overlap {overlap}s, chunk catch-up {chunk}s, lag recuperavel {lag}s, intervalo {intervalo}s.",
            iniciais.TimeoutSegundos, iniciais.JanelaInicialSegundos,
            iniciais.OverlapSegundos, iniciais.CatchupChunkSegundos,
            iniciais.MaxLagRecuperavelSegundos, iniciais.IntervaloEntreColetasSegundos);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ColetarUmaVezAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha inesperada no coletor SPPO; o servico continuara ativo.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_opcoesMonitor.CurrentValue.IntervaloEntreColetasSegundos),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("GpsSppoCollectorService encerrado.");
    }

    internal async Task<ResultadoFonteGps> ColetarUmaVezAsync(
        DateTimeOffset referencia,
        CancellationToken stoppingToken = default)
    {
        await _coletaEmAndamento.WaitAsync(stoppingToken);
        try
        {
            if (_source is ISnapshotGpsSource snapshotSource)
            {
                // Snapshot has no recoverable historical interval or source watermark.
                // Never overwrite an unacknowledged generation or perform historical catch-up.
                if (_snapshot.Ler() is not null) return ResultadoFonteGps.Vazio(TimeSpan.Zero);
                var started = DateTimeOffset.UtcNow;
                var read = await snapshotSource.GetResultAsync(stoppingToken);
                var result = new ResultadoFonteGps(read.Status,
                    read.Observations.Select(x => GpsObservationMapper.ToPosition(x, "ONIBUS")).ToArray(),
                    read.Duration, read.FailureReason);
                _logger.LogInformation("BUS current snapshot: status={status}, count={count}; historical coverage unknown, intermediate positions not recoverable.",
                    result.Status, result.Posicoes.Count);
                if (result.Status == StatusFonteGps.Sucesso && result.Posicoes.Count > 0)
                    await _snapshot.PublicarAsync(null, null, started, DateTimeOffset.UtcNow, null,
                        result.Posicoes, stoppingToken);
                return result;
            }
            var opcoes = _opcoesMonitor.CurrentValue;
            var watermarkAnterior = _watermarkConfirmado;
            var agora = referencia.ToUniversalTime();
            var plano = PlanejarJanela(agora, watermarkAnterior, opcoes);
            var janelaInicio = plano.Inicio;
            var janelaFim = plano.Fim;
            _metrics.Query(plano.LagSegundos, (janelaFim - janelaInicio).TotalSeconds, plano.Modo);
            if (plano.Modo == GpsSppoCatchupMode.FastForward)
            {
                _logger.LogWarning(
                    "SPPO fast-forward por lag de {lag:F0}s acima do limite {limite}s; " +
                    "intervalo historico [{ignoradoInicio}, {ignoradoFim}] ({ignorado:F0}s) somente sera " +
                    "descartado apos resposta com DataHoraServidor valida; watermark permanece {watermark}.",
                    plano.LagSegundos, opcoes.MaxLagRecuperavelSegundos, watermarkAnterior,
                    janelaInicio, plano.IntervaloIgnoradoSegundos, watermarkAnterior);
            }
            var coletaIniciada = DateTimeOffset.UtcNow;
            var cronometro = Stopwatch.StartNew();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(opcoes.TimeoutSegundos));

            GpsSourceReadResult leitura;
            try
            {
                leitura = await ((IWindowedGpsSource)_source).GetPositionsAsync(
                    janelaInicio, janelaFim, timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                cronometro.Stop();
                _metrics.Timeout();
                _logger.LogWarning(
                    "Coleta SPPO excedeu timeout proprio de {timeout}s para janela [{inicio}, {fim}]; " +
                    "watermark {watermark}; nenhum lote publicado.",
                    opcoes.TimeoutSegundos, janelaInicio, janelaFim, watermarkAnterior);
                return ResultadoFonteGps.Falha("timeout_coletor", cronometro.Elapsed);
            }

            cronometro.Stop();
            var resultado = new ResultadoFonteGps(
                leitura.Status,
                leitura.Observations.Select(x => GpsObservationMapper.ToPosition(x, "ONIBUS")).ToArray(),
                leitura.Duration,
                leitura.FailureReason,
                leitura.SourceWatermark);
            if (resultado.Status == StatusFonteGps.Falha)
            {
                _metrics.Failure();
                _logger.LogWarning(
                    "Coleta SPPO falhou para janela [{inicio}, {fim}] em {duracao:F0}ms: {motivo}; " +
                    "watermark permanece {watermark}; nenhum lote publicado.",
                    janelaInicio, janelaFim, cronometro.Elapsed.TotalMilliseconds,
                    resultado.MotivoFalha, watermarkAnterior);
                return resultado;
            }

            var watermarkNovo = watermarkAnterior;
            if (resultado.WatermarkFonte is { } candidato
                && (!watermarkNovo.HasValue || candidato > watermarkNovo.Value))
            {
                watermarkNovo = candidato;
            }

            _watermarkConfirmado = watermarkNovo;
            var coletaConcluida = DateTimeOffset.UtcNow;
            if (plano.Modo == GpsSppoCatchupMode.FastForward
                && watermarkAnterior.HasValue
                && watermarkNovo > watermarkAnterior)
            {
                _metrics.FastForwardApplied(plano.IntervaloIgnoradoSegundos);
                _logger.LogWarning(
                    "SPPO fast-forward confirmado por DataHoraServidor: intervalo historico " +
                    "[{ignoradoInicio}, {ignoradoFim}] ({ignorado:F0}s) nao foi recuperado pelo pipeline " +
                    "operacional; watermark avancou de {anterior} para {novo}.",
                    watermarkAnterior, janelaInicio, plano.IntervaloIgnoradoSegundos,
                    watermarkAnterior, watermarkNovo);
            }
            _metrics.Success(coletaConcluida);
            var lag = watermarkNovo.HasValue ? coletaConcluida - watermarkNovo.Value : (TimeSpan?)null;
            var metricas = _metrics.Capture(coletaConcluida);

            _logger.LogInformation(
                "SPPO metrics: sppo_watermark_lag_seconds={watermarkLag} " +
                "sppo_query_window_seconds={queryWindow} sppo_catchup_mode={catchupMode} " +
                "sppo_catchup_chunks_total={catchupChunks} sppo_fast_forward_total={fastForward} " +
                "sppo_fast_forward_seconds_total={fastForwardSeconds} " +
                "sppo_collection_timeouts_total={timeouts} sppo_collection_failures_total={failures} " +
                "sppo_last_success_age_seconds={lastSuccessAge}.",
                metricas.WatermarkLagSeconds, metricas.QueryWindowSeconds, metricas.CatchupMode,
                metricas.CatchupChunksTotal, metricas.FastForwardTotal,
                metricas.FastForwardSecondsTotal, metricas.CollectionTimeoutsTotal,
                metricas.CollectionFailuresTotal, metricas.LastSuccessAgeSeconds);

            if (resultado.Posicoes.Count == 0)
            {
                _logger.LogInformation(
                    "Coleta SPPO concluida sem posicoes operacionais: resultado={resultado}, " +
                    "janela=[{inicio}, {fim}], duracao={duracao:F0}ms, watermark anterior={anterior}, " +
                    "watermark novo={novo}, lag={lag}; snapshot pendente preservado.",
                    resultado.Status, janelaInicio, janelaFim,
                    cronometro.Elapsed.TotalMilliseconds, watermarkAnterior, watermarkNovo, lag);
                return resultado;
            }

            if (_snapshot.Ler() is not null)
            {
                _logger.LogInformation(
                    "Coleta SPPO completa aguardando vaga no handoff; lote pendente sera preservado ate ACK.");
            }

            var lote = await _snapshot.PublicarAsync(
                janelaInicio,
                janelaFim,
                coletaIniciada,
                coletaConcluida,
                watermarkNovo,
                resultado.Posicoes,
                stoppingToken);

            _logger.LogInformation(
                "Coleta SPPO concluida: resultado={resultado}, geracao={geracao}, janela=[{inicio}, {fim}], " +
                "duracao={duracao:F0}ms, posicoes={posicoes}, watermark anterior={anterior}, " +
                "watermark novo={novo}, lag={lag}.",
                resultado.Status, lote.Geracao, janelaInicio, janelaFim,
                cronometro.Elapsed.TotalMilliseconds, resultado.Posicoes.Count,
                watermarkAnterior, watermarkNovo, lag);

            return resultado;
        }
        finally
        {
            _coletaEmAndamento.Release();
        }
    }

    internal static GpsSppoQueryWindow PlanejarJanela(DateTimeOffset agora,
        DateTimeOffset? watermarkConfirmado, GpsSppoCollectorOptions opcoes)
    {
        if (!watermarkConfirmado.HasValue)
            return new(agora.AddSeconds(-opcoes.JanelaInicialSegundos), agora,
                GpsSppoCatchupMode.Initial, null, 0);

        var watermark = watermarkConfirmado.Value.ToUniversalTime();
        var lagSegundos = Math.Max(0, (agora - watermark).TotalSeconds);
        if (lagSegundos > opcoes.MaxLagRecuperavelSegundos)
        {
            var inicio = agora.AddSeconds(-opcoes.JanelaInicialSegundos);
            return new(inicio, agora, GpsSppoCatchupMode.FastForward, lagSegundos,
                Math.Max(0, (inicio - watermark).TotalSeconds));
        }

        var fim = watermark.AddSeconds(opcoes.CatchupChunkSegundos);
        if (fim > agora) fim = agora;
        var inicioNormal = watermark.AddSeconds(-opcoes.OverlapSegundos);
        var modo = fim < agora ? GpsSppoCatchupMode.Catchup : GpsSppoCatchupMode.Normal;
        return new(inicioNormal, fim, modo, lagSegundos, 0);
    }
}

internal sealed record GpsSppoQueryWindow(DateTimeOffset Inicio, DateTimeOffset Fim,
    GpsSppoCatchupMode Modo, double? LagSegundos, double IntervaloIgnoradoSegundos);
