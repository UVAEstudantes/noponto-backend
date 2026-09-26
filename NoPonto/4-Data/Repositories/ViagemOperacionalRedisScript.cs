namespace NoPonto.Data.Repositories;

internal static class ViagemOperacionalRedisScript
{
    internal const string DurableVersion = "VersaoDuravel";
    internal const string DurableCheckpoint = "UltimoCheckpointDuravelUtc";

    // Aplica a versao duravel sem regredir o progresso quente da mesma viagem.
    // Retornos: 2=projetado, 3=Redis ja possui versao duravel superior, 4=merge quente.
    internal const string ProjectDurable = """
        #!lua
        if #KEYS ~= 1 or #ARGV ~= 30 then return 5 end
        local names = {'ViagemId','OrdemVeiculo','PadraoVersaoId','TimestampObservacaoInicial',
            'TimestampUltimaAtualizacao','PosicaoNaRotaConfirmada','UltimaOcorrenciaParadaPadraoId','UltimaParadaOrdem',
            'CodigoLinha','LinhaId','SentidoId','EstadoViagem','ConfirmacoesPosTerminal','TimestampFim',
            'CandidatoPadraoVersaoId','CandidatoSentidoId','CandidatoTimestamp','CandidatoPosicao','CandidatoLinhaId',
            'CandidatoLatitudeInicial','CandidatoLongitudeInicial','PadraoOperacionalId',
            'OcorrenciaCursorId','OrdemCursor','Volta','ProgressoAbsolutoMetros','Topologia'}
        local kind = redis.call('TYPE', KEYS[1]).ok
        if kind ~= 'none' and kind ~= 'hash' then return 5 end
        local currentVersion = kind == 'hash' and tonumber(redis.call('HGET', KEYS[1], 'VersaoDuravel')) or nil
        local incomingVersion = tonumber(ARGV[28])
        if not incomingVersion or incomingVersion < 1 then return 5 end
        if currentVersion and currentVersion > incomingVersion then return 3 end
        local merged = false
        local state = {}
        for i=1,27 do state[i]=ARGV[i] end
        if kind == 'hash' and currentVersion and currentVersion <= incomingVersion then
            local current = redis.call('HMGET', KEYS[1], unpack(names))
            if current[1] == state[1] and current[2] == state[2]
                and current[3] == state[3] and current[4] == state[4]
                and current[5] and #current[5] == 19 and string.match(current[5], '^%d+$')
                and tonumber(current[6]) and tonumber(current[6]) >= 0 and tonumber(current[6]) <= 1
                and current[5] > state[5] then
                state[5] = current[5]
                state[6] = current[6]
                if current[26] then state[26] = current[26] end
                merged = true
            end
        end
        local args={}
        for i=1,27 do args[#args+1]=names[i]; args[#args+1]=state[i] end
        args[#args+1]='VersaoDuravel'; args[#args+1]=ARGV[28]
        args[#args+1]='UltimoCheckpointDuravelUtc'; args[#args+1]=ARGV[29]
        redis.call('HSET', KEYS[1], unpack(args))
        redis.call('EXPIRE', KEYS[1], ARGV[30])
        return merged and 4 or 2
        """;

    internal const string Read = """
        local t = redis.call('TYPE', KEYS[1]).ok
        if t == 'none' then return {} end
        if t ~= 'hash' then return redis.error_reply('INVALID_STATE') end
        return redis.call('HGETALL', KEYS[1])
        """;

    internal const string Commit = """
        #!lua
        if #KEYS ~= 2 or KEYS[1] == KEYS[2] or #ARGV ~= 4 then return 5 end
        local ok1, snapshot = pcall(cjson.decode, ARGV[1])
        local ok2, n = pcall(cjson.decode, ARGV[2])
        local ok3, events = pcall(cjson.decode, ARGV[3])
        if not ok1 or not ok2 or not ok3 or type(snapshot) ~= 'table'
            or type(n) ~= 'table' or type(events) ~= 'table' or #n ~= 27 then return 5 end
        local names = {'ViagemId','OrdemVeiculo','PadraoVersaoId','TimestampObservacaoInicial',
            'TimestampUltimaAtualizacao','PosicaoNaRotaConfirmada','UltimaOcorrenciaParadaPadraoId','UltimaParadaOrdem',
            'CodigoLinha','LinhaId','SentidoId','EstadoViagem','ConfirmacoesPosTerminal','TimestampFim',
            'CandidatoPadraoVersaoId','CandidatoSentidoId','CandidatoTimestamp','CandidatoPosicao','CandidatoLinhaId',
            'CandidatoLatitudeInicial','CandidatoLongitudeInicial','PadraoOperacionalId',
            'OcorrenciaCursorId','OrdemCursor','Volta','ProgressoAbsolutoMetros','Topologia'}
        local empty = '00000000000000000000000000000000'
        local function guid(v) return type(v) == 'string' and #v == 32 and v ~= empty and string.match(v, '^[0-9a-f]+$') end
        local function tick(v) return type(v) == 'string' and #v == 19 and string.match(v, '^%d+$')
            and v > '0621355968000000000' and v <= ARGV[4] and v <= '3155378975999999999' end
        local function progress(v) local x = tonumber(v); return x and x == x and x >= 0 and x <= 1 end
        local function coordinate(v, minimum, maximum)
            local x = tonumber(v); return x and x == x and x >= minimum and x <= maximum
        end
        local function integer(v) return type(v) == 'string' and string.match(v, '^%d+$') and tonumber(v) <= 2147483647 end
        local function within180(later, earlier)
            if later <= earlier then return false end
            local digits, borrow = {}, 0
            for i = 19,1,-1 do
                local d = tonumber(string.sub(later,i,i)) - tonumber(string.sub(earlier,i,i)) - borrow
                if d < 0 then d = d+10; borrow = 1 else borrow = 0 end
                digits[i] = tostring(d)
            end
            return table.concat(digits) <= '0000000001800000000'
        end
        local function valid(s, fields)
            for i = 1,fields do if type(s[i]) ~= 'string' then return false end end
            if not guid(s[1]) or s[2] == '' or not guid(s[3]) or not tick(s[4]) or not tick(s[5])
                or s[4] > s[5] or not progress(s[6]) then return false end
            if not integer(s[8]) or not ((s[7] == empty and tonumber(s[8]) == 0)
                or (guid(s[7]) and tonumber(s[8]) > 0)) then return false end
            if s[9] == '' or not guid(s[10]) or not guid(s[11]) or not integer(s[13]) or tonumber(s[13]) > 2 then return false end
            if s[12] == 'Ativa' then if s[13] ~= '0' or s[14] ~= '' then return false end
            elseif s[12] == 'PossivelFim' then if tonumber(s[13]) > 1 or s[14] ~= '' or tonumber(s[8]) == 0 then return false end
            elseif s[12] == 'Finalizada' then if not tick(s[14]) or s[14] > s[5] or s[14] < s[4] or tonumber(s[8]) == 0 then return false end
            else return false end
            local candidate = false
            for i = 15,19 do if s[i] ~= '' then candidate = true end end
            if candidate and (s[12] ~= 'Finalizada' or not guid(s[15]) or not guid(s[16])
                or not tick(s[17]) or s[17] > s[5] or not progress(s[18]) or not guid(s[19])) then return false end
            if candidate and (not coordinate(s[20],-90,90) or not coordinate(s[21],-180,180)) then return false end
            if not candidate and (s[20] ~= '' or s[21] ~= '') then return false end
            if not guid(s[22]) or not integer(s[25]) or not tonumber(s[26]) or tonumber(s[26]) < 0
                or (s[27] ~= 'LINEAR' and s[27] ~= 'CIRCULAR') then return false end
            local cursor = s[23] ~= '' or s[24] ~= ''
            if cursor and (not guid(s[23]) or not integer(s[24])) then return false end
            return true
        end
        if not tick(ARGV[4]) or not valid(n, #n) then return 5 end
        local kind = redis.call('TYPE', KEYS[1]).ok
        local streamkind = redis.call('TYPE', KEYS[2]).ok
        if (kind ~= 'none' and kind ~= 'hash') or (streamkind ~= 'none' and streamkind ~= 'stream') then return 5 end
        -- XADD * precisa de espaço no contador do último ID; rejeitar stream saturado antes do HSET.
        if streamkind == 'stream' then
            local info = redis.call('XINFO', 'STREAM', KEYS[2])
            for i = 1,#info,2 do
                if info[i] == 'last-generated-id' and string.match(info[i+1], '^18446744073709551615%-') then return 5 end
            end
        end
        local current = kind == 'hash' and redis.call('HGETALL', KEYS[1]) or {}
        if #current ~= #snapshot then return 7 end
        local oldmap, old = {}, {}
        for i = 1,#current,2 do oldmap[current[i]] = current[i+1] end
        for i = 1,#snapshot,2 do
            if oldmap[snapshot[i]] ~= snapshot[i+1] then return 7 end
        end
        if #current > 0 then
            if #current ~= 54 and #current ~= 58 then return 5 end
            local oldfields = 27
            for i = 1,oldfields do old[i] = oldmap[names[i]] end
            if not valid(old, oldfields) then return 5 end
            if n[5] <= old[5] then return 3 end
            local replacement = n[1] ~= old[1]
            if replacement then
                if old[12] ~= 'Finalizada' or n[12] ~= 'Ativa' or old[15] ~= n[3]
                    or old[16] ~= n[11] or old[19] ~= n[10] or old[17] >= n[5]
                    or old[20] == '' or old[21] == ''
                    or not within180(n[5],old[17]) or n[4] ~= n[5]
                    or tonumber(n[6]) <= tonumber(old[18]) then return 5 end
            else
                if n[2] ~= old[2] or n[3] ~= old[3] or n[4] ~= old[4] then return 5 end
                if old[8] and old[8] ~= '' and (tonumber(n[8]) < tonumber(old[8])
                    or (tonumber(n[8]) == tonumber(old[8]) and n[7] ~= old[7])) then return 5 end
                if n[9] ~= old[9] or n[10] ~= old[10] or n[11] ~= old[11] then return 5 end
                    if old[12] == 'PossivelFim'
                        and (n[12] == 'Ativa' or n[13] < old[13]) then return 5 end
                    if old[12] == 'Finalizada' and (n[12] ~= 'Finalizada' or n[14] ~= old[14]) then return 5 end
                    if old[12] == 'Ativa' and n[12] == 'Finalizada' then return 5 end
                    if old[12] == 'Ativa' and n[12] == 'PossivelFim' and n[13] ~= '0' then return 5 end
                    if old[12] == 'PossivelFim' then
                        if n[7] ~= old[7] or n[8] ~= old[8] then return 5 end
                        if n[12] == 'PossivelFim' and tonumber(n[13]) > tonumber(old[13])+1 then return 5 end
                        if n[12] == 'Finalizada' then
                            if n[14] ~= n[5] then return 5 end
                            if n[15] == '' then
                                if old[13] ~= '1' or n[13] ~= '2' then return 5 end
                            elseif n[13] ~= old[13] then return 5 end
                        end
                    end
                if old[12] == 'Finalizada' and (n[7] ~= old[7] or n[8] ~= old[8]
                    or n[6] ~= old[6] or n[13] ~= old[13]) then return 5 end
            end
        elseif n[4] ~= n[5] or n[12] ~= 'Ativa' then return 5 end
        local hashargs = {}
        for i = 1,#n do hashargs[#hashargs+1] = names[i]; hashargs[#hashargs+1] = n[i] end
        local eventargs, seen = {}, {}
        local starts, finishes, lastorder = 0, 0, 0
        local function compact(v) return type(v) == 'string' and string.gsub(v, '%-', '') or '' end
        for _, event in ipairs(events) do
            if type(event) ~= 'table' or type(event.event_id) ~= 'string' or seen[event.event_id]
                or (event.tipo ~= 'ViagemIniciada' and event.tipo ~= 'PassagemParada' and event.tipo ~= 'ViagemFinalizada')
                or type(event.viagem_id) ~= 'string' or type(event.timestamp_evento) ~= 'string'
                or event.ordem_veiculo ~= n[2] or type(event.codigo_linha) ~= 'string'
                or type(event.sentido_id) ~= 'string' or type(event.padrao_versao_id) ~= 'string' then return 5 end
            seen[event.event_id] = true
            local vid = compact(event.viagem_id)
            if not guid(vid) or compact(event.sentido_id) ~= n[11] or compact(event.padrao_versao_id) ~= n[3]
                or event.codigo_linha ~= n[9] then return 5 end
            if event.tipo == 'ViagemIniciada' then
                starts = starts + 1
                if vid ~= n[1] or event.event_id ~= 'inicio:' .. event.viagem_id then return 5 end
            elseif event.tipo == 'ViagemFinalizada' then
                finishes = finishes + 1
                if vid ~= n[1] or event.event_id ~= 'fim:' .. event.viagem_id or n[12] ~= 'Finalizada' then return 5 end
            else
                if #current == 0 or n[1] ~= old[1] or vid ~= n[1]
                    or old[12] ~= 'Ativa' or not guid(compact(event.ocorrencia_parada_padrao_id))
                    or not guid(compact(event.parada_id)) or not integer(event.ordem)
                    or tonumber(event.ordem) <= tonumber(old[8]) or tonumber(event.ordem) <= lastorder
                    or tonumber(event.ordem) > tonumber(n[8]) or not progress(event.posicao_linha)
                    or tonumber(event.posicao_linha) <= tonumber(old[6]) or tonumber(event.posicao_linha) > tonumber(n[6])
                    or type(event.timestamp_passagem) ~= 'string' or type(event.timestamp_gps) ~= 'string'
                    or not integer(event.volta)
                    or event.event_id ~= 'passagem:' .. event.viagem_id .. ':' .. event.ocorrencia_parada_padrao_id .. ':' .. event.volta then return 5 end
                lastorder = tonumber(event.ordem)
            end
            local args = {}
            for k,v in pairs(event) do
                if type(k) ~= 'string' or type(v) ~= 'string' then return 5 end
                args[#args+1] = k; args[#args+1] = v
            end
            eventargs[#eventargs+1] = args
        end
        local needstart = (#current == 0 or n[1] ~= old[1]) and 1 or 0
        local needfinish = (#current > 0 and old[12] == 'PossivelFim' and n[12] == 'Finalizada') and 1 or 0
        if starts ~= needstart or finishes ~= needfinish then return 5 end
        if #current > 0 and n[1] == old[1] and tonumber(n[8]) > tonumber(old[8])
            and lastorder ~= tonumber(n[8]) then return 5 end
        if not redis.acl_check_cmd('HSET', KEYS[1], unpack(hashargs)) then return 5 end
        for _,args in ipairs(eventargs) do
            if not redis.acl_check_cmd('XADD', KEYS[2], '*', unpack(args)) then return 5 end
        end
        -- Todos os erros previsíveis de tipo/argumentos/CAS já foram rejeitados.
        -- Redis isola scripts, mas não desfaz writes em falha interna/OOM; não usar allow-oom nem compensação.
        redis.call('HSET', KEYS[1], unpack(hashargs))
        for _,args in ipairs(eventargs) do redis.call('XADD', KEYS[2], '*', unpack(args)) end
        return #current == 0 and 1 or 2
        """;

    internal const string CommitHot = """
        #!lua
        if #KEYS ~= 1 or #ARGV ~= 4 then return 5 end
        local ok1, expected = pcall(cjson.decode, ARGV[1])
        local ok2, next = pcall(cjson.decode, ARGV[2])
        if not ok1 or not ok2 or type(expected) ~= 'table' or type(next) ~= 'table'
            or #expected ~= 27 or #next ~= 27 then return 5 end
        local names = {'ViagemId','OrdemVeiculo','PadraoVersaoId','TimestampObservacaoInicial',
            'TimestampUltimaAtualizacao','PosicaoNaRotaConfirmada','UltimaOcorrenciaParadaPadraoId','UltimaParadaOrdem',
            'CodigoLinha','LinhaId','SentidoId','EstadoViagem','ConfirmacoesPosTerminal','TimestampFim',
            'CandidatoPadraoVersaoId','CandidatoSentidoId','CandidatoTimestamp','CandidatoPosicao','CandidatoLinhaId',
            'CandidatoLatitudeInicial','CandidatoLongitudeInicial','PadraoOperacionalId',
            'OcorrenciaCursorId','OrdemCursor','Volta','ProgressoAbsolutoMetros','Topologia'}
        if redis.call('TYPE',KEYS[1]).ok ~= 'hash' then return 7 end
        local current = redis.call('HMGET',KEYS[1],unpack(names))
        for i=1,27 do if current[i] ~= expected[i] then return 7 end end
        if redis.call('HGET',KEYS[1],'VersaoDuravel') ~= ARGV[3] then return 7 end
        if next[5] <= current[5] then return 3 end
        local args={}
        for i=1,27 do args[#args+1]=names[i]; args[#args+1]=next[i] end
        redis.call('HSET',KEYS[1],unpack(args))
        redis.call('EXPIRE',KEYS[1],ARGV[4])
        return 2
        """;
}
