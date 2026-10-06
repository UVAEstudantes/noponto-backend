# 06 — Falhas, casos-limite e validação

**Finalidade:** matriz verificável de recuperação, limitações, testes e roteiro E2E.  
**Data:** 2026-10-06 · **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`.  
**Estado:** comportamentos extraídos do código; configuração produtiva datada; testes existentes não foram executados nesta etapa.

## 1. Matriz de falhas

| Cenário | Detecção/responsável | Comportamento implementado | Estado/passageiro | Evidência e limite |
|---|---|---|---|---|
| provider indisponível | client/collector/gate | classifica falha; Zirix preserva watermark/snapshot, BRT pode reutilizar cache | posições anteriores seguem até TTL; depois `SemSinal` | Data.Rio não é fallback automático |
| JSON inválido | clients | Zirix `json_invalido`; BRT catch geral | sem lote novo | não há retry imediato |
| coordenada inválida | client/mapper | descarta item | estado anterior até TTL | ausência não é evento |
| timestamp ausente/futuro | validator | descarta epoch/ausente/>2 min futuro | idem | C usa tolerância própria de 5 s |
| repetido/atrasado | dedupe, comparação e Lua | maior do lote vence; `<=` não avança | posição monotônica | duas barreiras antes/durante CAS |
| salto geográfico | enriquecedor | >2×90 km/h implícitos anula matching | pode publicar GPS sem rota | não rejeita integralmente a posição |
| bearing inconsistente | PostGIS | diferença ≥80° elimina candidato | sem estrutura/próxima parada | bearing não decide sozinho |
| nenhum padrão | matching | campos V2 nulos | frontend tende a ocultar | estado auxiliar expira após 3 ciclos |
| matching ambíguo | score/UUID/histerese | vencedor determinístico | potencial escolha semântica errada | falta ground truth |
| versão ausente | matching/viagem | sem enriquecimento/viagem | marcador pode sumir pelo filtro | não inventa versão |
| troca de sentido | matching/viagem | exige evidência; viagem não reinicia livremente | transição pode ficar pendente | validar com trajetória real |
| Redis indisponível | leitura/CAS | sem confirmação; SPPO pode ficar pendente | último estado até TTL | falha pós-aceite não reverte B |
| PostgreSQL indisponível | matching/viagem | match nulo/falha; viagem isolada após B | veículo sem estrutura | ramos têm degradação distinta |
| histórico/outbox falha | viagem/workers | GPS permanece aceito | mapa continua | analítica atrasada/incompleta |
| ML legado indisponível | `GpsEtaClient` | cooldown 30 s, zero previsões | sem ETA | serviço intencionalmente off |
| ETA V2 off | shadow | nenhuma gravação | sem efeito atual | produção OFF/OFF/0% |
| SignalR desconectado | client | reconnect automático, estado congelado | posição antiga visível | não reinscreve/faz REST fallback |
| versão desconhecida no app | hook | tenta hidratar estrutura | oculto até sucesso | `null` pode ficar cacheado |
| WebView não pronta | bridge | aguarda `map_ready` | ainda sem mapa atualizado | CDN sem fallback comprovado |
| veículo para de enviar | TTL/merge | ativo 40 s, recente 180 s, depois omissão | deveria desaparecer | não há snapshot vazio final |
| veículo muda de linha | memória/índice | remove do índice antigo se observado | pode duplicar | restart/multi-instância enfraquecem |

## 2. Casos-limite algorítmicos

- Circularidade: delta negativo pode ser wrap dirigido; próxima ocorrência volta ao início.
- Parada repetida: identidade é ocorrência+volta, não apenas `ParadaId`.
- Curvas/rotas sobrepostas: bearing local usa `p±0,025` e limite 80°; score não é probabilidade calibrada.
- Veículo parado: bearing anterior/fonte pode sustentar matching; histerese dificulta troca pequena.
- Restart: caches de padrão, velocidade, linha anterior e demanda somem; Redis/PG não recompõem tudo de forma equivalente.
- Concorrência: B, viagem e C têm CAS próprios, sem atomicidade conjunta.
- Resposta Redis perdida: B pode ter sido aplicado, mas não é publicado sem confirmação; retry igual é rejeitado.

## 3. Inventário de testes

| Grupo | Arquivos representativos | Verifica | Natureza |
|---|---|---|---|
| provider/polling | `GpsSppoClientTests`, `GpsSppoCollectorTests`, `GpsPollingFontesTests`, `GpsPollingCadenciaTests`, `GpsBrtTelemetriaTests`, `GpsDatarioClientTests` | schema, fontes, janelas, cadência | unitário/HTTP fake |
| validação | `GpsLeituraValidatorTests`, `GpsEnriquecimentoServiceTests` | coordenada/timestamp, bearing, salto | unitário |
| matching | `GpsItinerarioRepositoryPostgisTests`, `GpsMatchingLotePostgisTests`, `GpsMatchingCombinadoDiferencialPostgisTests`, `GpsPinnedVersionProjectionPostgisTests` | SQL, batch, versão | integração PostGIS |
| direção/circular | `CompetidorDirecionalV2Tests`, `GpsCircularNextOccurrencePostgisTests` | bearing e wrap | unitário/integração |
| aceite B | `PosicaoVeiculoCacheRepositoryTests` | concorrência, lock/fencing, TTL, resposta perdida | integração Redis |
| estado C | `CorrecaoTemporalPosicaoTests`, `EstadoCausalPosicaoTests`, `EstadoCausalPosicaoRedisTests` | estratégias, CAS/retry/fail-open | unitário/integração |
| viagem/outbox | `ViagemOperacionalRegraTests`, `ViagemObservadaServiceTests`, `ViagemOperacionalIntegracaoTests`, `ViagemDuravelPostgresTests`, `ViagemOutboxBatchTests` | estados, cursor, eventos | unitário/integração |
| leitura/contrato | `VeiculosLinhaRuntimeReaderTests`, `FrontendLegacyCompatibilityTests` | ativo/recente e alias | unitário |
| frontend | `veiculosMapa.test.ts`, `estruturaV2.test.ts` | adapter, snapshot, cache | unitário |
| desempenho | benchmarks GPS/causal/shadow | harness/métricas | experimental, não SLA |

Os arquivos existem no baseline, mas não foram executados nesta tarefa. Relatos históricos não equivalem a aprovação atual.

## 4. Cobertura ausente

1. E2E fonte fake → handoff → PostGIS → B/C → viagem → SignalR → WebView.
2. snapshot vazio e remoção do último veículo.
3. reconnect com reinscrição e período offline.
4. múltiplas instâncias com demanda/grupos/caches.
5. mudança de linha/sentido e restart intermediário.
6. payload SignalR fora de ordem e validação runtime frontend.
7. falha CDN/MapLibre e recriação da WebView.
8. ground truth de matching/próxima ocorrência.
9. falha PostgreSQL por ramo batch/fallback.
10. velocidade negativa/não finita.

## 5. Roteiro futuro não destrutivo

Em ambiente isolado: semear rotas linear/circular; servir fixtures determinísticas; registrar cada fronteira; injetar uma falha por vez; conectar cliente SignalR real; instrumentar WebView; validar Redis/PG por leitura; repetir com duas APIs; guardar versões e resultados. Critérios mínimos: monotonicidade B, nenhuma viagem para B rejeitado, ocorrência correta, snapshot vazio identificável, reinscrição e remoção após expiração.

## 6. Sequências para diagramas

**Provider:** client → falha → collector/gate preserva estado → ativo → recente → expiração.  
**Redis:** matching/ETA → Lua falha → sem viagem/C/broadcast → SPPO sem ACK.  
**Frontend:** desconexão → servidor remove demanda → reconnect → hoje sem reinscrição.  
**Expiração:** aceite → `:ativo` expira → `SemSinal` → `:recente` expira → ausência; falta evento vazio.

## 7. Referências e pendências

Referências: classes citadas nos documentos 01–05, testes em `NoPonto/5-Testes`, testes frontend e [reconciliação de produção](../00-auditoria/15-reconciliacao-producao-e-contexto.md).

Pendências prioritárias: snapshot vazio, reconnect/reinscrição e multi-instância. Não impedem compreender o fluxo ou iniciar 2.3B, mas impedem afirmar que todo veículo obsoleto sempre desaparece do mapa.
