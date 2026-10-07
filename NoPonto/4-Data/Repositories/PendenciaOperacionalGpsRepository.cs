using System.Text.Json;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using StackExchange.Redis;

namespace NoPonto.Data.Repositories;

/// <summary>Hash global limitado, índice de prazo e ordenação por veículo; lease por veículo.</summary>
public sealed class PendenciaOperacionalGpsRepository(IConnectionMultiplexer redis,
    IOptions<RetryOperacionalGpsOptions> options,
    Microsoft.Extensions.Logging.ILogger<PendenciaOperacionalGpsRepository>? logger = null) : IPendenciaOperacionalGpsStore
{
    internal string Prefixo { get; init; } = "noponto:gps:retry:";
    private RedisKey Dados => Prefixo + "dados";
    private RedisKey Prazos => Prefixo + "prazos";
    private RedisKey Veiculo(string ordem) => Prefixo + "veiculo:" + ordem;
    private RedisKey Lock(string ordem) => Prefixo + "lease:" + ordem;
    private static string Membro(PendenciaOperacionalGps p) => p.Gps.TimestampGps.UtcTicks.ToString("D19") + ":" + p.Id;

    public async Task<string> AdicionarAsync(PendenciaOperacionalGps p, CancellationToken ct)
    {
        const string script = """
            local old=redis.call('HGET',KEYS[1],ARGV[1])
            if old then
                if cjson.decode(old).Assinatura~=cjson.decode(ARGV[2]).Assinatura then return 'CONFLITO_PAYLOAD' end
                return 'EXISTENTE'
            end
            if redis.call('HLEN',KEYS[1])>=tonumber(ARGV[4]) then return 'DESCARTADA_LIMITE_GLOBAL' end
            if redis.call('ZCARD',KEYS[3])>=tonumber(ARGV[5]) then return 'DESCARTADA_LIMITE_VEICULO' end
            redis.call('HSET',KEYS[1],ARGV[1],ARGV[2])
            local due=tonumber(ARGV[3])
            local head=redis.call('ZRANGE',KEYS[3],0,0)[1]
            if head then
                local olddue=redis.call('ZSCORE',KEYS[2],string.sub(head,21))
                if olddue then due=math.max(due,tonumber(olddue)) end
            end
            redis.call('ZADD',KEYS[2],due,ARGV[1])
            redis.call('ZADD',KEYS[3],0,ARGV[6])
            for i=1,3 do redis.call('EXPIRE',KEYS[i],ARGV[7]) end
            return 'CRIADA'
            """;
        return (await redis.GetDatabase().ScriptEvaluateAsync(script, [Dados,Prazos,Veiculo(p.Gps.Ordem)],
            [p.Id,JsonSerializer.Serialize(p),p.CriadaEm.ToUnixTimeMilliseconds(),options.Value.MaxGlobal,
                options.Value.MaxPorVeiculo,Membro(p),options.Value.TtlSegundos*2+options.Value.LeaseSegundos]).WaitAsync(ct)).ToString();
    }

    public async Task<LeasePendenciaOperacional?> ClaimAsync(string ordem, DateTimeOffset agora, CancellationToken ct)
    {
        const string script = """
            local item=redis.call('ZRANGE',KEYS[3],0,0)[1]
            if not item then return nil end
            local id=string.sub(item,21)
            local data=redis.call('HGET',KEYS[1],id)
            if not data then redis.call('ZREM',KEYS[3],item); redis.call('ZREM',KEYS[2],id); return nil end
            local due=redis.call('ZSCORE',KEYS[2],id)
            if not due or tonumber(due)>tonumber(ARGV[1]) then return nil end
            if not redis.call('SET',KEYS[4],ARGV[2],'NX','PX',ARGV[3]) then return nil end
            return {item,data}
            """;
        var token=Guid.NewGuid().ToString("N");
        var r=await redis.GetDatabase().ScriptEvaluateAsync(script,[Dados,Prazos,Veiculo(ordem),Lock(ordem)],
            [agora.ToUnixTimeMilliseconds(),token,options.Value.LeaseSegundos*1000]).WaitAsync(ct);
        if(r.IsNull) return null;
        var partes=(RedisResult[])r!;
        var membro=partes[0].ToString(); var id=membro[20..];
        try
        {
            var p=JsonSerializer.Deserialize<PendenciaOperacionalGps>(partes[1].ToString())
                ?? throw new FormatException("Pendência GPS inválida.");
            if(p.Id!=id||p.Gps.Ordem!=ordem||Membro(p)!=membro)
                throw new FormatException("Identidade da pendência GPS inválida.");
            return new(p,token);
        }
        catch(Exception ex) when(ex is JsonException or FormatException or ArgumentException or NullReferenceException)
        {
            const string invalid="""
                if redis.call('GET',KEYS[4])~=ARGV[1] then return 0 end
                redis.call('HDEL',KEYS[1],ARGV[2]); redis.call('ZREM',KEYS[2],ARGV[2])
                redis.call('ZREM',KEYS[3],ARGV[3]); redis.call('DEL',KEYS[4]); return 1
                """;
            await redis.GetDatabase().ScriptEvaluateAsync(invalid,[Dados,Prazos,Veiculo(ordem),Lock(ordem)],
                [token,id,membro]).WaitAsync(ct);
            logger?.LogWarning(ex,"Pendência GPS {id} descartada: payload Redis inválido.",id);
            return null;
        }
    }

    public async Task<bool> ConcluirAsync(LeasePendenciaOperacional l,CancellationToken ct)
    {
        const string script="""
            if redis.call('GET',KEYS[4])~=ARGV[1] then return 0 end
            redis.call('HDEL',KEYS[1],ARGV[2]); redis.call('ZREM',KEYS[2],ARGV[2])
            redis.call('ZREM',KEYS[3],ARGV[3]); redis.call('DEL',KEYS[4])
            if redis.call('ZCARD',KEYS[3])==0 then redis.call('DEL',KEYS[3]) end
            return 1
            """;
        return (long)await redis.GetDatabase().ScriptEvaluateAsync(script,
            [Dados,Prazos,Veiculo(l.Pendencia.Gps.Ordem),Lock(l.Pendencia.Gps.Ordem)],
            [l.Token,l.Pendencia.Id,Membro(l.Pendencia)]).WaitAsync(ct)==1;
    }

    public async Task<bool> ReagendarAsync(LeasePendenciaOperacional l,DateTimeOffset quando,CancellationToken ct)
    {
        const string script="""
            if redis.call('GET',KEYS[3])~=ARGV[1] then return 0 end
            redis.call('HSET',KEYS[1],ARGV[2],ARGV[3]); redis.call('ZADD',KEYS[2],ARGV[4],ARGV[2])
            for _,item in ipairs(redis.call('ZRANGE',KEYS[4],1,-1)) do
                local id=string.sub(item,21)
                local due=redis.call('ZSCORE',KEYS[2],id)
                if due and tonumber(due)<tonumber(ARGV[4]) then redis.call('ZADD',KEYS[2],ARGV[4],id) end
            end
            redis.call('DEL',KEYS[3]);
            redis.call('EXPIRE',KEYS[1],ARGV[5]); redis.call('EXPIRE',KEYS[2],ARGV[5]);
            redis.call('EXPIRE',KEYS[4],ARGV[5]); return 1
            """;
        var p=l.Pendencia with{Tentativas=l.Pendencia.Tentativas+1};
        return (long)await redis.GetDatabase().ScriptEvaluateAsync(script,[Dados,Prazos,Lock(p.Gps.Ordem),Veiculo(p.Gps.Ordem)],
            [l.Token,p.Id,JsonSerializer.Serialize(p),Math.Min(quando.ToUnixTimeMilliseconds(),p.ExpiraEm.ToUnixTimeMilliseconds()),
                options.Value.TtlSegundos*2+options.Value.LeaseSegundos])
            .WaitAsync(ct)==1;
    }
    public async Task<bool> TemPendenciaAsync(string ordem,CancellationToken ct) =>
        await redis.GetDatabase().SortedSetLengthAsync(Veiculo(ordem)).WaitAsync(ct)>0;

    public async Task<IReadOnlyList<string>> VeiculosElegiveisAsync(DateTimeOffset agora,CancellationToken ct)
    {
        var db=redis.GetDatabase();
        var ids=await db.SortedSetRangeByScoreAsync(Prazos,stop:agora.ToUnixTimeMilliseconds(),
            take:options.Value.LotePorCiclo).WaitAsync(ct);
        if(ids.Length==0) return [];
        var payloads=await db.HashGetAsync(Dados,ids).WaitAsync(ct);
        var ordens=new HashSet<string>(StringComparer.Ordinal);
        for(var i=0;i<payloads.Length;i++)
        {
            if(payloads[i].IsNull)
            {
                const string limparOrfao="""
                    if redis.call('HEXISTS',KEYS[1],ARGV[1])==0 then
                        return redis.call('ZREM',KEYS[2],ARGV[1])
                    end
                    return 0
                    """;
                await db.ScriptEvaluateAsync(limparOrfao,[Dados,Prazos],[ids[i]]).WaitAsync(ct);
                continue;
            }
            try
            {
                var p=JsonSerializer.Deserialize<PendenciaOperacionalGps>(payloads[i].ToString())!;
                if(p.Id!=ids[i].ToString()||string.IsNullOrWhiteSpace(p.Gps.Ordem))
                    throw new FormatException("Índice de pendência GPS incompatível.");
                // Um índice de veículo pode ter expirado enquanto outros mantinham o hash global.
                if(await db.SortedSetLengthAsync(Veiculo(p.Gps.Ordem)).WaitAsync(ct)==0)
                {
                    await db.SortedSetAddAsync(Veiculo(p.Gps.Ordem),Membro(p),0).WaitAsync(ct);
                    await db.KeyExpireAsync(Veiculo(p.Gps.Ordem),TimeSpan.FromSeconds(options.Value.TtlSegundos*2+options.Value.LeaseSegundos)).WaitAsync(ct);
                }
                ordens.Add(p.Gps.Ordem);
            }
            catch(Exception ex) when(ex is JsonException or NullReferenceException or ArgumentException or FormatException)
            {
                // Sem identidade legível não executar nada; remover somente o item corrupto indexado.
                const string limpar="""
                    if redis.call('HGET',KEYS[1],ARGV[1])~=ARGV[2] then return 0 end
                    redis.call('HDEL',KEYS[1],ARGV[1]); redis.call('ZREM',KEYS[2],ARGV[1]); return 1
                    """;
                await db.ScriptEvaluateAsync(limpar,[Dados,Prazos],[ids[i],payloads[i]]).WaitAsync(ct);
                logger?.LogWarning(ex,"Pendência GPS {id} descartada: índice/payload inválido.",ids[i]);
            }
        }
        return ordens.ToArray();
    }
}
