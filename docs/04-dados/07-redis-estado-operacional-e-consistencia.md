# Redis, estado operacional e consistência

**Análise:** 2026-10-06 · **Commit:** `53567bd` · **Estado:** chaves/scripts/workers verificados; Redis não é autoridade estrutural.

## Finalidade

Redis reduz latência e coordena atualizações por veículo. Guarda estado com TTL e streams de transporte; PostgreSQL continua autoridade durável. Persistência do volume Redis não converte chaves efêmeras em modelo de registro.

## Posições e índice de linha

| Chave | Tipo/conteúdo | TTL default | Papel |
|---|---|---:|---|
| `veiculo:{ordem}:ativo` | hash compatível com RedisCache: metadados + JSON | 40 s | posição com sinal ativo |
| `veiculo:{ordem}:recente` | mesmo payload | 180 s | fallback “sem sinal” |
| `veiculo:{ordem}:ts` | string Unix ms | max(active,recent)=180 s | fence temporal |
| `veiculo:{ordem}:gps-lock` | string token | 15 s | exclusão/fencing por veículo |
| `linha:{codigo}:veiculos` | string com ordens de veículos separadas por vírgula | 180 s | leitura por linha |

Os valores são defaults versionados; configuração de ambiente pode alterar TTLs. `PosicaoVeiculoTsBootstrapper` cria `:ts` ausente para chaves legadas, preservando TTL quando possível.

## CAS GPS em Lua

`PosicaoVeiculoCacheRepository` tenta `SET NX` do lock até 60 vezes com 25 ms. Serializa DTO e chama `PosicaoVeiculoPayloadWriter` com quatro chaves. O script:

1. valida argumentos, chaves, TTLs, tamanho e ACLs sem write;
2. comprova token do lock;
3. valida tipos/estado de `:ts`, ativo e recente;
4. rejeita timestamp menor ou igual;
5. grava timestamp e ambos os payloads/TTLs;
6. libera somente lock com mesmo token.

Lua impede interleaving durante o script, mas Redis não oferece rollback de writes em falha interna/OOM após o primeiro write. Timeout com resposta perdida é ambíguo: os dados podem ter sido aplicados; por isso não há compensação. A atomicidade é local a esse conjunto de chaves, não inclui PostgreSQL/SignalR.

## Estado causal

`veiculo:{ordem}:posicao-causal` armazena estado versionado da correção temporal: histórico causal limitado, timestamps, velocidades e estado de movimento. É atualizado por CAS/script próprio após aceite e usado para projeção. TTL/configuração pertencem a `PositionCorrection`; perda causa bootstrap de novo histórico, não perda estrutural.

## Projeção quente de viagem

`veiculo:{ordem}:viagem` é hash de 27 campos mais `VersaoDuravel` e checkpoint, TTL 24 h. Contém identidade, versão pinada, fração/cursor/volta, linha/sentido, estado, terminal e candidato. `CommitHot` compara snapshot esperado e versão durável; `ProjectDurable` impede regredir uma projeção mais nova da mesma viagem. Se Redis falha, repositório lê PostgreSQL e reprojeta.

O script `Commit` também pode atualizar hash e adicionar eventos ao stream na mesma execução Redis após preflight. Isso é atomicidade Redis, não durabilidade conjunta com banco.

## Streams

| Stream | Consumer group / uso | Falhas |
|---|---|---|
| `noponto:viagem:eventos` | histórico de viagem/passagens | pending, autoclaim, retry, DLQ `noponto:viagem:eventos:dead-letter` |
| `noponto:ml:telemetria` | telemetria ML→PostgreSQL | group `telemetria-ml-postgres`, DLQ própria |
| `noponto:position-correction:shadow` | auditoria de posição | group/worker, DLQ/retention |

Workers só fazem `XACK` após persistência. Até cinco falhas são rastreadas com chaves de retry TTL 7 dias; payload permanente ou limite vai à DLQ. Trim considera grupos/pending e margem; não é garantia exactly-once. Evento pode reaparecer, e consumers precisam ser idempotentes.

## Consistência e recuperação

- posição aceita no Redis e falha posterior de viagem: posição segue visível; contexto durável pode ficar atrás;
- PostgreSQL commitado e projeção Redis falha: leitura posterior faz fallback/reprojeção;
- SignalR falha: cache permanece e cliente recupera no próximo fluxo/snapshot; não há transação;
- processo reinicia: channels/memória somem; streams/pending e PostgreSQL sobrevivem conforme infraestrutura;
- Redis perde dados: posições/contexto quente somem; estrutura continua no PostgreSQL, viagem pode ser restaurada do checkpoint;
- duas instâncias: locks/CAS por chave e advisory locks PostgreSQL mitigam concorrência; caches/memória ferroviária não são compartilhados.

Runtime ferroviário principal usa caches, tracker, binding e engine em memória, não Redis. Não atribuir durabilidade a esses objetos.

## Limitações, testes e evidências

Streams dependem da política de persistência/infra Redis, abordada em 2.4B; channel antes do stream pode perder dados; TTL causa desaparecimento deliberado; scripts grandes aumentam acoplamento ao formato. Evidências: cache/payload writers, `ViagemOperacionalRedisScript`, workers/retention e testes `PosicaoVeiculoCacheRepositoryTests`, `RedisStreamBoundedTests`, viagem/telemetria/shadow. Não executados.

