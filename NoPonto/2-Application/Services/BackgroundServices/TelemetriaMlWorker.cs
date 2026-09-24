using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using StackExchange.Redis;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed class TelemetriaMlWorker(
    IConnectionMultiplexer redis,
    ITelemetriaMlRepository repository,
    TelemetriaMlMetrics metrics,
    ILogger<TelemetriaMlWorker> logger,
    IOptions<TelemetriaMlRetentionOptions>? retentionOptions = null) : BackgroundService
{
    public const int TamanhoMaximoLote = 100;
    public const int MaxTentativas = 5;
    internal string StreamKey { get; init; } = TelemetriaMlContrato.Stream;
    internal string GroupKey { get; init; } = TelemetriaMlContrato.Group;
    internal string DeadLetterKey { get; init; } = TelemetriaMlContrato.DeadLetter;
    internal string Consumer { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private RedisValue _claimCursor = "0-0";

    internal static string Tentativas(RedisValue id) => $"noponto:ml:telemetria:tentativas:{id}";
    internal static string UltimoErro(RedisValue id) => Tentativas(id) + ":erro";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await GarantirGrupoAsync();
                var config = ConfigurationOptions.Parse(redis.Configuration);
                config.AsyncTimeout = Math.Max(config.AsyncTimeout, 10_000);
                using var leitura = await ConnectionMultiplexer.ConnectAsync(config);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await RecuperarPendentesAsync(stoppingToken);
                    var entries = await LerNovosAsync(leitura.GetDatabase(), stoppingToken);
                    if (entries.Length > 0) await ProcessarLoteAsync(entries, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LogWarningSeguro(ex, "Worker de telemetria ML indisponível; mensagens sem ACK permanecem pendentes.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    internal async Task GarantirGrupoAsync()
    {
        try
        {
            await redis.GetDatabase().StreamCreateConsumerGroupAsync(StreamKey, GroupKey, "0-0", true);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal)) { }
    }

    internal async Task<StreamEntry[]> LerNovosAsync(IDatabase leitura, CancellationToken ct)
    {
        var inicio = Stopwatch.GetTimestamp();
        var response = await leitura.ExecuteAsync("XREADGROUP", "GROUP", GroupKey, Consumer,
            "COUNT", TamanhoMaximoLote, "BLOCK", 1000, "STREAMS", StreamKey, ">").WaitAsync(ct);
        metrics.RegistrarWorkerLeitura(Stopwatch.GetElapsedTime(inicio));
        return ParseRead(response);
    }

    internal async Task RecuperarPendentesAsync(CancellationToken ct, long idleMinimoMs = 30_000)
    {
        var result = await redis.GetDatabase().StreamAutoClaimAsync(
            StreamKey, GroupKey, Consumer, idleMinimoMs, _claimCursor, TamanhoMaximoLote);
        _claimCursor = result.NextStartId;
        if (result.ClaimedEntries.Length > 0)
            await ProcessarLoteAsync(result.ClaimedEntries, ct, limparRetry: true);
    }

    internal async Task ProcessarLoteAsync(StreamEntry[] entries, CancellationToken ct,
        bool limparRetry = false)
    {
        ct.ThrowIfCancellationRequested();
        metrics.RegistrarConsumidos(entries.Length);
        var validos = new List<(StreamEntry Entry, EventoTelemetriaMl Evento)>(entries.Length);
        var inicioDesserializacao = Stopwatch.GetTimestamp();
        foreach (var entry in entries)
        {
            try
            {
                var payload = entry.Values.FirstOrDefault(v => v.Name == "payload").Value;
                if (payload.IsNull) throw new FormatException("Payload ausente.");
                var evento = JsonSerializer.Deserialize<EventoTelemetriaMl>(payload!)
                    ?? throw new FormatException("Payload vazio.");
                TelemetriaMlValidator.Validar(evento);
                validos.Add((entry, evento));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
            {
                metrics.RegistrarInvalido();
                await EncaminharDeadLetterAsync(entry, ex.Message, "invalido");
            }
        }
        metrics.RegistrarWorkerDesserializacao(Stopwatch.GetElapsedTime(inicioDesserializacao));

        if (validos.Count == 0) return;
        var inicio = Stopwatch.GetTimestamp();
        try
        {
            var inicioPostgres = Stopwatch.GetTimestamp();
            var resultado = await repository.PersistirLoteAsync(validos.Select(v => v.Evento).ToArray(), ct);
            metrics.RegistrarWorkerPostgres(Stopwatch.GetElapsedTime(inicioPostgres));
            ct.ThrowIfCancellationRequested();
            var ids = validos.Select(v => v.Entry.Id).ToArray();
            var inicioAck = Stopwatch.GetTimestamp();
            await redis.GetDatabase().StreamAcknowledgeAsync(StreamKey, GroupKey, ids);
            metrics.RegistrarWorkerAck(Stopwatch.GetElapsedTime(inicioAck));
            if (limparRetry)
            {
                var inicioCleanup = Stopwatch.GetTimestamp();
                var chaves = ids.SelectMany(id => new RedisKey[] { Tentativas(id), UltimoErro(id) }).ToArray();
                if (chaves.Length > 0) await redis.GetDatabase().KeyDeleteAsync(chaves);
                metrics.RegistrarWorkerCleanup(Stopwatch.GetElapsedTime(inicioCleanup));
            }
            metrics.RegistrarPersistencia(resultado.Persistidos, resultado.Duplicados,
                validos.Count, Stopwatch.GetElapsedTime(inicio));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            metrics.RegistrarRetry(validos.Count);
            foreach (var item in validos)
            {
                var db = redis.GetDatabase();
                await db.StringSetAsync(UltimoErro(item.Entry.Id), ex.ToString(), TimeSpan.FromDays(7));
                var tentativas = await db.StringIncrementAsync(Tentativas(item.Entry.Id));
                await db.KeyExpireAsync(Tentativas(item.Entry.Id), TimeSpan.FromDays(7));
                if (tentativas >= MaxTentativas)
                    await EncaminharDeadLetterAsync(item.Entry, ex.ToString(), "persistencia");
            }
            LogWarningSeguro(ex,
                "Falha ao persistir lote de {quantidade} telemetrias ML; mensagens permanecem pendentes.",
                validos.Count);
        }
    }

    internal async Task EncaminharDeadLetterAsync(StreamEntry entry, string erro, string classe)
    {
        var payload = entry.Values.FirstOrDefault(v => v.Name == "payload").Value;
        var result = await redis.GetDatabase().ScriptEvaluateAsync(DeadLetterScript,
            [StreamKey, DeadLetterKey, Tentativas(entry.Id), UltimoErro(entry.Id)],
            [GroupKey, entry.Id, payload.IsNull ? "" : payload, erro, classe,
                (retentionOptions?.Value ?? new TelemetriaMlRetentionOptions()).MaxDeadLetterEntries]);
        if ((long)result == 1) metrics.RegistrarDeadLetter();
    }

    private void LogWarningSeguro(Exception ex, string mensagem, params object?[] args)
    {
        try { logger.LogWarning(ex, mensagem, args); }
        catch { /* Logging ML nunca pode encerrar o hosted service. */ }
    }

    private static StreamEntry[] ParseRead(RedisResult response)
    {
        if (response.IsNull) return [];
        var streams = (RedisResult[])response!;
        return streams.SelectMany(stream =>
        {
            var values = (RedisResult[])stream!;
            return ((RedisResult[])values[1]!).Select(entry =>
            {
                var fields = (RedisResult[])entry!;
                var pairs = (RedisResult[])fields[1]!;
                return new StreamEntry((string)fields[0]!, Enumerable.Range(0, pairs.Length / 2)
                    .Select(i => new NameValueEntry((string)pairs[2 * i]!, (string)pairs[2 * i + 1]!))
                    .ToArray());
            });
        }).ToArray();
    }

    private const string DeadLetterScript = """
        local t = redis.call('TYPE', KEYS[2]).ok
        if t ~= 'none' and t ~= 'stream' then return redis.error_reply('INVALID_DLQ_TYPE') end
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[2], ARGV[2], 1)
        if #pending == 0 then redis.call('DEL', KEYS[3], KEYS[4]); return 0 end
        local maximum=tonumber(ARGV[6])
        if not maximum or maximum<=0 then return redis.error_reply('INVALID_DLQ_MAXLEN') end
        redis.call('XADD', KEYS[2], 'MAXLEN', '~', maximum, '*', 'stream_id', ARGV[2], 'payload', ARGV[3], 'erro', ARGV[4], 'classe', ARGV[5])
        redis.call('XACK', KEYS[1], ARGV[1], ARGV[2])
        redis.call('DEL', KEYS[3], KEYS[4])
        return 1
        """;
}
