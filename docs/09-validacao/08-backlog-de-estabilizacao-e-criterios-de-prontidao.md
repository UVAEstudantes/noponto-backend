# Backlog de estabilização e critérios de prontidão

**Finalidade:** consolidar riscos; nenhuma correção foi implementada.

| ID/prioridade | Evidência e impacto | Pré-requisitos/plano proposto | Teste/confirmação | Implantação/rollback |
|---|---|---|---|---|
| EST-01 crítico | backup/restore não comprovado; risco de perda PostgreSQL | definir RPO/RTO, cópia externa e runbook | restore isolado, integridade e tempo | janela; preservar backup anterior e abortar sem validação |
| EST-02 alto | socket Docker montado na API; controle amplo do host | inventariar uso, remover ou proxy allow-list | API funciona e socket inacessível | Compose versionado; rollback do mount somente com análise |
| EST-03 alto | PostgreSQL/Redis publicados; alcance/firewall não verificado | mapear consumidores e restringir bind/rede | conectividade interna/administrativa e negativa externa autorizada | acesso de emergência documentado; restaurar regra anterior |
| EST-04 alto | auth/autorização não observável | inventariar mutações e políticas | testes 401/403/escopo e regressão pública | feature/config versionada; rollback sem reabrir mutações |
| EST-05 alto | Redis sem RDB/AOF; streams/pending perdidos | aceitar/reconstruir formalmente ou persistir seletivamente | restart em Redis descartável, medir perdas/rebuild | snapshot/config anterior e capacidade de disco |
| EST-06 alto | SignalR reconecta sem reinscrição garantida | explicitar lifecycle e subscription registry cliente | E2E disconnect/reconnect e recebimento por linha | rollback app/API compatível |
| EST-07 médio/alto | ausência de snapshot vazio em expiração pode manter marcador | definir contrato de remoção/empty snapshot | TTL/provider sumido remove UI no prazo | versionar contrato e manter compatibilidade |
| EST-08 alto | CI sem testes/smoke; regressões chegam ao artefato | separar projeto de teste, filtros seguros e gates | pipeline bloqueia falha e smoke read-only | tornar gate gradual; rollback workflow |
| EST-09 alto | sem métricas históricas/alertas | health API, métricas leves e coleta controlada | alertas sintéticos e custo medido | desabilitar exporter sem afetar API |
| EST-10 médio | imagem `latest`, rollback não comprovado | digest/revision e runbook schema-aware | implantar/voltar artefato em staging | digest anterior preservado |
| EST-11 alto | fixtures `EstadoCausal` quebradas por DI | atualizar doubles para dependência vigente | 10 testes passam sem rede | somente código de teste; reverter patch se mascarar comportamento |
| EST-12 médio | ETA timing test passa/falha | relógio controlado/sinalização determinística | repetição N definida sem intermitência | não promove ETA enquanto instável |
| EST-13 médio | frontend sem runner e lint warning | configurar runner existente sem instalar ad hoc; limpar warning em tarefa própria | 10 arquivos executados + lint/tsc | preservar lockfile/build |

## Critérios de prontidão do núcleo

### Para validação controlada

- ambiente PostgreSQL/PostGIS e Redis descartável, destinos comprovados;
- suite categorizada e comandos por domínio;
- fixtures locais verdes e flakiness ETA resolvida/isolada;
- corpus/snapshots com hashes;
- build mobile identificado;
- observabilidade suficiente para atribuir falhas.

### Para demonstração acadêmica

- opção A/B/C aprovada;
- requisitos do núcleo com evidências reais;
- jornada Android ensaiada inclusive sem dados;
- métricas do eixo experimental e limitações;
- fallback offline/capturas sanitizadas para provider indisponível.

### Para disponibilização pública

- EST-01–06 e EST-08–10 tratados e confirmados;
- privacidade, termos/fontes e política de retenção aprovados;
- monitoramento, resposta a incidente e rollback testados.

Estado atual: núcleo **não pronto para validação integrada completa** por falta de ambiente isolado; documentação está pronta para orientar sua criação.
