# NoPonto — logs, métricas e observabilidade

**Finalidade:** separar sinais realmente produzidos de uma arquitetura futura de monitoramento.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Verificação:** `ILogger`, classes de métricas, workers, healthchecks e ferramentas do host inspecionados; não há backend de séries temporais confirmado.

## Estado atual

A aplicação usa `ILogger<T>` com templates estruturados e níveis Information/Warning/Error. Logs identificam, conforme fluxo, veículo, viagem, evento, provider, batches, duração, contadores, backlog e falha. Há reporters periódicos para GPS/estado causal, shadow, telemetria e ferrovia. Isso torna contadores observáveis nos logs durante a vida do processo, mas não equivale a métricas instrumentadas, persistidas e consultáveis.

| Área | Sinais confirmados no código | Lacuna |
|---|---|---|
| GPS/matching | duração de ciclo/fases, recebidos, aceitos/rejeitados, batch e round-trips | sem série/percentis/dashboard |
| causal/shadow | conflitos, drops, batches, backlog, retention e DLQ | estado em memória reinicia; alertas ausentes |
| Redis/SignalR | logs de falha/publicação no fluxo | latência e conexões não expostas como métrica central |
| ferrovia | requests, hits, rate limiting, tracker/canary/topologia e falhas | cobertura expected-run e freshness sem painel |
| ETA/telemetria | elegibilidade, fila, batches, persistência, realizado/expirado e pending | ETA V2 desligado; métricas não exportadas |
| infraestrutura | healthcheck PostgreSQL/Redis, `docker stats/logs` | API sem healthcheck; sem histórico de host |

Não foi identificado trace distribuído, correlation ID HTTP global, OpenTelemetry, endpoint Prometheus, Grafana ou alerta. Docker mantém logs conforme driver do daemon, mas rotação/tamanho não foram verificados. Dozzle é visualizador de logs, não sistema de métricas, retenção ou alerta. Portainer auxilia operação de containers, não demonstra SLO.

## Proposta incremental compatível com o host

### Nível 1 — mínimo

Adicionar health/readiness da API com dependências separadas; padronizar `traceId`, ciclo, provider e viagem; registrar início/fim/duração; inventariar driver/rotação Docker; criar checklist manual de disco, RAM, swap, container e backlog. Baixo custo, sem novo stack obrigatório.

### Nível 2 — métricas estruturadas

Usar `System.Diagnostics.Metrics`/OpenTelemetry para histogramas e contadores: HTTP, ciclo GPS, matching, queries, Redis, SignalR, requests ferroviários, fila/channel, stream/pending/DLQ, ETA e provider freshness. Coletar em intervalos controlados. Pode-se exportar remotamente ou usar agente leve, evitando manter toda a retenção no host.

### Nível 3 — dashboard

Painéis: disponibilidade/latência API; idade e volume GPS; matching e rejeições; provider ferroviário/cobertura; filas, pending e DLQ; PostgreSQL/Redis; CPU/RAM/swap/disco; ETA quando habilitado. Prometheus/Grafana é uma opção, não requisito; hospedagem remota reduz pressão local.

### Nível 4 — alertas

Alertar por API/down, disco, memória/swap, banco/Redis, ausência anormal de dados, erro de provider, pending/DLQ, channel saturado, crash loop e scanner sem cobertura. Limiares devem nascer de baseline, não de números inventados; usar janela e severidade para evitar ruído.

## Métricas para o TCC

Já calculáveis/logáveis: duração do ciclo e matching, volumes/aceite/rejeição, batches, requests/hits/rate limit ferroviário, backlog e ETA counters. Plano experimental: p50/p95/p99 HTTP e matching, idade do dado exibido, custo por lote, cobertura de expected runs, disponibilidade, CPU/RAM e crescimento do armazenamento. Cada experimento deve registrar commit, flags, janela, carga, método e limitações.

## Riscos, testes e pendências

Sem persistência de métricas, regressões e picos desaparecem; logs excessivos podem consumir disco; identificadores exigem minimização e retenção. Nenhuma ferramenta foi implantada. Pendente: confirmar logging driver/rotação, endpoints de diagnóstico, retenção Dozzle, acesso ao dashboard, métricas do PostgreSQL/Redis e orçamento de coleta.

Fontes: `GpsCicloPerformance`, classes `*Metrics`, reporters, workers de retenção, Compose e auditoria produtiva. Relacionados: [desempenho](04-desempenho-otimizacoes-e-capacidade.md), [segurança](06-seguranca-e-superficie-de-exposicao.md) e [plano](08-operacao-riscos-e-plano-de-melhorias.md).
