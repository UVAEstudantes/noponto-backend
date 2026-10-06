# Rastreabilidade de objetivos e requisitos

**Finalidade:** ligar objetivos/requisitos a implementação, contrato, teste, evidência e pendência. **Baseline:** 2026-10-06. Estados mantêm a classificação de 2.1; teste listado não implica execução recente.

## Objetivos específicos

| Objetivo | Implementação/evidência | Validação/estado |
|---|---|---|
| OE-01, OE-02 | estrutura V2, importações, proveniência, DER 06 | implementado; execução reproduzível a consolidar |
| OE-03, OE-04 | sources, polling, validator, PostGIS, viagem | operacional; suíte/experimento consolidado pendente |
| OE-05 | REST, Redis e SignalR | operacional; E2E/reconnect pendente |
| OE-06 | schedules, binding, engine e snapshot rail | operacional; qualidade/cobertura pendente |
| OE-07 | app, WebView e mapa | código implementado; dispositivo pendente |
| OE-08 | PostgreSQL durável, Redis/TTL efêmeros | implementado; recovery pendente |
| OE-09 | ETA V2 foundation/ground truth | migração OFF; avaliação pendente |
| OE-10 | classes xUnit e cenários | testes existem; relatório por commit pendente |
| OE-11 | documentação 00–08 | concluído documentalmente; revisão acadêmica pendente |

## Requisitos funcionais

| Requisito | Implementação/contrato | Teste ou cenário/evidência | Estado/pendência |
|---|---|---|---|
| RF-001 | EstruturaLinhasController `/linhas` | controller/estrutura V2 | operacional |
| RF-002 | sentidos/padrões V2 | EstruturaLeituraV2* | operacional |
| RF-003 | itinerário por PadraoVersao | PostGIS/estrutura tests | operacional |
| RF-004 | ModaisController | consulta/catalogação | operacional |
| RF-005 | MapLibre/WebView | mapStructure/roteiro mobile | implementado; dispositivo pendente |
| RF-006 | busca/telas | searchPresentation/history | implementado; UX pendente |
| RF-007 | collector/sources GPS | GpsSppoCollector/Fontes tests | operacional |
| RF-008 | validator, causal, CAS | GpsLeitura/EstadoCausal/Redis tests | operacional; executar suíte |
| RF-009 | matching PostGIS | testes repository/diferencial/representativo | operacional; relatório pendente |
| RF-010 | próxima ocorrência/volta | CircularNextOccurrence/Ocorrencia tests | operacional |
| RF-011 | cache veículo/linha | PosicaoVeiculoCache/VeiculosLinha tests | operacional; Redis efêmero |
| RF-012 | GpsHub `PosicaoAtualizada` | cenário inscrição/cancelamento | operacional; E2E pendente |
| RF-013 | adapter/mapa rodoviário | veiculosMapa tests | implementado; dispositivo pendente |
| RF-014 | RailSchedule tables/import | RailScheduleImportTests | operacional |
| RF-015 | binding/tracker/estimador | testes TremRealtime e evidência produtiva | operacional; precisão pendente |
| RF-016 | snapshot/polling/mapa rail | railPresentation/geometry tests | implementado; dispositivo pendente |
| RF-017 | eventos de parada | EventosParadaProjectionTests | operacional |
| RF-018 | AsyncStorage preferências | cenário reinício | implementado; migração schema pendente |
| RF-019 | searchHistory | searchHistory.test | implementado/local |
| RF-020 | tarifa entity/controller/UI | fluxo completo ausente | parcial; service excluído |
| RF-021 | UI POI/legado excluído | nenhuma prova E2E atual | parcial/incompleto |
| RF-022 | GpsEtaClient fail-open | testes/código cooldown | migração; serviço OFF |
| RF-023 | EtaV2 shadow/workers/DB | Foundation/Hardening/Postgres tests | migração OFF; sem resultado |
| RF-024 | Rotinas placeholder | nenhum | planejado |
| RF-025 | roteamento multimodal | nenhum | planejado |
| RF-026 | comparação alternativas | nenhum | planejado |
| RF-027 | alertas | nenhum | planejado |
| RF-028 | metrô | nenhum provider/contrato | planejado |

## Requisitos não funcionais

| Requisito | Mecanismo/evidência | Estado e lacuna |
|---|---|---|
| RNF-001 | CAS + estado causal/testes | operacional |
| RNF-002 | catches/fail-open/tests ETA/telemetria | operacional; Redis total não coberto |
| RNF-003 | SRID 4326, GiST, PostGIS tests | operacional |
| RNF-004 | TTL/`SemSinal` | operacional; metas configuráveis |
| RNF-005 | versão/fonte/hash | operacional; publicação controlada |
| RNF-006 | ILogger e métricas internas | implementado; sem backend/painel |
| RNF-007 | batch/instrumentação | parcial; p95/meta pendentes |
| RNF-008 | health PostgreSQL/Redis/restart | parcial; API/SLO/recovery pendentes |
| RNF-009 | controles insuficientes | proposto; auth/portas/socket |
| RNF-010 | env/Secrets e scan documental | não validado integralmente; rotação pendente |
| RNF-011 | retenções parciais | proposto; política/privacidade pendente |
| RNF-012 | exclusões, docs e testes | parcial; limpeza coordenada |
| RNF-013 | Expo/EAS Android preview | não validado em plataformas alvo |
| RNF-014 | campos rail/origem/confiança | não validado com usuários |
| RNF-015 | labels pontuais | proposto; auditoria de acessibilidade |

## Regra de evidência

Classe comprova implementação, não atendimento. Atendimento requer contrato exercitado, resultado rastreável e critério. Fontes: [requisitos originais](../01-projeto/04-requisitos-e-funcionalidades.md), [catálogo de evidências](04-catalogo-de-evidencias.md) e [plano de validação](03-plano-de-testes-e-validacao.md).
