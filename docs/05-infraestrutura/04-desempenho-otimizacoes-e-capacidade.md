# NoPonto — desempenho, otimizações e capacidade

**Finalidade:** explicar mecanismos que reduzem custo e seus compromissos, sem convertê-los em ganho medido.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Estado:** implementação e configuração inspecionadas; não houve benchmark. Medições do host são fotografia pontual.

## Rodoviário

| Mecanismo | Gargalo reduzido e funcionamento | Custo/limite |
|---|---|---|
| polling incremental | SPPO usa janela, overlap, catch-up e handoff para cobrir atraso sem buscar todo histórico | overlap duplica dados; depende de relógio/provider |
| deduplicação/estado causal | rejeita observações antigas/duplicadas antes de publicar | mantém estado Redis e exige CAS correto |
| batch/set-based | matching combinado envia lotes ao PostgreSQL/PostGIS, reduzindo round-trips | lote maior aumenta memória/latência de cauda |
| filtro espacial/índices | GiST e `ST_DWithin` reduzem candidatos antes da projeção longitudinal | plano precisa medição; casts geography custam CPU |
| demanda | matching/publicação por linhas assinadas quando enriquecimento global está desligado | primeira assinatura pode exigir hidratação; cobertura depende da política |
| cache/TTL | estrutura em memória e posições Redis evitam releitura relacional | invalidação/versionamento e perda no restart |
| outbox/telemetria assíncrona | desacopla persistência secundária do hot path | cria backlog, retenção e duplicidade idempotente |
| channels bounded | limita memória e aplica backpressure | `Wait` pode propagar atraso; outras filas podem descartar conforme política |

Defaults versionados incluem polling GPS de 20 s, ETA V2 channel 5.000/batch 250 e batches de shadow/outbox de 100; ambiente produtivo pode sobrescrever. Métricas internas calculam duração, batches e aceite, mas são principalmente emitidas em logs, não séries consultáveis.

## Ferrovia

O gate schedule-aware acorda consultas próximo às viagens esperadas; catálogo de sentinelas, discovery com stride, pursuit limitado, reacquisition, single-flight, semáforo de concorrência, budget por minuto e backoff reduzem chamadas redundantes. Cache de schedule/topologia reaproveita estrutura. A interpolação local move o trem entre evidências sem consultar continuamente o provider; freshness/staleness limita extrapolação.

O benefício é menor tráfego externo com cobertura orientada pela grade. Custos: estado em memória se perde; grade incorreta suprime ou desperta probes inadequados; baixa taxa pode atrasar descoberta. Produção anteriormente confirmou máximo configurado baixo e concorrência 1, mas não há série de cobertura/requisições.

## Frontend

TanStack Query e caches estruturais evitam requests repetidos; contratos por versão permitem reutilizar geometria. A WebView MapLibre é criada uma vez e recebe `updateMap`, `updateRealtime` e `updateUser`, evitando reconstruir toda a estrutura a cada posição. Filtros por linha/sentido, hidratação sob demanda, animação local e pausa/adequação ao background reduzem rede/renderização. Ganhos não foram perfilados; WebView, serialização e mapa/CDN continuam custos e dependências.

## Banco, concorrência e capacidade

Npgsql compartilhado usa pooling padrão da biblioteca; limites produtivos do pool não foram confirmados. Há queries parametrizadas, CTEs, `NpgsqlBatch`, advisory locks, índices e projeções Redis. Histórico, telemetria, previsões e schedules podem crescer continuamente; retenções existem em alguns streams/outbox, mas a política completa do PostgreSQL não foi comprovada. Não foram executados `COUNT(*)`, `EXPLAIN ANALYZE` nem scans grandes.

Na amostra: API ~307–327 MiB, PostgreSQL ~1,11 GiB, Redis ~115 MiB; CPU do banco variou fortemente. Host: 3,7 GiB RAM + 5 GiB swap, raiz 71%. Esses números não são média, pico nem capacidade segura. Tamanho agregado do banco, maiores relações/índices, Docker layers/logs e taxas de crescimento atuais permanecem `NÃO VERIFICADO`.

## Gargalos e projeção qualitativa

- CPU: matching espacial e workers disputam 2 núcleos físicos; medir tempo de ciclo e fila.
- RAM: banco, API e stacks externas podem empurrar o host para swap; ML futuro requer orçamento próprio.
- Redis: `noeviction` transforma saturação em erro de escrita; acompanhar used/maxmemory, stream e pending.
- Disco: telemetria, WAL, índices, logs Docker e imagens podem preencher raiz; medir crescimento e retenção.
- Providers: latência/rate limit controla freshness; acompanhar budget, erros e idade do dado.

Melhorias devem começar por medição leve, orçamento e alertas; separar workers ou aumentar recursos só depois de perfis. Fontes: serviços GPS/ferrovia/ETA, repositórios PostGIS, frontend, Compose e docs 03/04. Testes de performance existentes foram inspecionados em etapas anteriores, não executados aqui.

Relacionados: [observabilidade](05-logs-metricas-e-observabilidade.md), [Redis](../04-dados/07-redis-estado-operacional-e-consistencia.md) e [riscos](08-operacao-riscos-e-plano-de-melhorias.md).
