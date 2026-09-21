using System.Diagnostics;
using System.Globalization;
using NoPonto.Application.GPS;
using StackExchange.Redis;

namespace NoPonto.Data.Repositories;

public sealed class EstadoCausalPosicaoRepository(
    IConnectionMultiplexer redis,
    EstadoCausalPosicaoCodec codec,
    EstadoCausalPosicaoMetrics metrics) : IEstadoCausalPosicaoRepository
{
    private const string ScriptLeitura = """
        local result = {}
        for i = 1, #KEYS do
            local kind = redis.call('TYPE', KEYS[i]).ok
            if kind == 'none' then
                result[#result + 1] = 'M'
                result[#result + 1] = ''
                result[#result + 1] = ''
                result[#result + 1] = ''
            elseif kind ~= 'hash' then
                result[#result + 1] = 'I'
                result[#result + 1] = ''
                result[#result + 1] = ''
                result[#result + 1] = ''
            else
                local values = redis.call('HMGET', KEYS[i], 'v', 'ts', 'data')
                result[#result + 1] = 'H'
                result[#result + 1] = values[1] or ''
                result[#result + 1] = values[2] or ''
                result[#result + 1] = values[3] or ''
            end
        end
        return result
        """;

    // KEYS em pares: timestamp B, estado causal. ARGV: ttl; por item expected, novo, versão, data.
    private const string ScriptCas = """
        local ACCEPTED, B_NOT_CURRENT, STALE, CONFLICT, VERSION, INVALID = 1, 2, 3, 4, 5, 6
        if #KEYS == 0 or #KEYS % 2 ~= 0 or #ARGV ~= 1 + (#KEYS / 2) * 4 then
            return redis.error_reply('invalid causal batch arguments')
        end

        local function integer(text, allow_zero)
            local pattern = allow_zero and '^%d+$' or '^[1-9]%d*$'
            if not text or not string.match(text, pattern) then return nil end
            local n = tonumber(text)
            if not n or n ~= math.floor(n) or n > 253402300799999 then return nil end
            if not allow_zero and n < 1 then return nil end
            return n
        end

        local ttl = integer(ARGV[1], false)
        if not ttl then return redis.error_reply('invalid causal ttl') end

        local result = {}
        for item = 1, #KEYS / 2 do
            local keyIndex = (item - 1) * 2 + 1
            local argIndex = (item - 1) * 4 + 2
            local expectedText = ARGV[argIndex]
            local newText = ARGV[argIndex + 1]
            local versionText = ARGV[argIndex + 2]
            local data = ARGV[argIndex + 3]
            local newTimestamp = integer(newText, false)
            local version = integer(versionText, false)

            local bkind = redis.call('TYPE', KEYS[keyIndex]).ok
            local ckind = redis.call('TYPE', KEYS[keyIndex + 1]).ok
            if (bkind ~= 'none' and bkind ~= 'string')
                or (ckind ~= 'none' and ckind ~= 'hash')
                or not newTimestamp or not version or data == '' then
                result[#result + 1] = INVALID
            else
                local bTimestamp = redis.call('GET', KEYS[keyIndex])
                if bTimestamp ~= newText then
                    result[#result + 1] = B_NOT_CURRENT
                else
                    local causalKey = KEYS[keyIndex + 1]
                    local currentText = redis.call('HGET', causalKey, 'ts')
                    local currentVersion = redis.call('HGET', causalKey, 'v')
                    local current = nil
                    local status = nil

                    if currentText then
                        current = integer(currentText, false)
                        if not current or not currentVersion or not integer(currentVersion, false) then
                            status = INVALID
                        elseif currentVersion ~= versionText then
                            status = VERSION
                        elseif current >= newTimestamp then
                            status = STALE
                        end
                    elseif redis.call('EXISTS', causalKey) == 1 then
                        status = INVALID
                    end

                    if not status then
                        local expected = expectedText == '-' and nil or integer(expectedText, false)
                        if expectedText ~= '-' and not expected then
                            status = INVALID
                        elseif (current == nil and expected ~= nil)
                            or (current ~= nil and expected == nil)
                            or (current ~= nil and current ~= expected) then
                            status = CONFLICT
                        else
                            redis.call('HSET', causalKey, 'v', versionText, 'ts', newText, 'data', data)
                            redis.call('EXPIRE', causalKey, ttl)
                            status = ACCEPTED
                        end
                    end
                    result[#result + 1] = status
                end
            end
        end
        return result
        """;

    public static string ChaveEstado(string ordem) => $"veiculo:{ordem}:posicao-causal";

    public async Task<IReadOnlyDictionary<string, EstadoCausalLeitura>> LerLoteAsync(
        IReadOnlyCollection<string> ordens, int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var unicas = ordens.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var saida = new Dictionary<string, EstadoCausalLeitura>(StringComparer.OrdinalIgnoreCase);
        metrics.RegistrarLeituraSolicitada(unicas.Length);

        foreach (var chunk in unicas.Chunk(batchSize))
        {
            ct.ThrowIfCancellationRequested();
            var inicio = Stopwatch.GetTimestamp();
            try
            {
                var resposta = (RedisResult[]?)await redis.GetDatabase().ScriptEvaluateAsync(
                    ScriptLeitura, chunk.Select(x => (RedisKey)ChaveEstado(x)).ToArray(),
                    Array.Empty<RedisValue>()).ConfigureAwait(false)
                    ?? throw new RedisServerException("Resposta causal batch nula.");
                if (resposta.Length != chunk.Length * 4)
                    throw new RedisServerException("Resposta causal batch com tamanho inválido.");

                for (var i = 0; i < chunk.Length; i++)
                    saida[chunk[i]] = InterpretarLeitura(chunk[i], resposta, i * 4);
            }
            catch (Exception ex) when (ex is RedisException or InvalidCastException)
            {
                metrics.RegistrarRedisFailure();
                foreach (var ordem in chunk)
                    saida[ordem] = new(ordem, EstadoCausalLeituraStatus.InfrastructureFailure, null, null);
            }
            finally
            {
                metrics.RegistrarLeituraChunk(Stopwatch.GetElapsedTime(inicio));
            }
        }
        return saida;
    }

    public async Task<IReadOnlyList<EstadoCausalCommitResultado>> TentarAtualizarLoteAsync(
        IReadOnlyList<EstadoCausalCommit> commits, int batchSize, TimeSpan ttl, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        if (ttl < TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(ttl));
        var resultados = new List<EstadoCausalCommitResultado>(commits.Count);

        foreach (var chunk in commits.Chunk(batchSize))
        {
            ct.ThrowIfCancellationRequested();
            var serializados = chunk.Select(x => (Commit: x, Codec: codec.Serializar(x.Estado))).ToArray();
            foreach (var item in serializados.Where(x => x.Codec.Status != EstadoCausalCodecStatus.Success))
            {
                metrics.RegistrarInvalido();
                resultados.Add(new(item.Commit.Ordem, EstadoCausalCommitStatus.InvalidState));
            }
            var validos = serializados.Where(x => x.Codec.Status == EstadoCausalCodecStatus.Success).ToArray();
            if (validos.Length == 0) continue;

            var keys = validos.SelectMany(x => new RedisKey[]
            {
                PosicaoVeiculoCacheRepository.ChaveVeiculoTimestamp(x.Commit.Ordem),
                ChaveEstado(x.Commit.Ordem),
            }).ToArray();
            var args = new List<RedisValue>(1 + validos.Length * 4) { (long)ttl.TotalSeconds };
            foreach (var item in validos)
            {
                args.Add(item.Commit.ExpectedTimestampMs?.ToString(CultureInfo.InvariantCulture) ?? "-");
                args.Add(item.Commit.TimestampMs);
                args.Add(EstadoCausalPosicaoCodec.VersaoAtual);
                args.Add(item.Codec.Json!);
            }

            var inicio = Stopwatch.GetTimestamp();
            try
            {
                var resposta = (RedisResult[]?)await redis.GetDatabase().ScriptEvaluateAsync(
                    ScriptCas, keys, args.ToArray()).ConfigureAwait(false)
                    ?? throw new RedisServerException("Resposta CAS causal batch nula.");
                if (resposta.Length != validos.Length)
                    throw new RedisServerException("Resposta CAS causal batch com tamanho inválido.");
                for (var i = 0; i < validos.Length; i++)
                {
                    var status = (EstadoCausalCommitStatus)(long)resposta[i];
                    RegistrarCommit(status, validos[i].Codec.Bytes);
                    resultados.Add(new(validos[i].Commit.Ordem, status));
                }
            }
            catch (Exception ex) when (ex is RedisException or InvalidCastException)
            {
                metrics.RegistrarRedisFailure();
                resultados.AddRange(validos.Select(x =>
                    new EstadoCausalCommitResultado(x.Commit.Ordem,
                        EstadoCausalCommitStatus.InfrastructureFailure)));
            }
            finally
            {
                metrics.RegistrarEscritaChunk(Stopwatch.GetElapsedTime(inicio));
            }
        }
        return resultados;
    }

    private EstadoCausalLeitura InterpretarLeitura(string ordem, RedisResult[] resposta, int offset)
    {
        var tipo = (string?)resposta[offset];
        if (tipo == "M") { metrics.RegistrarMiss(); return new(ordem, EstadoCausalLeituraStatus.Miss, null, null); }
        if (tipo != "H") { metrics.RegistrarInvalido(); return new(ordem, EstadoCausalLeituraStatus.InvalidState, null, null); }

        var versaoTexto = (string?)resposta[offset + 1];
        var timestampTexto = (string?)resposta[offset + 2];
        var data = (string?)resposta[offset + 3];
        if (!int.TryParse(versaoTexto, NumberStyles.None, CultureInfo.InvariantCulture, out var versao))
        {
            metrics.RegistrarInvalido();
            return new(ordem, EstadoCausalLeituraStatus.InvalidState, null, null);
        }
        if (versao != EstadoCausalPosicaoCodec.VersaoAtual)
        {
            metrics.RegistrarInvalido();
            return new(ordem, EstadoCausalLeituraStatus.VersionUnsupported, null, null);
        }
        if (!long.TryParse(timestampTexto, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || timestamp <= 0)
        {
            metrics.RegistrarInvalido();
            return new(ordem, EstadoCausalLeituraStatus.InvalidState, null, null);
        }

        var decodificado = codec.Desserializar(versao, data);
        if (decodificado.Status != EstadoCausalCodecStatus.Success
            || decodificado.Estado!.UltimoTimestampGps.ToUnixTimeMilliseconds() != timestamp)
        {
            metrics.RegistrarInvalido();
            return new(ordem, decodificado.Status == EstadoCausalCodecStatus.VersionUnsupported
                ? EstadoCausalLeituraStatus.VersionUnsupported : EstadoCausalLeituraStatus.InvalidState,
                null, null);
        }
        metrics.RegistrarHit(decodificado.Bytes);
        return new(ordem, EstadoCausalLeituraStatus.Hit, decodificado.Estado, timestamp, decodificado.Bytes);
    }

    private void RegistrarCommit(EstadoCausalCommitStatus status, int bytes)
    {
        switch (status)
        {
            case EstadoCausalCommitStatus.Accepted: metrics.RegistrarAtualizado(bytes); break;
            case EstadoCausalCommitStatus.BNotCurrent: metrics.RegistrarBNotCurrent(); break;
            case EstadoCausalCommitStatus.RejectedOlderOrEqual: metrics.RegistrarRejeitadoStale(); break;
            case EstadoCausalCommitStatus.Conflict: metrics.RegistrarConflito(); break;
            case EstadoCausalCommitStatus.VersionUnsupported:
            case EstadoCausalCommitStatus.InvalidState: metrics.RegistrarInvalido(); break;
            case EstadoCausalCommitStatus.InfrastructureFailure: metrics.RegistrarRedisFailure(); break;
        }
    }
}
