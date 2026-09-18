using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoPonto.Application.GPS;
using StackExchange.Redis;

namespace NoPonto.Application.Services.BackgroundServices;

/// <summary>
/// O polling faz somente TryWrite. Redis e serialização são executados fora do
/// caminho crítico; saturação/falha descarta apenas telemetria secundária.
/// </summary>
public sealed class TelemetriaMlStreamPublisher : BackgroundService, ITelemetriaMlIngress
{
    internal const int Capacidade = 10_000;
    internal const int TamanhoMaximoLote = 100;
    internal static readonly TimeSpan EsperaMaximaLote = TimeSpan.FromMilliseconds(5);
    internal static readonly TimeSpan EsperaAposFalhaInesperada = TimeSpan.FromMilliseconds(100);
    internal string StreamKey { get; init; } = TelemetriaMlContrato.Stream;
    internal Func<NameValueEntry[], Task<RedisValue>>? PublicarOverride { get; init; }
    internal Func<EventoTelemetriaMl, string> Serializar { get; init; } =
        static evento => JsonSerializer.Serialize(evento);
    private readonly Channel<EventoTelemetriaMl> _canal = Channel.CreateBounded<EventoTelemetriaMl>(
        new BoundedChannelOptions(Capacidade)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly IConnectionMultiplexer _redis;
    private readonly TelemetriaMlMetrics _metrics;
    private readonly ILogger<TelemetriaMlStreamPublisher> _logger;

    public TelemetriaMlStreamPublisher(IConnectionMultiplexer redis, TelemetriaMlMetrics metrics,
        ILogger<TelemetriaMlStreamPublisher> logger)
    {
        _redis = redis;
        _metrics = metrics;
        _logger = logger;
    }

    public bool TentarPublicar(EventoTelemetriaMl evento)
    {
        _metrics.RegistrarEntradaChannel();
        if (_canal.Writer.TryWrite(evento))
        {
            _metrics.RegistrarProduzido();
            return true;
        }
        _metrics.DesfazerEntradaChannel();
        _metrics.RegistrarFalhaChannel();
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!await _canal.Reader.WaitToReadAsync(stoppingToken)) break;
                    var lote = await LerMicrobatchAsync(stoppingToken);
                    if (lote.Count > 0) await PublicarMicrobatchAsync(lote);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    RegistrarFalhaInesperadaSeguro();
                    LogErroSeguro(ex,
                        "Falha inesperada no publisher de telemetria ML; o lote foi descartado e o serviço continuará.");
                    try { await Task.Delay(EsperaAposFalhaInesperada, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Última barreira fail-open: nenhuma falha do publisher pode alcançar o host.
            RegistrarFalhaInesperadaSeguro();
            LogErroSeguro(ex, "Publisher de telemetria ML foi encerrado após falha inesperada contida.");
        }
    }

    internal async Task<List<EventoTelemetriaMl>> LerMicrobatchAsync(CancellationToken ct)
    {
        var lote = new List<EventoTelemetriaMl>(TamanhoMaximoLote);
        while (lote.Count < TamanhoMaximoLote && _canal.Reader.TryRead(out var evento)) lote.Add(evento);
        if (lote.Count == 0) return lote;

        var limite = Stopwatch.GetTimestamp() + (long)(EsperaMaximaLote.TotalSeconds * Stopwatch.Frequency);
        while (lote.Count < TamanhoMaximoLote)
        {
            while (lote.Count < TamanhoMaximoLote && _canal.Reader.TryRead(out var evento)) lote.Add(evento);
            if (lote.Count == TamanhoMaximoLote) break;
            var restante = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), limite);
            if (restante <= TimeSpan.Zero) break;
            using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct);
            espera.CancelAfter(restante);
            try { if (!await _canal.Reader.WaitToReadAsync(espera.Token)) break; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
        }
        _metrics.RegistrarSaidaChannel(lote.Count);
        return lote;
    }

    internal async Task PublicarMicrobatchAsync(IReadOnlyList<EventoTelemetriaMl> lote)
    {
        var inicioSerializacao = Stopwatch.GetTimestamp();
        var values = new List<NameValueEntry[]>(lote.Count);
        foreach (var evento in lote)
        {
            try
            {
                values.Add([
                    new("observacao_id", evento.ObservacaoId),
                    new("payload", Serializar(evento)),
                ]);
            }
            catch (Exception ex)
            {
                RegistrarFalhaPreparacaoSeguro();
                LogErroSeguro(ex,
                    "Falha local ao preparar observação {ObservacaoId} para o Stream ML; somente o evento foi descartado.",
                    evento.ObservacaoId);
            }
        }
        var serializacao = Stopwatch.GetElapsedTime(inicioSerializacao);
        var inicioRedis = Stopwatch.GetTimestamp();
        var tasks = new List<Task<RedisValue>>(values.Count);
        var falhasSincronasRedis = 0;

        IDatabase? db = null;
        if (PublicarOverride is null && values.Count > 0)
        {
            try { db = _redis.GetDatabase(); }
            catch (Exception ex)
            {
                falhasSincronasRedis = values.Count;
                LogErroSeguro(ex,
                    "Falha ao obter conexão Redis para microbatch ML; {Falhas} eventos foram descartados.",
                    falhasSincronasRedis);
            }
        }

        if (falhasSincronasRedis == 0)
        {
            foreach (var value in values)
            {
                try
                {
                    var task = PublicarOverride?.Invoke(value) ?? db!.StreamAddAsync(StreamKey, value);
                    tasks.Add(task ?? Task.FromException<RedisValue>(
                        new InvalidOperationException("Operação Redis retornou Task nula.")));
                }
                catch (Exception ex)
                {
                    falhasSincronasRedis++;
                    LogErroSeguro(ex,
                        "Falha síncrona ao preparar XADD de telemetria ML; somente o evento foi descartado.");
                }
            }
        }

        try { await Task.WhenAll(tasks); }
        catch { /* Todas as tasks são inspecionadas abaixo; sucessos não são republicados. */ }

        var publicados = tasks.Count(t => t.IsCompletedSuccessfully);
        var falhas = falhasSincronasRedis + tasks.Count - publicados;
        _metrics.RegistrarMicrobatch(lote.Count, serializacao,
            Stopwatch.GetElapsedTime(inicioRedis), publicados, falhas);
        if (falhas == 0) return;

        for (var i = 0; i < falhas; i++) _metrics.RegistrarFalhaPublicacao();
        var erro = tasks.FirstOrDefault(t => t.IsFaulted)?.Exception?.GetBaseException();
        LogErroSeguro(erro,
            "Falha parcial ao publicar microbatch de telemetria ML: publicados={Publicados}, falhas={Falhas}; sucessos não serão republicados.",
            publicados, falhas);
    }

    private void RegistrarFalhaPreparacaoSeguro()
    {
        try
        {
            _metrics.RegistrarFalhaPreparacaoPublisher();
            _metrics.RegistrarFalhaPublicacao();
        }
        catch { /* Métricas nunca podem comprometer o publisher fail-open. */ }
    }

    private void RegistrarFalhaInesperadaSeguro()
    {
        try
        {
            _metrics.RegistrarFalhaInesperadaPublisher();
            _metrics.RegistrarFalhaPublicacao();
        }
        catch { /* Métricas nunca podem comprometer o publisher fail-open. */ }
    }

    private void LogErroSeguro(Exception? ex, string mensagem, params object?[] args)
    {
        try { _logger.LogError(ex, mensagem, args); }
        catch { /* Logging de telemetria também é secundário ao host. */ }
    }
}
