# NoPonto — requisitos e funcionalidades

**Finalidade:** manter um catálogo rastreável de requisitos funcionais e não funcionais, com critério de verificação e estado real.  
**Data de elaboração:** 2026-10-06  
**Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Estado de verificação:** requisitos derivados da implementação e do roadmap; metas não medidas são explicitamente propostas.

## Convenções de estado

- **OPERACIONAL:** implementado e observado na implantação de referência; não implica qualidade integralmente validada.
- **IMPLEMENTADO_NAO_VALIDADO:** código/call site confirmado, sem validação integral em dispositivo ou ponta a ponta.
- **PARCIAL:** apenas parte do comportamento está disponível ou há quebra de integração.
- **EM_MIGRACAO:** implementação coexistente com legado ou aguardando promoção.
- **PLANEJADO:** visão/requisito ainda não implementado.
- **PROPOSTO:** requisito não funcional desejável, mas sem meta ou validação aprovada.

## Requisitos funcionais

| ID | Nome e descrição | Interessado | Critério de verificação | Estado atual | Evidência | Dependências e observações |
|---|---|---|---|---|---|---|
| RF-001 | **Consultar linhas.** Pesquisar/listar linhas com paginação e filtros compatíveis com modal/tipo. | passageiro, app | consulta retorna itens V2 coerentes com filtros | OPERACIONAL | `/linhas`; `EstruturaLinhasController`; `estruturaV2.ts` | depende da estrutura importada |
| RF-002 | **Consultar sentidos e padrões.** Exibir os sentidos e variantes publicadas de uma linha. | passageiro, app | linha válida retorna sentidos; cada sentido retorna padrões/versão atual | OPERACIONAL | `/linhas/{codigo}/sentidos`; `/sentidos/{id}/padroes` | depende de identidades e versão atual |
| RF-003 | **Obter percurso versionado.** Retornar geometria e ocorrências ordenadas de uma versão. | app/mapa | versão válida retorna LineString e ocorrências na ordem | OPERACIONAL | `/padroes-versoes/{id}/itinerario`; `PadroesVersoesController` | contrato autoritativo V2 |
| RF-004 | **Consultar modais.** Disponibilizar os modais cadastrados para filtros e apresentação. | passageiro, app | `/modais` retorna catálogo ativo | OPERACIONAL | `ModaisController`; 2 modais confirmados no banco | cobertura não equivale aos quatro modais planejados |
| RF-005 | **Visualizar mapa estrutural.** Desenhar percursos, paradas/estações e camadas selecionadas. | passageiro | roteiro em dispositivo mostra estrutura V2 no MapLibre | IMPLEMENTADO_NAO_VALIDADO | `mapOSM.tsx`, `mapScript.ts`, `app/index.tsx` | MapLibre remoto em WebView; build distribuído não verificado |
| RF-006 | **Pesquisar e selecionar linhas.** Oferecer busca por código/nome/destino e seleção no mapa/tela de linha. | passageiro | busca retorna opções e seleção carrega padrões/estrutura | IMPLEMENTADO_NAO_VALIDADO | `buscarOpcoesPorNome`, `linhasContainer.tsx`, `app/linhas.tsx` | depende de RF-001–003 |
| RF-007 | **Adquirir posições rodoviárias.** Coletar e normalizar GPS de ônibus/BRT por fontes configuráveis. | backend, passageiro | ciclo registra posições normalizadas da fonte primária sem depender do shadow | OPERACIONAL | `GpsSppoCollectorService`, `GpsPollingService`, adapters; flags produção | providers externos |
| RF-008 | **Validar e ordenar observações GPS.** Rejeitar leituras inválidas/antigas e impedir regressão temporal. | backend | testes/cenário demonstram rejeição e CAS por timestamp | OPERACIONAL | `GpsLeituraValidator`, correção temporal, `PosicaoVeiculoCacheRepository` | parâmetros configuráveis; validar suíte |
| RF-009 | **Associar veículo à estrutura V2.** Determinar linha, sentido, padrão/versão e posição longitudinal. | backend, passageiro | amostra conhecida produz associação coerente ou falha fechada | OPERACIONAL | `GpsEnriquecimentoService`; `GpsItinerarioRepository.*`; matching flags ON | exige PostGIS e estrutura V2 |
| RF-010 | **Determinar próxima ocorrência.** Identificar próxima parada considerando repetição, circularidade e volta. | passageiro | casos linear/circular/repetido passam em teste e aparecem no DTO | OPERACIONAL | serviço GPS, ocorrência V2 e testes específicos | depende de viagem/version pinning |
| RF-011 | **Manter snapshots rodoviários.** Armazenar posição ativa/recente e consultar veículo/linha. | app, backend | leitura REST/Redis respeita aceite e TTL | OPERACIONAL | Redis; `/veiculos/{ordem}`; `/veiculos/linha/{codigo}` | Redis é efêmero; TTL configurável |
| RF-012 | **Atualizar veículos rodoviários em tempo real.** Publicar por linha para clientes inscritos. | passageiro, app | inscrição recebe `PosicaoAtualizada` e cancelamento cessa o grupo | OPERACIONAL | `GpsHub`; `GpsPollingService.cs:577-640`; `gpsHub.ts` | DTO de compatibilidade é legado ativo |
| RF-013 | **Exibir veículos rodoviários no mapa.** Reconciliar snapshots e filtrar pelas versões visíveis. | passageiro | demonstração mostra atualização sem duplicação indevida | IMPLEMENTADO_NAO_VALIDADO | `useMobilidadeRio.ts`; `veiculosMapa.ts`; WebView | depende de RF-003, RF-012 |
| RF-014 | **Manter grade ferroviária versionada.** Representar patterns, runs e stops por calendário. | backend | versão publicada gera runs/stops consultáveis pelo runtime | OPERACIONAL | tabelas `RailSchedule*`; schedule importado | dados coletados externamente |
| RF-015 | **Inferir estado e posição ferroviária.** Combinar schedule, evidências, binding e estimadores com fonte/qualidade explícitas. | passageiro, backend | snapshot diferencia Live/Estimated/Scheduled e degrada quando stale | OPERACIONAL | `TremRealtime/*`; flags ferroviárias ON; `/rail/vehicles/snapshot` | posição não é GPS real; qualidade precisa de avaliação |
| RF-016 | **Exibir veículos ferroviários.** Consultar snapshots por polling, hidratar geometria e desenhar no mapa. | passageiro | roteiro em dispositivo mostra veículo e rótulo de confiança | IMPLEMENTADO_NAO_VALIDADO | `useRailRealtime.ts`; `railRealtime.ts`; `mapScript.ts` | demo condicional deve estar desligada/identificada |
| RF-017 | **Consultar eventos de parada.** Retornar eventos/previsões contextualizados por parada. | passageiro, app | endpoint retorna eventos ordenáveis com qualidade/origem | OPERACIONAL | `/paradas/{id}/eventos`; `EventosParadaService`; frontend | precisão depende do matching/estado operacional |
| RF-018 | **Persistir preferências locais.** Guardar seleção de linhas, modal, tema, estilo e lateralidade. | passageiro | reinício do app restaura valores válidos | IMPLEMENTADO_NAO_VALIDADO | `storage.ts`, `ProvedorTema.tsx` | AsyncStorage; migração de schema não documentada |
| RF-019 | **Manter histórico de pesquisa.** Registrar e apresentar buscas recentes por categoria. | passageiro | seleção persiste; limite/deduplicação funcionam | IMPLEMENTADO_NAO_VALIDADO | `searchHistory.ts` e testes | somente local |
| RF-020 | **Consultar tarifas.** Disponibilizar tarifa cadastrada associável ao contexto de linha/modal. | passageiro | endpoint retorna dados reais e UI os apresenta corretamente | PARCIAL | `TarifasController`, componente `tarifas.tsx` | população/uso produtivo não comprovados |
| RF-021 | **Apresentar POIs.** Exibir pontos de interesse associados a percurso/parada. | passageiro | endpoints ativos alimentam a tela sem fallback vazio | PARCIAL | call sites em `app/linhas.tsx`; controllers POI excluídos | integração atualmente quebrada/adiada; decidir destino |
| RF-022 | **Fornecer ETA legado durante a transição.** Consultar serviço antigo sem bloquear GPS. | passageiro, backend | indisponibilidade retorna sem ETA e preserva ciclo; serviço pode responder em ambiente isolado | EM_MIGRACAO | `GpsEtaClient`; chamada no polling; cooldown | serviço desligado intencionalmente; sem validade V2 |
| RF-023 | **Produzir e avaliar ETA V2.** Gerar baseline longitudinal em shadow, persistir contexto e fechar erro com passagem real. | equipe/TCC, futuro passageiro | canary gera previsões, ground truth e métricas sem alterar GPS | EM_MIGRACAO | `EtaV2ShadowService`, channel/workers/repository | flags OFF e tabela vazia em produção |
| RF-024 | **Criar Rotinas.** Definir deslocamento com nome, origem/destino, dias, horários e alternativas. | passageiro | CRUD e acompanhamento definidos/implementados | PLANEJADO | placeholder `favoritos.tsx`; ideias futuras | requisitos detalhados ainda pendentes |
| RF-025 | **Planejar rotas multimodais.** Combinar caminhada e modais com alternativas. | passageiro | motor retorna etapas coerentes e explicáveis | PLANEJADO | roadmap/ideias | grafo, dados, critérios e tarifas ainda não definidos |
| RF-026 | **Comparar alternativas operacionais.** Considerar tempo, espera, baldeações, custo ou confiabilidade. | passageiro | critérios definidos e alternativas comparáveis | PLANEJADO | visão de Rotinas | não prometer “melhor rota” sem modelo/avaliação |
| RF-027 | **Emitir alertas contextuais.** Avisar sobre aproximação, atraso ou impacto na rotina. | passageiro | consentimento, gatilho e entrega verificáveis | PLANEJADO | roadmap | depende de Rotinas, eventos e notificações mobile |
| RF-028 | **Integrar metrô.** Representar estrutura e operação com origem/confiança adequadas. | passageiro | linha, estação e estado aparecem no modelo comum | PLANEJADO | visão de quatro modais | fonte e estratégia operacional não confirmadas |

### Contagem dos requisitos funcionais

| Estado | Quantidade |
|---|---:|
| OPERACIONAL | 13 |
| IMPLEMENTADO_NAO_VALIDADO | 6 |
| PARCIAL | 2 |
| EM_MIGRACAO | 2 |
| PLANEJADO | 5 |
| **Total** | **28** |

## Requisitos não funcionais

| ID | Nome e descrição | Critério de verificação | Estado | Evidência | Dependências/observações |
|---|---|---|---|---|---|
| RNF-001 | **Consistência temporal.** Uma observação antiga/igual não deve substituir posição mais nova. | teste concorrente e métrica de rejeição | OPERACIONAL | CAS Redis e estado causal | relógio/timestamp do provider |
| RNF-002 | **Degradação controlada.** Falha de ETA, telemetria ou shadow não deve bloquear o GPS principal. | testes de falha e ciclo continua | OPERACIONAL | catches/fail-open no polling e workers | não cobre falha total Redis |
| RNF-003 | **Integridade geoespacial.** Geometrias devem usar referência consistente e índices apropriados. | schema/migrations e casos PostGIS | OPERACIONAL | SRID 4326, GIST, NetTopologySuite | qualidade da fonte permanece externa |
| RNF-004 | **Atualização e expiração.** Snapshots não devem permanecer ativos indefinidamente. | TTL e estado `SemSinal` verificados | OPERACIONAL | chaves ativo/recente/linha | metas temporais são configuráveis, não fixadas aqui |
| RNF-005 | **Rastreabilidade estrutural.** Mudanças de percurso devem preservar versão, fonte e hash. | importação gera versão/proveniência sem alterar viagem pinada | OPERACIONAL | modelo V2 e identidades externas | requer processo de publicação controlado |
| RNF-006 | **Observabilidade.** Ciclos e pipelines devem registrar contadores, descartes, falhas e backlog. | painel/relatório coleta métricas relevantes | IMPLEMENTADO_NAO_VALIDADO | métricas/loggers em GPS, rail, ETA e streams | centralização/alertas não verificados |
| RNF-007 | **Desempenho operacional.** O ciclo deve terminar antes da próxima cadência sem backlog crescente. | definir e medir p95 por etapa/ciclo | PARCIAL | instrumentação por etapa e batch | `[META PENDENTE: cadência e p95 aceitáveis]` |
| RNF-008 | **Disponibilidade.** API e dependências críticas devem sinalizar saúde e recuperar de falhas. | healthchecks e teste de recuperação | PARCIAL | Postgres/Redis healthchecks; restart | API sem healthcheck formal; SLO ausente |
| RNF-009 | **Segurança de acesso.** Serviços internos e operações mutáveis devem ser restritos. | autenticação/autorização, rede e revisão de exposição | PROPOSTO | riscos da auditoria | não há controle ativo suficiente comprovado |
| RNF-010 | **Gestão de segredos.** Credenciais não devem constar em código ou documentação. | scan e uso de mecanismo seguro | IMPLEMENTADO_NAO_VALIDADO | variáveis de ambiente; documentação sanitizada | gestão/rotação externa não auditada |
| RNF-011 | **Privacidade e minimização.** Dados operacionais e futuros dados de rotina devem ter finalidade/retenção definidas. | política, inventário e testes de retenção | PROPOSTO | workers de retenção parciais | `[DECISÃO PENDENTE: política LGPD e retenção]` |
| RNF-012 | **Manutenibilidade.** Arquitetura vigente e legado devem permanecer distinguíveis e testáveis. | build sem legado excluído e matriz de dependência atualizada | PARCIAL | `.csproj`, docs 14/15 | limpeza futura controlada |
| RNF-013 | **Portabilidade mobile.** Funcionalidades centrais devem operar nas plataformas alvo declaradas. | build e roteiro Android/iOS por commit | IMPLEMENTADO_NAO_VALIDADO | Expo/React Native | plataformas mínimas ainda não definidas |
| RNF-014 | **Transparência da informação.** A interface deve diferenciar GPS, estimativa e schedule e expor confiança quando aplicável. | teste de UI/contrato | IMPLEMENTADO_NAO_VALIDADO | campos rail e apresentação de confiança | pesquisa de compreensão com usuário pendente |
| RNF-015 | **Acessibilidade e usabilidade.** Fluxos centrais devem ser compreensíveis e acessíveis. | critérios WCAG/mobile e teste com usuários | PROPOSTO | alguns accessibility labels | `[META PENDENTE: protocolo de avaliação]` |

### Contagem dos requisitos não funcionais

| Estado | Quantidade |
|---|---:|
| OPERACIONAL | 5 |
| IMPLEMENTADO_NAO_VALIDADO | 4 |
| PARCIAL | 3 |
| PROPOSTO | 3 |
| **Total** | **15** |

## Matriz resumida de dependências

- RF-007–013 dependem de estrutura V2, PostGIS e Redis.
- RF-014–016 dependem de schedule importado, provider ferroviário e geometria publicada.
- RF-023 depende de viagem operacional, ocorrência V2, passagem observada e política de sampling.
- RF-024–027 dependem de requisitos ainda não definidos de usuário, grafo multimodal, tarifas, notificações e privacidade.
- RNF-009 e RNF-011 devem anteceder qualquer armazenamento de conta, favoritos sincronizados ou Rotinas pessoais.

## Limitações

“Operacional” descreve a fotografia de produção de 2026-10-06 e não substitui testes de aceitação. Não foram inventadas metas quantitativas. Precisão ferroviária, ETA, experiência mobile e disponibilidade necessitam protocolos próprios.

## Evidências

Fontes principais: [auditoria arquitetural](../00-auditoria/14-evolucao-arquitetural-e-legado.md), [reconciliação de produção](../00-auditoria/15-reconciliacao-producao-e-contexto.md), código referenciado e [objetivos](03-objetivos-e-escopo.md).

## Pendências

Revisar estados após cada release e transformar itens propostos em critérios aprovados antes da avaliação final.
