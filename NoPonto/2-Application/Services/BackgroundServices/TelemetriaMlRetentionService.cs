using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using StackExchange.Redis;

namespace NoPonto.Application.Services.BackgroundServices;

public sealed class TelemetriaMlRetentionOptions
{
    public const string Secao = "TelemetriaMlRetention";
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 5;
    public int MainStreamSafetyMarginMinutes { get; set; } = 60;
    public int DeadLetterRetentionDays { get; set; } = 7;
    public int TrimLimit { get; set; } = 100_000;
}

public sealed class TelemetriaMlRetentionMetrics
{
    private long _ciclos, _semTrim, _failClosed, _removidosPrincipal, _removidosDlq;
    private long _duracaoTicks, _falhasRedis, _pending, _lag, _xlen, _idadeMaisAntigaMs;
    private long _cutoffTemporalMs, _cutoffEfetivoMs, _ultimoSucessoUnixMs;

    public long Ciclos => Interlocked.Read(ref _ciclos);
    public long CiclosSemTrim => Interlocked.Read(ref _semTrim);
    public long CiclosFailClosed => Interlocked.Read(ref _failClosed);
    public long RemovidosPrincipal => Interlocked.Read(ref _removidosPrincipal);
    public long RemovidosDlq => Interlocked.Read(ref _removidosDlq);
    public double DuracaoTotalMs => TimeSpan.FromTicks(Interlocked.Read(ref _duracaoTicks)).TotalMilliseconds;
    public long FalhasRedis => Interlocked.Read(ref _falhasRedis);
    public long PendingObservado => Interlocked.Read(ref _pending);
    public long LagObservado => Interlocked.Read(ref _lag);
    public long XlenObservado => Interlocked.Read(ref _xlen);
    public long IdadeEntradaMaisAntigaMs => Interlocked.Read(ref _idadeMaisAntigaMs);
    public long CutoffTemporalMs => Interlocked.Read(ref _cutoffTemporalMs);
    public long CutoffEfetivoMs => Interlocked.Read(ref _cutoffEfetivoMs);
    public DateTimeOffset? UltimoSucesso => Interlocked.Read(ref _ultimoSucessoUnixMs) is var ms && ms > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    internal void Registrar(RetentionCycleResult resultado, TimeSpan duracao)
    {
        Interlocked.Increment(ref _ciclos);
        Interlocked.Add(ref _duracaoTicks, duracao.Ticks);
        Interlocked.Exchange(ref _pending, resultado.Pending);
        Interlocked.Exchange(ref _lag, resultado.Lag);
        Interlocked.Exchange(ref _xlen, resultado.Xlen);
        Interlocked.Exchange(ref _idadeMaisAntigaMs, resultado.IdadeMaisAntigaMs);
        Interlocked.Exchange(ref _cutoffTemporalMs, resultado.CutoffTemporalMs);
        Interlocked.Exchange(ref _cutoffEfetivoMs, resultado.CutoffEfetivoMs);
        if (resultado.FailClosed) Interlocked.Increment(ref _failClosed);
        if (resultado.RemovidosPrincipal == 0 && resultado.RemovidosDlq == 0) Interlocked.Increment(ref _semTrim);
        Interlocked.Add(ref _removidosPrincipal, resultado.RemovidosPrincipal);
        Interlocked.Add(ref _removidosDlq, resultado.RemovidosDlq);
        if (!resultado.FailClosed) Interlocked.Exchange(ref _ultimoSucessoUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal void RegistrarFalhaRedis(TimeSpan duracao)
    {
        Interlocked.Increment(ref _ciclos);
        Interlocked.Increment(ref _failClosed);
        Interlocked.Increment(ref _falhasRedis);
        Interlocked.Add(ref _duracaoTicks, duracao.Ticks);
    }
}

internal readonly record struct RetentionCycleResult(
    string Status, long RemovidosPrincipal, long RemovidosDlq, long Pending, long Lag,
    long Xlen, long IdadeMaisAntigaMs, long CutoffTemporalMs, long CutoffEfetivoMs)
{
    public bool FailClosed => Status.StartsWith("FAIL_", StringComparison.Ordinal);
}

public sealed class TelemetriaMlRetentionService(
    IConnectionMultiplexer redis,
    IOptions<TelemetriaMlRetentionOptions> options,
    TelemetriaMlRetentionMetrics metrics,
    ILogger<TelemetriaMlRetentionService> logger) : BackgroundService
{
    internal string StreamKey { get; set; } = TelemetriaMlContrato.Stream;
    internal string ExpectedGroup { get; set; } = TelemetriaMlContrato.Group;
    internal string DeadLetterKey { get; set; } = TelemetriaMlContrato.DeadLetter;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ExecutarCicloSeguroAsync(DateTimeOffset.UtcNow, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogWarningSeguro(ex, "Serviço de retenção ML encerrou uma falha inesperada sem propagá-la ao host.");
        }
    }

    internal async Task<RetentionCycleResult> ExecutarCicloSeguroAsync(DateTimeOffset agora, CancellationToken ct = default)
    {
        var inicio = Stopwatch.GetTimestamp();
        try
        {
            var config = options.Value;
            var temporalMs = agora.Subtract(TimeSpan.FromMinutes(config.MainStreamSafetyMarginMinutes))
                .ToUnixTimeMilliseconds();
            var dlqMs = agora.Subtract(TimeSpan.FromDays(config.DeadLetterRetentionDays)).ToUnixTimeMilliseconds();
            var db = redis.GetDatabase();
            var principalRaw = await db.ScriptEvaluateAsync(MainRetentionScript, [StreamKey],
                [ExpectedGroup, temporalMs, config.TrimLimit]).WaitAsync(ct);
            var principal = ParsePrincipal(principalRaw, agora, temporalMs);
            if (principal.FailClosed)
            {
                metrics.Registrar(principal, Stopwatch.GetElapsedTime(inicio));
                LogWarningSeguro(null, "Retenção ML fail-closed no Stream principal: {Status}.", principal.Status);
                return principal;
            }

            var dlqRaw = await db.ScriptEvaluateAsync(DeadLetterRetentionScript, [DeadLetterKey],
                [dlqMs, config.TrimLimit]).WaitAsync(ct);
            var (statusDlq, removidosDlq) = ParseDlq(dlqRaw);
            var resultado = statusDlq.StartsWith("FAIL_", StringComparison.Ordinal)
                ? principal with { Status = statusDlq }
                : principal with { RemovidosDlq = removidosDlq };
            metrics.Registrar(resultado, Stopwatch.GetElapsedTime(inicio));
            if (resultado.FailClosed)
                LogWarningSeguro(null, "Retenção ML fail-closed na DLQ: {Status}.", resultado.Status);
            else if (resultado.RemovidosPrincipal > 0 || resultado.RemovidosDlq > 0)
                LogInformationSeguro(
                    "Retenção ML removeu principal={Principal}, dlq={Dlq}, cutoff={Cutoff}, xlen_anterior={Xlen}, pending={Pending}, lag={Lag}.",
                    resultado.RemovidosPrincipal, resultado.RemovidosDlq, resultado.CutoffEfetivoMs,
                    resultado.Xlen, resultado.Pending, resultado.Lag);
            return resultado;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            metrics.RegistrarFalhaRedis(Stopwatch.GetElapsedTime(inicio));
            LogWarningSeguro(ex, "Retenção ML falhou; ciclo encerrado sem afetar ingestão.");
            return new("FAIL_REDIS", 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    private void LogWarningSeguro(Exception? ex, string mensagem, params object?[] args)
    {
        try { logger.LogWarning(ex, mensagem, args); }
        catch { /* Logging ML nunca pode encerrar o hosted service. */ }
    }

    private void LogInformationSeguro(string mensagem, params object?[] args)
    {
        try { logger.LogInformation(mensagem, args); }
        catch { /* Logging ML nunca pode encerrar o hosted service. */ }
    }

    private static RetentionCycleResult ParsePrincipal(RedisResult raw, DateTimeOffset agora, long temporalMs)
    {
        var values = (RedisResult[]?)raw;
        if (values is null || values.Length != 7) return new("FAIL_UNEXPECTED_RESPONSE", 0, 0, 0, 0, 0, 0, temporalMs, 0);
        var status = (string?)values[0] ?? "FAIL_UNEXPECTED_RESPONSE";
        if (!long.TryParse(values[1].ToString(), out var removidos)
            || !long.TryParse(values[2].ToString(), out var cutoff)
            || !long.TryParse(values[3].ToString(), out var pending)
            || !long.TryParse(values[4].ToString(), out var lag)
            || !long.TryParse(values[5].ToString(), out var xlen)
            || !long.TryParse(values[6].ToString(), out var oldestMs))
            return new("FAIL_UNEXPECTED_RESPONSE", 0, 0, 0, 0, 0, 0, temporalMs, 0);
        var idade = oldestMs > 0 ? Math.Max(0, agora.ToUnixTimeMilliseconds() - oldestMs) : 0;
        return new(status, removidos, 0, pending, lag, xlen, idade, temporalMs, cutoff);
    }

    private static (string Status, long Removidos) ParseDlq(RedisResult raw)
    {
        var values = (RedisResult[]?)raw;
        if (values is null || values.Length != 2 || !long.TryParse(values[1].ToString(), out var removidos))
            return ("FAIL_UNEXPECTED_DLQ_RESPONSE", 0);
        return ((string?)values[0] ?? "FAIL_UNEXPECTED_DLQ_RESPONSE", removidos);
    }

    // Redis 7.4: cálculo e XTRIM ficam na mesma execução atômica. Não usa ACKED/DELREF (Redis 8.2).
    private const string MainRetentionScript = """
        local function fail(code) return {code, 0, 0, 0, 0, 0, 0} end
        local function fields(a)
            local m = {}
            if type(a) ~= 'table' or (#a % 2) ~= 0 then return nil end
            for i = 1, #a, 2 do m[a[i]] = a[i + 1] end
            return m
        end
        local function parse_id(id)
            if type(id) ~= 'string' then return nil end
            local dash = string.find(id, '-', 1, true)
            if not dash then return nil end
            local ms = tonumber(string.sub(id, 1, dash - 1))
            local seq = tonumber(string.sub(id, dash + 1))
            if not ms or not seq or ms < 0 or seq < 0 then return nil end
            return ms, seq
        end
        local function less(a, b)
            local am, as = parse_id(a); local bm, bs = parse_id(b)
            if not am or not bm then return nil end
            return am < bm or (am == bm and as < bs)
        end
        local function minimum(a, b)
            local value = less(a, b)
            if value == nil then return nil end
            return value and a or b
        end
        local function successor(id)
            local ms, seq = parse_id(id)
            if not ms then return nil end
            return tostring(ms) .. '-' .. tostring(seq + 1)
        end

        local kind = redis.call('TYPE', KEYS[1]).ok
        if kind == 'none' then return {'EMPTY', 0, 0, 0, 0, 0, 0} end
        if kind ~= 'stream' then return fail('FAIL_INVALID_TYPE') end
        local groups = redis.call('XINFO', 'GROUPS', KEYS[1])
        if type(groups) ~= 'table' or #groups == 0 then return fail('FAIL_NO_GROUPS') end
        local expected = false
        local safe = nil
        local total_pending = 0
        local max_lag = 0
        for _, raw in ipairs(groups) do
            local g = fields(raw)
            if not g or type(g['name']) ~= 'string' then return fail('FAIL_INVALID_GROUP') end
            if g['name'] == ARGV[1] then expected = true end
            local delivered = g['last-delivered-id']
            local dm, ds = parse_id(delivered)
            if not dm or (dm == 0 and ds == 0) then return fail('FAIL_INVALID_PROGRESS') end
            local lag = tonumber(g['lag'])
            if not lag or lag < 0 then return fail('FAIL_INVALID_LAG') end
            if lag > max_lag then max_lag = lag end
            local pending = redis.call('XPENDING', KEYS[1], g['name'])
            if type(pending) ~= 'table' or #pending < 4 then return fail('FAIL_INVALID_PENDING') end
            local count = tonumber(pending[1])
            if not count or count < 0 then return fail('FAIL_INVALID_PENDING') end
            total_pending = total_pending + count
            local progress = successor(delivered)
            if not progress then return fail('FAIL_INVALID_PROGRESS') end
            if count > 0 then
                local lowest = pending[2]
                if not parse_id(lowest) then return fail('FAIL_INVALID_PENDING') end
                progress = minimum(progress, lowest)
                if not progress then return fail('FAIL_INVALID_PENDING') end
            end
            safe = safe and minimum(safe, progress) or progress
            if not safe then return fail('FAIL_INVALID_PROGRESS') end
        end
        if not expected then return fail('FAIL_EXPECTED_GROUP_MISSING') end
        local temporal_ms = tonumber(ARGV[2]); local limit = tonumber(ARGV[3])
        if not temporal_ms or temporal_ms <= 0 or not limit or limit <= 0 then return fail('FAIL_INVALID_ARGUMENT') end
        local temporal = tostring(temporal_ms) .. '-0'
        local cutoff = minimum(temporal, safe)
        if not cutoff then return fail('FAIL_INVALID_CUTOFF') end
        local info = redis.call('XINFO', 'STREAM', KEYS[1])
        local stream = fields(info)
        if not stream or not tonumber(stream['length']) then return fail('FAIL_INVALID_STREAM_INFO') end
        local oldest = 0
        local first = redis.call('XRANGE', KEYS[1], '-', '+', 'COUNT', 1)
        if #first > 0 then
            local fm = parse_id(first[1][1]); if not fm then return fail('FAIL_INVALID_FIRST_ID') end
            oldest = fm
        end
        local removed = redis.call('XTRIM', KEYS[1], 'MINID', '~', cutoff, 'LIMIT', limit)
        return {'OK', removed, parse_id(cutoff), total_pending, max_lag, tonumber(stream['length']), oldest}
        """;

    // A DLQ é diagnóstica e hoje não possui consumer group. Se um surgir, falha fechado para revisão da política.
    private const string DeadLetterRetentionScript = """
        local kind = redis.call('TYPE', KEYS[1]).ok
        if kind == 'none' then return {'EMPTY_DLQ', 0} end
        if kind ~= 'stream' then return {'FAIL_DLQ_INVALID_TYPE', 0} end
        local groups = redis.call('XINFO', 'GROUPS', KEYS[1])
        if type(groups) ~= 'table' or #groups > 0 then return {'FAIL_DLQ_HAS_GROUP', 0} end
        local cutoff = tonumber(ARGV[1]); local limit = tonumber(ARGV[2])
        if not cutoff or cutoff <= 0 or not limit or limit <= 0 then return {'FAIL_DLQ_INVALID_ARGUMENT', 0} end
        local removed = redis.call('XTRIM', KEYS[1], 'MINID', '~', tostring(cutoff) .. '-0', 'LIMIT', limit)
        return {'OK_DLQ', removed}
        """;
}
