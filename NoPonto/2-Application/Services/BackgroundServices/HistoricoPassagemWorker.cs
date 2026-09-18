using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;
using StackExchange.Redis;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed record HistoricoStreamOptions(string Configuration);

public sealed class HistoricoPassagemWorker(IConnectionMultiplexer redis, IHistoricoEventoRepository repository,
    ILogger<HistoricoPassagemWorker> logger, HistoricoStreamOptions? leituraOptions = null) : BackgroundService
{
    public const string Group = "historico-passagens";
    public const string DeadLetter = "noponto:viagem:eventos:dead-letter";
    public string Consumer { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private RedisValue _claimCursor = "0-0";
    internal string StreamKey { get; init; } = ViagemOperacionalRepository.Stream;
    internal string DeadLetterKey { get; init; } = DeadLetter;
    internal static string Tentativas(RedisValue id) => $"noponto:viagem:evento:tentativas:{id}";
    internal static string UltimoErro(RedisValue id) => Tentativas(id) + ":erro";

    internal async Task GarantirGrupoAsync()
    {
        try { await redis.GetDatabase().StreamCreateConsumerGroupAsync(StreamKey, Group, "0-0", true); }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal)) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // BLOCK não pode ocupar a conexão multiplexada do commit GPS/viagem.
                var config = ConfigurationOptions.Parse(leituraOptions?.Configuration ?? redis.Configuration);
                config.AsyncTimeout = Math.Max(config.AsyncTimeout, 10000);
                using var leitura = await ConnectionMultiplexer.ConnectAsync(config);
                await GarantirGrupoAsync();
                while (!stoppingToken.IsCancellationRequested)
                {
                    await RecuperarPendentesAsync(stoppingToken);
                    var entries = await LerNovosAsync(leitura.GetDatabase(), stoppingToken);
                    foreach (var entry in entries) await ProcessarAsync(entry, stoppingToken);
                    await TrimSeguroAsync(DateTimeOffset.UtcNow);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Worker histórico indisponível; mensagens não confirmadas permanecem pendentes.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    internal async Task<StreamEntry[]> LerNovosAsync(IDatabase leitura, CancellationToken ct)
    {
        var response = await leitura.ExecuteAsync("XREADGROUP", "GROUP", Group, Consumer,
            "COUNT", 100, "BLOCK", 5000, "STREAMS", StreamKey, ">").WaitAsync(ct);
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
                    .Select(i => new NameValueEntry((string)pairs[2*i]!, (string)pairs[2*i+1]!)).ToArray());
            });
        }).ToArray();
    }

    internal async Task RecuperarPendentesAsync(CancellationToken ct, long idleMinimoMs = 60000)
    {
        var result = await redis.GetDatabase().StreamAutoClaimAsync(StreamKey, Group, Consumer,
            idleMinimoMs, _claimCursor, 100);
        _claimCursor = result.NextStartId;
        foreach (var entry in result.ClaimedEntries) await ProcessarAsync(entry, ct);
    }

    internal async Task ProcessarAsync(StreamEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = redis.GetDatabase();
        var previousAttempts = await db.StringGetAsync(Tentativas(entry.Id));
        if (!previousAttempts.IsNull && (long)previousAttempts >= 5)
        {
            var error = await db.StringGetAsync(UltimoErro(entry.Id));
            await EncaminharDeadLetterAsync(entry, error.IsNull ? "Limite de cinco falhas atingido." : error.ToString(), "limite-esgotado");
            return;
        }
        try
        {
            var fields = entry.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());
            await repository.PersistirAsync(EventoViagemValidator.Parse(fields), ct);
            ct.ThrowIfCancellationRequested();
            await db.StreamAcknowledgeAsync(StreamKey, Group, entry.Id);
            await db.KeyDeleteAsync([Tentativas(entry.Id), UltimoErro(entry.Id)]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var permanent = ex is FormatException or JsonException or ArgumentException or OverflowException
                || ex is PostgresException { SqlState: "23503" or "23514" or "22P02" };
            await db.StringSetAsync(UltimoErro(entry.Id), ex.ToString());
            var attempts = await db.StringIncrementAsync(Tentativas(entry.Id));
            logger.LogWarning(ex, "Evento {id}: tentativa {tentativa}/5, permanente={permanente}.", entry.Id, attempts, permanent);
            if (attempts >= 5)
            {
                await EncaminharDeadLetterAsync(entry, ex.ToString(), permanent ? "permanente" : "transitorio");
            }
        }
    }

    private Task<RedisResult> EncaminharDeadLetterAsync(StreamEntry entry, string error, string classe)
    {
        var payload = JsonSerializer.Serialize(entry.Values.Select(v => new { name = v.Name.ToString(), value = v.Value.ToString() }));
        return redis.GetDatabase().ScriptEvaluateAsync(DeadLetterScript,
            [StreamKey, DeadLetterKey, Tentativas(entry.Id), UltimoErro(entry.Id)], [Group, entry.Id, payload, error, classe]);
    }

    internal Task<RedisResult> TrimSeguroAsync(DateTimeOffset agora) => redis.GetDatabase().ScriptEvaluateAsync(
        TrimScript, [StreamKey], [$"{agora.AddDays(-7).ToUnixTimeMilliseconds()}-0"]);

    private const string DeadLetterScript = """
        #!lua
        local t = redis.call('TYPE', KEYS[2]).ok
        if t ~= 'none' and t ~= 'stream' then return redis.error_reply('INVALID_DEAD_LETTER_TYPE') end
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[2], ARGV[2], 1)
        if #pending == 0 then redis.call('DEL', KEYS[3], KEYS[4]); return 0 end
        redis.call('XADD', KEYS[2], '*', 'stream_id', ARGV[2], 'payload', ARGV[3], 'erro', ARGV[4], 'classe', ARGV[5])
        redis.call('XACK', KEYS[1], ARGV[1], ARGV[2])
        redis.call('DEL', KEYS[3], KEYS[4])
        return 1
        """;

    private const string TrimScript = """
        local kind = redis.call('TYPE', KEYS[1]).ok
        if kind ~= 'stream' then return 0 end
        local function less(a,b)
            local am,as = string.match(a, '^(%d+)%-(%d+)$')
            local bm,bs = string.match(b, '^(%d+)%-(%d+)$')
            if not am or not bm then return nil end
            if #am ~= #bm then return #am < #bm end
            if am ~= bm then return am < bm end
            if #as ~= #bs then return #as < #bs end
            return as < bs
        end
        local groups = redis.call('XINFO','GROUPS',KEYS[1])
        if #groups == 0 then return 0 end
        local cutoff = ARGV[1]
        for _,g in ipairs(groups) do
            local name, delivered
            for i = 1,#g,2 do
                if g[i] == 'name' then name = g[i+1] end
                if g[i] == 'last-delivered-id' then delivered = g[i+1] end
            end
            if not name or not delivered or less(delivered,cutoff) == nil then return 0 end
            if less(delivered,cutoff) then cutoff = delivered end
            local pending = redis.call('XPENDING',KEYS[1],name)
            if pending[1] > 0 then
                if not pending[2] or less(pending[2],cutoff) == nil then return 0 end
                if less(pending[2],cutoff) then cutoff = pending[2] end
            end
        end
        return redis.call('XTRIM',KEYS[1],'MINID','=',cutoff)
        """;
}
