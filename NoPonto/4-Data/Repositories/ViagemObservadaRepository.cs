using System.Globalization;
using Microsoft.Extensions.Logging;
using NoPonto.Application.GPS;
using NoPonto.Data.Interfaces;
using StackExchange.Redis;

namespace NoPonto.Data.Repositories;

/// <summary>Snapshot SQL + CAS de uma chave; um único HSET, sem TTL ou compensação.</summary>
public sealed class ViagemObservadaRepository(
    IConnectionMultiplexer redis, ILogger<ViagemObservadaRepository> logger,
    IOcorrenciaParadaRepository ocorrencias) : IViagemObservadaRepository
{
    // Três tentativas totais; cada conflito exige nova leitura e nova consulta SQL.
    internal const int MaxTentativas = 3;
    private const string Script = """
        #!lua
        local CREATED, UPDATED, OLDER, CHANGED, INVALID, CONFLICT = 1, 2, 3, 4, 5, 7
        local empty = '00000000000000000000000000000000'
        local function guid(v)
            return v and #v == 32 and string.match(v, '^[0-9a-f]+$') and v ~= empty
        end
        local function timestamp(v)
            return v and #v == 19 and string.match(v, '^%d+$')
                and v > '0621355968000000000' and v <= '3155378975999999999' and v <= ARGV[6]
        end
        local function progress(v)
            local n = v and tonumber(v)
            return n and n == n and n >= 0 and n <= 1
        end
        local function cursor(id, order)
            local n = order and tonumber(order)
            return n and string.match(order, '^%d+$') and n <= 2147483647
                and ((n == 0 and id == empty) or (n > 0 and guid(id)))
        end
        if #KEYS ~= 1 or KEYS[1] == '' or #ARGV ~= 18
            or #ARGV[6] ~= 19 or not string.match(ARGV[6], '^%d+$')
            or not guid(ARGV[1]) or ARGV[2] == '' or not guid(ARGV[3])
            or not timestamp(ARGV[4]) or not progress(ARGV[5])
            or (ARGV[7] ~= 'read' and ARGV[7] ~= 'write') then return {INVALID} end
        local kind = redis.call('TYPE', KEYS[1]).ok
        if kind ~= 'none' and kind ~= 'hash' then return {INVALID} end
        local names = {'ViagemId', 'OrdemVeiculo', 'ItinerarioId', 'TimestampObservacaoInicial',
            'TimestampUltimaAtualizacao', 'PosicaoNaRotaConfirmada',
            'UltimaParadaItinerarioId', 'UltimaParadaOrdem'}
        local state = nil
        if kind == 'hash' then
            state = redis.call('HMGET', KEYS[1], unpack(names))
            if not guid(state[1]) or state[2] ~= ARGV[2] or not guid(state[3])
                or not timestamp(state[4]) or not timestamp(state[5]) or state[4] > state[5]
                or not progress(state[6]) then return {INVALID} end
            if not state[7] and not state[8] then state[7], state[8] = '', ''
            elseif not cursor(state[7], state[8]) then return {INVALID} end
        end
        if ARGV[7] == 'read' then
            if not state then return {0} end
            return {UPDATED, unpack(state)}
        end
        if not cursor(ARGV[17], ARGV[18]) then return {INVALID} end
        if state then
            if ARGV[4] <= state[5] then return {OLDER, unpack(state)} end
            if ARGV[3] ~= state[3] then return {CHANGED, unpack(state)} end
            if ARGV[8] ~= '1' then return {CONFLICT} end
            for i = 1, 8 do
                if state[i] ~= ARGV[8+i] then return {CONFLICT} end
            end
            if state[8] ~= '' and (tonumber(ARGV[18]) < tonumber(state[8])
                or (ARGV[18] == state[8] and ARGV[17] ~= state[7])) then return {INVALID} end
            state[5], state[6], state[7], state[8] = ARGV[4], ARGV[5], ARGV[17], ARGV[18]
        else
            if ARGV[8] ~= '0' then return {CONFLICT} end
            state = {ARGV[1], ARGV[2], ARGV[3], ARGV[4], ARGV[4], ARGV[5], ARGV[17], ARGV[18]}
        end
        local status = kind == 'none' and CREATED or UPDATED
        local reply = {status, unpack(state)}
        -- Todos os argumentos/tipos/retorno preparados antes do único write.
        redis.call('HSET', KEYS[1], names[1], state[1], names[2], state[2], names[3], state[3],
            names[4], state[4], names[5], state[5], names[6], state[6], names[7], state[7], names[8], state[8])
        return reply
        """;

    public static string ChaveVeiculoViagem(string ordem) => $"veiculo:{ordem}:viagem";

    public async Task<ViagemObservadaResultado> TentarAtualizarAsync(
        string ordem, Guid itinerarioId, DateTimeOffset timestampGps, double posicaoNaRota, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ordem) || itinerarioId == Guid.Empty
            || !double.IsFinite(posicaoNaRota) || posicaoNaRota is < 0 or > 1
            || !GpsLeituraValidator.TimestampValido(timestampGps, DateTimeOffset.UtcNow, out _))
            return new(ViagemObservadaStatus.InvalidState);
        try
        {
            var db = redis.GetDatabase();
            for (var attempt = 0; attempt < MaxTentativas; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var args = new RedisValue[18];
                Array.Fill(args, (RedisValue)"");
                args[0] = Guid.NewGuid().ToString("N"); args[1] = ordem; args[2] = itinerarioId.ToString("N");
                args[3] = timestampGps.UtcTicks.ToString("D19", CultureInfo.InvariantCulture);
                args[4] = posicaoNaRota.ToString("R", CultureInfo.InvariantCulture);
                args[5] = DateTimeOffset.UtcNow.Add(GpsLeituraValidator.ToleranciaFuturo).UtcTicks
                    .ToString("D19", CultureInfo.InvariantCulture);
                args[6] = "read";
                var snapshot = (RedisResult[])(await db.ScriptEvaluateAsync(Script,
                    [ChaveVeiculoViagem(ordem)], args))!;
                var readStatus = (int)snapshot[0];
                if (readStatus == (int)ViagemObservadaStatus.InvalidState) return new(ViagemObservadaStatus.InvalidState);
                var exists = readStatus != 0;
                var state = exists ? ParseState(snapshot) : null;
                if (state is not null && timestampGps <= state.TimestampUltimaAtualizacao)
                    return new(ViagemObservadaStatus.RejectedOlderOrEqual, state);
                if (state is not null && state.ItinerarioId != itinerarioId)
                    return new(ViagemObservadaStatus.ItineraryChanged, state);
                var baseline = !exists || (string)snapshot[7]! == "";
                var transition = await ocorrencias.BuscarTransicaoAsync(itinerarioId,
                    state?.PosicaoNaRotaConfirmada ?? posicaoNaRota, posicaoNaRota,
                    state?.UltimaParadaItinerarioId ?? Guid.Empty, state?.UltimaParadaOrdem ?? 0, baseline, ct);
                if (transition.Status != ViagemObservadaStatus.Updated) return new(transition.Status);
                args[6] = "write"; args[7] = exists ? "1" : "0";
                if (exists) for (var i = 0; i < 8; i++) args[8 + i] = (string)snapshot[i + 1]!;
                args[16] = transition.UltimaId.ToString("N");
                args[17] = transition.UltimaOrdem.ToString(CultureInfo.InvariantCulture);
                ct.ThrowIfCancellationRequested();
                var reply = (RedisResult[])(await db.ScriptEvaluateAsync(Script,
                    [ChaveVeiculoViagem(ordem)], args))!;
                var status = (ViagemObservadaStatus)(int)reply[0];
                if (status == ViagemObservadaStatus.Conflict) continue;
                if (status == ViagemObservadaStatus.InvalidState) return new(status);
                return new(status, ParseState(reply))
                {
                    OcorrenciasUltrapassadas = status == ViagemObservadaStatus.Updated && !baseline
                        ? transition.Ultrapassadas : Array.Empty<OcorrenciaParada>(),
                    ProximaOcorrenciaOperacional = status is ViagemObservadaStatus.Updated or ViagemObservadaStatus.Created
                        ? transition.Proxima : null,
                };
            }
            return new(ViagemObservadaStatus.Conflict);
        }
        catch (Exception ex)
        {
            // Resposta perdida pode esconder commit concluído. Não compensar nem emitir candidatos.
            logger.LogError(ex, "Falha ao confirmar viagem observada de {ordem}; sem compensação.", ordem);
            return new(ViagemObservadaStatus.InfrastructureFailure);
        }
    }

    private static ViagemObservadaState ParseState(RedisResult[] fields) => new(
        Guid.ParseExact((string)fields[1]!, "N"), (string)fields[2]!, Guid.ParseExact((string)fields[3]!, "N"),
        new(long.Parse((string)fields[4]!, CultureInfo.InvariantCulture), TimeSpan.Zero),
        new(long.Parse((string)fields[5]!, CultureInfo.InvariantCulture), TimeSpan.Zero),
        double.Parse((string)fields[6]!, CultureInfo.InvariantCulture),
        (string)fields[7]! == "" ? Guid.Empty : Guid.ParseExact((string)fields[7]!, "N"),
        (string)fields[8]! == "" ? 0 : int.Parse((string)fields[8]!, CultureInfo.InvariantCulture));
}
