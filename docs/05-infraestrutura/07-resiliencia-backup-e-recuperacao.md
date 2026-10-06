# NoPonto — resiliência, backup e recuperação

**Finalidade:** registrar garantias reais diante de falhas e impedir que volume, cache ou restart sejam confundidos com recuperação.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Estado:** mecanismos no código/Compose verificados; não houve teste de falha, backup ou restore.

## Durabilidade e restart

| Estado | Sobrevive à API? | Sobrevive ao Redis/host? | Recuperação observável |
|---|---|---|---|
| estrutura, schedules, viagens/outbox, histórico, telemetria/ETA persistidos | sim, PostgreSQL | sim se volume/banco íntegro | reconnect/workers retomam; restore não testado |
| posições, TTLs, locks, causalidade Redis | sim à API se Redis continua | não, sem RDB/AOF | novas coletas reconstroem parcialmente |
| streams, groups e pending Redis | sim à API | não | eventos não persistidos podem ser perdidos |
| channels e filas em processo | não | não | novos eventos; drain limitado apenas onde implementado |
| caches, demand registry, expected runs, trackers/bindings ferroviários | não | não | rebuild de schedule e novas evidências; tempo não medido |
| viagem operacional | snapshot durável no PostgreSQL e projeções Redis | núcleo durável sobrevive | reconciliação após perda Redis não foi ensaiada |

Redis `noeviction` protege contra expulsão seletiva, não contra indisponibilidade: ao atingir memória, escritas falham. PostgreSQL é dependência crítica; outbox preserva eventos secundários quando seu consumidor falha, mas não quando a própria transação no banco falha.

## Matriz de cenários

| Cenário | Efeito/comportamento | Perda possível e recuperação | Medida futura |
|---|---|---|---|
| restart API | HTTP/SignalR/workers interrompem; clientes reconectam | memória/channels/trackers; DB/Redis permanecem | readiness e teste de retomada |
| restart host/Redis | realtime quente e streams zeram | pending e eventos Redis não persistidos | runbook de reconstrução; decidir persistência |
| PostgreSQL indisponível | estrutura, matching e persistência degradam/falham | outbox novo não pode ser confirmado | alertas, timeout e restore ensaiado |
| perda volume PostgreSQL | perda durável potencial | sem backup comprovado, recuperação indeterminada | backup externo e restore periódico |
| internet/provider | dados ficam antigos; TTL/staleness remove ou degrada | lacuna observacional | freshness, backoff e comunicação de indisponibilidade |
| deploy falho/crash loop | API indisponível; `unless-stopped` tenta reiniciar | memória; risco de loop | pin, smoke test e rollback documentado |
| CPU/RAM/swap | ciclos atrasam; OOM possível | filas/channels e processos | budgets/alertas/limites |
| disco cheio/log sem rotação | banco/logs/builds falham | corrupção/perda possível | alertas, retenção e reserva |
| channel saturado | espera ou drop conforme fila | telemetria/shadow pode atrasar/perder | gauge, drop counter e limite validado |
| mapas/CDN indisponíveis | mapa mobile incompleto | sem perda durável | fallback/estado de erro |
| perda tracker ferroviário | posição some/recomeça schedule-only | estado inferido em memória | reconstrução e métrica de reacquisition |

## Falhas externas e degradação

ETA legado é fail-open: timeout/cooldown retorna sem previsões e GPS continua. Shadows e telemetria capturam falhas para não bloquear o hot path. Providers GPS/ferrovia usam timeout, budget/backoff; porém sem dados frescos a funcionalidade percebida degrada. Redis afeta diretamente realtime; PostgreSQL afeta estrutura/matching/durabilidade; falha total da API ou host interrompe tudo.

## Backup, restore, RPO e RTO

Há volume PostgreSQL e um filesystem secundário chamado de backup, mas não foram comprovados rotina, periodicidade, retenção, criptografia, cópia externa, consistência, monitoramento nem restore. Volume Docker não é backup. Redis não possui persistência e deve ter RPO conscientemente efêmero; eventos críticos deveriam alcançar PostgreSQL antes de depender dele.

RPO é a quantidade máxima aceitável de dados perdidos; RTO é o tempo aceitável até restaurar o serviço. Não há valores aprovados ou evidência para declarar objetivos. Defini-los exige criticidade, tamanho do banco, janela de backup e ensaio de recuperação.

## Plano incremental

1. inventariar dados e donos; definir RPO/RTO;
2. backup PostgreSQL consistente, automatizado, criptografado e fora do host;
3. retenção e verificação de sucesso;
4. restore periódico em ambiente isolado, medindo tempo e integridade;
5. runbooks para API, Redis, DB, disco, provider e rede;
6. teste controlado de reconstrução Redis/ferrovia e compatibilidade de rollback.

Fontes: Compose, workers/outbox/streams, docs 03/04 e auditorias 07/10/15. Testes existentes cobrem unidades/integração específicas, não desastre produtivo. Relacionados: [dados](../04-dados/05-viagens-historico-telemetria-e-eta.md), [Redis](../04-dados/07-redis-estado-operacional-e-consistencia.md) e [plano operacional](08-operacao-riscos-e-plano-de-melhorias.md).
