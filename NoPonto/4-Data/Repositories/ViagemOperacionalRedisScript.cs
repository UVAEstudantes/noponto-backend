namespace NoPonto.Data.Repositories;

internal static class ViagemOperacionalRedisScript
{
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
            or type(n) ~= 'table' or type(events) ~= 'table' or #n ~= 19 then return 5 end
        local names = {'ViagemId','OrdemVeiculo','ItinerarioId','TimestampObservacaoInicial',
            'TimestampUltimaAtualizacao','PosicaoNaRotaConfirmada','UltimaParadaItinerarioId','UltimaParadaOrdem',
            'CodigoLinha','LinhaId','SentidoId','EstadoViagem','ConfirmacoesPosTerminal','TimestampFim',
            'CandidatoItinerarioId','CandidatoSentidoId','CandidatoTimestamp','CandidatoPosicao','CandidatoLinhaId'}
        local empty = '00000000000000000000000000000000'
        local function guid(v) return type(v) == 'string' and #v == 32 and v ~= empty and string.match(v, '^[0-9a-f]+$') end
        local function tick(v) return type(v) == 'string' and #v == 19 and string.match(v, '^%d+$')
            and v > '0621355968000000000' and v <= ARGV[4] and v <= '3155378975999999999' end
        local function progress(v) local x = tonumber(v); return x and x == x and x >= 0 and x <= 1 end
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
        local function valid(s, legacy)
            for i = 1, legacy and 6 or 19 do if type(s[i]) ~= 'string' then return false end end
            if not guid(s[1]) or s[2] == '' or not guid(s[3]) or not tick(s[4]) or not tick(s[5])
                or s[4] > s[5] or not progress(s[6]) then return false end
            if legacy and s[7] == nil and s[8] == nil then return true end
            if legacy and s[7] == '' and s[8] == '' then return true end
            if not integer(s[8]) or not ((s[7] == empty and tonumber(s[8]) == 0)
                or (guid(s[7]) and tonumber(s[8]) > 0)) then return false end
            if legacy then return true end
            if s[9] == '' or not guid(s[10]) or not guid(s[11]) or not integer(s[13]) or tonumber(s[13]) > 2 then return false end
            if s[12] == 'Ativa' then if s[13] ~= '0' or s[14] ~= '' then return false end
            elseif s[12] == 'PossivelFim' then if tonumber(s[13]) > 1 or s[14] ~= '' or tonumber(s[8]) == 0 then return false end
            elseif s[12] == 'Finalizada' then if not tick(s[14]) or s[14] > s[5] or s[14] < s[4] or tonumber(s[8]) == 0 then return false end
            else return false end
            local candidate = false
            for i = 15,19 do if s[i] ~= '' then candidate = true end end
            if candidate and (s[12] ~= 'Finalizada' or not guid(s[15]) or not guid(s[16]) or s[16] == s[11]
                or not tick(s[17]) or s[17] ~= s[5] or not progress(s[18]) or s[19] ~= s[10]) then return false end
            return true
        end
        if not tick(ARGV[4]) or not valid(n, false) then return 5 end
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
        local legacy = #current == 12 or #current == 16
        if #current > 0 then
            if not legacy and #current ~= 38 then return 5 end
            for i = 1,19 do old[i] = oldmap[names[i]] end
            if not valid(old, legacy) then return 5 end
            if n[5] <= old[5] then return 3 end
            local replacement = n[1] ~= old[1]
            if legacy and #current == 16 and (old[7] == nil or old[8] == nil) then return 5 end
            if legacy and n[12] == 'Finalizada' then return 5 end
            if replacement then
                if legacy or old[12] ~= 'Finalizada' or n[12] ~= 'Ativa' or old[15] ~= n[3]
                    or old[16] ~= n[11] or old[19] ~= n[10] or old[17] >= n[5]
                    or not within180(n[5],old[17]) or n[4] ~= n[5] then return 5 end
            else
                if n[2] ~= old[2] or n[3] ~= old[3] or n[4] ~= old[4] then return 5 end
                if old[8] and old[8] ~= '' and (tonumber(n[8]) < tonumber(old[8])
                    or (tonumber(n[8]) == tonumber(old[8]) and n[7] ~= old[7])) then return 5 end
                if not legacy then
                    if n[9] ~= old[9] or n[10] ~= old[10] or n[11] ~= old[11] then return 5 end
                    local terminalreset = old[12] == 'PossivelFim' and n[12] == 'Ativa'
                        and n[13] == '0' and n[14] == '' and n[15] == ''
                    if old[12] == 'PossivelFim' and not terminalreset
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
            end
        elseif n[4] ~= n[5] or n[12] ~= 'Ativa' then return 5 end
        local hashargs = {}
        for i = 1,19 do hashargs[#hashargs+1] = names[i]; hashargs[#hashargs+1] = n[i] end
        local eventargs, seen = {}, {}
        local starts, finishes, lastorder = 0, 0, 0
        local function compact(v) return type(v) == 'string' and string.gsub(v, '%-', '') or '' end
        for _, event in ipairs(events) do
            if type(event) ~= 'table' or type(event.event_id) ~= 'string' or seen[event.event_id]
                or (event.tipo ~= 'ViagemIniciada' and event.tipo ~= 'PassagemParada' and event.tipo ~= 'ViagemFinalizada')
                or type(event.viagem_id) ~= 'string' or type(event.timestamp_evento) ~= 'string'
                or event.ordem_veiculo ~= n[2] or type(event.codigo_linha) ~= 'string'
                or type(event.sentido_id) ~= 'string' or type(event.itinerario_id) ~= 'string' then return 5 end
            seen[event.event_id] = true
            local vid = compact(event.viagem_id)
            if not guid(vid) or compact(event.sentido_id) ~= n[11] or compact(event.itinerario_id) ~= n[3]
                or event.codigo_linha ~= n[9] then return 5 end
            if event.tipo == 'ViagemIniciada' then
                starts = starts + 1
                if vid ~= n[1] or event.event_id ~= 'inicio:' .. event.viagem_id then return 5 end
            elseif event.tipo == 'ViagemFinalizada' then
                finishes = finishes + 1
                if vid ~= n[1] or event.event_id ~= 'fim:' .. event.viagem_id or n[12] ~= 'Finalizada' then return 5 end
            else
                if #current == 0 or (legacy and (#current ~= 16 or old[8] == '')) or n[1] ~= old[1] or vid ~= n[1]
                    or (not legacy and old[12] ~= 'Ativa') or not guid(compact(event.parada_itinerario_id))
                    or not guid(compact(event.parada_id)) or not integer(event.ordem)
                    or tonumber(event.ordem) <= tonumber(old[8]) or tonumber(event.ordem) <= lastorder
                    or tonumber(event.ordem) > tonumber(n[8]) or not progress(event.posicao_linha)
                    or tonumber(event.posicao_linha) <= tonumber(old[6]) or tonumber(event.posicao_linha) > tonumber(n[6])
                    or type(event.timestamp_passagem) ~= 'string' or type(event.timestamp_gps) ~= 'string'
                    or event.event_id ~= 'passagem:' .. event.viagem_id .. ':' .. event.parada_itinerario_id then return 5 end
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
        local needfinish = (#current > 0 and not legacy and old[12] == 'PossivelFim' and n[12] == 'Finalizada') and 1 or 0
        if starts ~= needstart or finishes ~= needfinish then return 5 end
        if #current > 0 and (not legacy or (#current == 16 and old[8] ~= '')) and n[1] == old[1] and tonumber(n[8]) > tonumber(old[8])
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
}
