# NoPonto — operação, riscos e plano de melhorias

**Finalidade:** sintetizar decisões, riscos priorizados e evolução proporcional aos recursos disponíveis.  
**Data:** 2026-10-06. **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Estado:** síntese de código, configuração, documentação e fotografia produtiva; propostas não implantadas.

## Decisões e trade-offs

| Decisão | Benefício atual | Custo/risco | Alternativa futura |
|---|---|---|---|
| servidor próprio + Compose | baixo custo e operação simples | ponto único, rede/energia/recursos compartilhados | host gerenciado ou segundo nó/restore rápido |
| API com workers | DI/deploy simples | blast radius e contenção | separar somente workers medidos como críticos |
| PostgreSQL/PostGIS | integridade e matching espacial set-based | CPU/disco e operação especializada | manter, otimizar e monitorar; não há substituto justificado |
| Redis efêmero | cache/TTL/CAS rápidos | perda de streams/pending e falha por `noeviction` | persistência seletiva ou tornar todo evento reconstruível |
| batch/channels/outbox | menos round-trips e hot path desacoplado | backlog, latência e observabilidade | ajustar por métricas, não por suposição |
| SignalR/polling ferroviário | realtime direcionado e controle de providers | reconexão/estado e custo periódico | backplane/escala só se necessário |
| GHCR/Actions + `latest` | automação simples | rastreabilidade/rollback fracos | promoção por digest e provenance |
| Expo/EAS | build Android gerenciado | dependência externa e versão distribuída opaca | registrar artefato/build/versionamento |

## Critério de severidade

`Crítico` combina perda/controle amplo com recuperação inexistente; `alto` ameaça segurança, dados ou disponibilidade principal; `médio` degrada operação/rastreabilidade; `baixo` é impacto limitado. Probabilidade só é classificada quando há evidência qualitativa; não se inventam percentuais.

## Matriz de riscos

| ID/tipo | Risco e componente | Evidência | Prob. | Impacto/severidade | Medida, esforço e dependência | Estado/TCC |
|---|---|---|---|---|---|---|
| SEC-01 | socket Docker na API | mount + cliente compilado | plausível | controle do host / **alto** | remover/proxy allow-list; médio; inventariar uso | aberto; justificar hardening |
| SEC-02 | DB/Redis publicados e API sem auth observável | bindings e pipeline | plausível | acesso indevido / **alto** | confirmar firewall, fechar portas, auth/políticas; médio | alcance não verificado; defesa |
| OPS-01 | backup/restore não comprovado | só volumes/área secundária | desconhecida | perda durável / **crítico** | backup externo + restore; médio/alto; definir RPO/RTO | aberto; limitação central |
| OPS-02 | host único/rede compartilhada | topologia confirmada conceitualmente | certa como dependência | indisponibilidade ampla / **alto** | runbook, restore, energia/rede; médio | aberto |
| OPS-03 | Redis sem persistência | `save ""`, AOF off | certa em restart | perda quente/streams / **alto** | reconstrução testada ou persistência; médio | aceito sem teste |
| PERF-01 | RAM/CPU limitadas e swap | 3,7 GiB, i3, ML exit 137 | plausível | atraso/OOM / **alto** | métricas, budgets, limites; baixo/médio | aberto; medir |
| PERF-02 | raiz 71% e crescimento | fotografia do disco; retenção parcial | plausível | parada por disco / **alto** | alerta, rotação, retenção; baixo | aberto |
| OPS-04 | `latest` e rollback indefinido | Compose/workflow | frequente no deploy | versão não reproduzível / **médio** | digest/revision/runbook; baixo | aberto |
| OBS-01 | sem métricas/alertas persistentes | métricas em memória/log | certa | detecção tardia / **alto** | níveis 1–4; incremental | aberto; plano experimental |
| FUNC-01 | provider/mapa indisponível ou dado velho | dependências externas | plausível | informação incorreta/ausente / **médio** | freshness, UI degradada, alertas; médio | parcialmente tratado |
| OPS-05 | processo único reúne HTTP/workers | composição DI | certa | falha compartilhada / **médio** | isolamento somente após perfil; alto | trade-off aceito |
| CI-01 | CI sem testes/smoke/security | workflows | certa | regressão implantável / **alto** | testes e gates graduais; médio | aberto; validade do TCC |

## Prioridades

1. **Estabilização imediata:** comprovar backup/restore; confirmar/restringir exposição; tratar socket; alertar disco e indisponibilidade.
2. **Rastreabilidade:** imagem por digest/revision, health/readiness, smoke pós-deploy e rollback documentado.
3. **Observabilidade:** métricas leves de ciclo, provider, filas, Redis, DB e host; retenção remota se possível.
4. **Capacidade:** medir 7–30 dias, definir budgets e ajustar batches/pools/retention; só então separar workers ou dimensionar ML.
5. **Resiliência funcional:** ensaiar restart Redis/API, reconstrução ferroviária e estados degradados no frontend.

## Elementos para diagramas

- implantação física: dispositivo, internet/Tailscale, host Debian, Docker Engine e armazenamento;
- containers/C4: API com workers, PostgreSQL, Redis, ML legado off, providers e frontend;
- rede: HTTP/SignalR, PostgreSQL, RESP, Docker socket, GHCR, SSH/Tailscale e EAS;
- CI/CD: commit, runner, Buildx, GHCR, Compose/script e APK EAS;
- observabilidade: logs/health atuais e collector/dashboard/alerta propostos;
- recuperação: falha → detecção → parada/reconstrução/restore → validação.

## Pendências e governança

Faltam firewall/TLS, backup/restore, digest atual, tamanhos do banco/volumes/logs, métricas históricas, configuração de pools e política RPO/RTO. Cada mudança futura deve ter responsável, evidência, rollback e validação. Nenhum risco autoriza alteração automática da produção.

Fontes: todos os documentos desta etapa, auditorias 07/10/15, Compose, workflows e código. Relacionados: [observabilidade](05-logs-metricas-e-observabilidade.md), [segurança](06-seguranca-e-superficie-de-exposicao.md) e [resiliência](07-resiliencia-backup-e-recuperacao.md).
