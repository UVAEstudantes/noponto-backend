using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed class GpsSppoCollectorService : BackgroundService
{
    private readonly GpsSppoClient _cliente;
    private readonly GpsSppoSnapshotStore _snapshot;
    private readonly IOptionsMonitor<GpsSppoCollectorOptions> _opcoesMonitor;
    private readonly ILogger<GpsSppoCollectorService> _logger;
    private readonly SemaphoreSlim _coletaEmAndamento = new(1, 1);
    private DateTimeOffset? _watermarkConfirmado;

    public GpsSppoCollectorService(
        GpsSppoClient cliente,
        GpsSppoSnapshotStore snapshot,
        IOptionsMonitor<GpsSppoCollectorOptions> opcoesMonitor,
        ILogger<GpsSppoCollectorService> logger)
    {
        _cliente = cliente;
        _snapshot = snapshot;
        _opcoesMonitor = opcoesMonitor;
        _logger = logger;
    }

    internal DateTimeOffset? WatermarkConfirmado => _watermarkConfirmado;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var iniciais = _opcoesMonitor.CurrentValue;
        _logger.LogInformation(
            "GpsSppoCollectorService iniciado: timeout {timeout}s, janela inicial {janela}s, " +
            "overlap {overlap}s, intervalo {intervalo}s.",
            iniciais.TimeoutSegundos, iniciais.JanelaInicialSegundos,
            iniciais.OverlapSegundos, iniciais.IntervaloEntreColetasSegundos);

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
            var opcoes = _opcoesMonitor.CurrentValue;
            var watermarkAnterior = _watermarkConfirmado;
            var janelaFim = referencia.ToUniversalTime();
            var janelaInicio = watermarkAnterior.HasValue
                ? watermarkAnterior.Value.AddSeconds(-opcoes.OverlapSegundos)
                : janelaFim.AddSeconds(-opcoes.JanelaInicialSegundos);
            var coletaIniciada = DateTimeOffset.UtcNow;
            var cronometro = Stopwatch.StartNew();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(opcoes.TimeoutSegundos));

            ResultadoFonteGps resultado;
            try
            {
                resultado = await _cliente.BuscarResultadoPorIntervaloAsync(
                    janelaInicio, janelaFim, timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                cronometro.Stop();
                _logger.LogWarning(
                    "Coleta SPPO excedeu timeout proprio de {timeout}s para janela [{inicio}, {fim}]; " +
                    "watermark {watermark}; nenhum lote publicado.",
                    opcoes.TimeoutSegundos, janelaInicio, janelaFim, watermarkAnterior);
                return ResultadoFonteGps.Falha("timeout_coletor", cronometro.Elapsed);
            }

            cronometro.Stop();
            if (resultado.Status == StatusFonteGps.Falha)
            {
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
            var lag = watermarkNovo.HasValue ? coletaConcluida - watermarkNovo.Value : (TimeSpan?)null;

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
}
