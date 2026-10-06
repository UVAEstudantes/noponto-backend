# 01 — Realtime de ônibus/BRT: fluxo completo

**Finalidade:** acompanhar uma observação rodoviária da fonte ao mapa.  
**Data:** 2026-10-06 · **Etapa:** 2.3A  
**Baseline:** backend `53567bdfe3dad87bf767c77b9c3f69cf67a7a73d`; frontend `c69a92ea161e749b77842d35f82e14a86cbfc66e`; ML `08c493baddb83db37c935dc459ca4d7a93aeab2d`.  
**Verificação:** código local vigente, testes inventariados e configuração de produção read-only já reconciliada em [15 — produção](../00-auditoria/15-reconciliacao-producao-e-contexto.md). Nenhum provider foi consultado e nenhum teste dependente de infraestrutura foi executado nesta etapa.

## 1. Escopo e estado operacional

O runtime rodoviário possui dois caminhos primários independentes:

| Modal | Fonte primária | Aquisição | Produção em 2026-10-06 |
|---|---|---|---|
| ônibus/SPPO | `ZIRIX_DIRECT` | `GpsSppoCollectorService`, janelas e handoff com ACK | ativo |
| BRT | `BRT_CURRENT` | `GpsPollingService` por `GpsBrtPollingGate` | ativo |

`DATARIO` está configurada como shadow de ambos. O resolver conhece essas fontes, mas o ciclo operacional mostrado em `GpsPollingService.ProcessarCicloAsync` consome somente o snapshot Zirix e o resultado BRT. Não há promoção automática de Data.Rio a primária nesse caminho.

Em produção estavam ativos matching batch, combinado set-based, correção temporal e seu shadow. ETA V2 estava desligado; o serviço ML antigo estava intencionalmente desligado, embora o cliente legado continue no pipeline em modo fail-open.

## 2. Sequência ponta a ponta verificável

1. `GpsSppoCollectorService.ColetarUmaVezAsync` consulta a fonte BUS primária por janela; `GpsBrtClient.BuscarResultadoAsync` consulta o snapshot BRT quando o gate permite.
2. Os clients desserializam DTOs externos, descartam identidade/linha/coordenadas/timestamps inválidos e produzem `PosicaoVeiculoDto` com metadados internos da fonte.
3. O coletor SPPO entrega um `LoteSppoSnapshot` em memória e só o remove após ACK do polling. O BRT entra diretamente no ciclo, com cache do último sucesso no gate.
4. `GpsPollingService.ProcessarCicloAsync` concatena as fontes, mantém a observação de maior `TimestampGps` por `Ordem` e descarta idade superior a `MaxIdadeGpsSegundos` (default 300 s).
5. O serviço lê `veiculo:{ordem}:ativo`. Leitura com timestamp menor ou igual à aceita não avança. A leitura anterior também alimenta bearing, velocidade e detecção de salto.
6. Linhas assinadas — ou todas, se `EnriquecerTodasLinhas=true` — recebem matching. `ViagemObservadaService.LerContextoAsync` fornece a versão/progresso da viagem antes da consulta espacial.
7. `GpsEnriquecimentoService` calcula bearing confiável, média de velocidade e consulta `GpsPadraoRepository`. O resultado contém linha/sentido/padrão/versão, fração longitudinal, comprimento e próxima ocorrência.
8. `GpsEtaClient.PredizirLoteAsync` tenta preencher ETA legado. Falha ou cooldown devolve nenhuma previsão e não interrompe o GPS.
9. Se habilitado, `CorrecaoTemporalPosicaoCoordinator.PrepararAsync` calcula um candidato causal. Esse candidato não altera o `PosicaoVeiculoDto` operacional nesta baseline.
10. `PosicaoVeiculoCacheRepository.TentarAtualizarAsync` adquire lock por veículo e executa CAS Lua por timestamp, gravando `:ts`, `:ativo` e `:recente`. Este é o aceite operacional B.
11. Somente após `Accepted`, `ViagemObservadaService.AtualizarAsync`, ETA V2 shadow e telemetria ML são acionados. Falha da viagem/telemetria é isolada do aceite GPS.
12. Após o lote, o estado causal C é persistido apenas para posições aceitas em B; o shadow de correção também depende dos dois aceites.
13. Índices `linha:{codigo}:veiculos` são mesclados. Para assinantes, ativos do ciclo são combinados com payloads `:recente` marcados `SemSinal`.
14. O grupo SignalR recebe `PosicaoAtualizada` com um array. O frontend adapta o contrato, substitui o snapshot das linhas presentes e envia atualização leve à WebView.
15. `window.updateRealtime` reutiliza a estrutura já carregada; `window.updateMap(...realtimeOnly:true)` cria, move e remove marcadores sem reconstruir as geometrias.

## 3. Estados que não devem ser confundidos

| Estado | Significado |
|---|---|
| recebida | item existente no JSON externo |
| normalizada | item convertido para `PosicaoVeiculoDto`, com identidade, coordenada e timestamp básicos válidos |
| válida no ciclo | vencedora por veículo e não mais velha que a janela de idade |
| enriquecida | matching e campos de estrutura tentados; pode continuar com campos estruturais nulos |
| operacionalmente aceita | CAS Redis B confirmou timestamp estritamente crescente e escrita integral |
| publicada | entrou no snapshot de uma linha assinada; requer ainda preparação dos índices e broadcast bem-sucedido |

Uma posição pode ser aceita sem enriquecimento quando não há assinante e `EnriquecerTodasLinhas=false`. Também pode ser aceita com matching inconclusivo. Portanto, “aceita” não significa “associada a uma rota”.

## 4. Dados e responsabilidades

- PostgreSQL/PostGIS: estrutura V2, geometria, ocorrência seguinte, viagem durável, eventos/outbox e histórico assíncrono.
- Redis: posição aceita quente/recente, timestamp de controle, índice por linha, estado causal e projeção quente de viagem.
- memória do processo: handoff SPPO, cache BRT, padrão/bearing e velocidades do enriquecedor, assinaturas SignalR.
- SignalR: entrega push apenas a linhas demandadas.
- REST: leitura sob demanda do mesmo estado Redis por veículo ou linha; não é o transporte normal do mapa atual.

Não existe uma transação única envolvendo PostGIS, ETA, Redis, viagem, telemetria e SignalR. O CAS da posição é atômico apenas dentro do script Redis; as fases posteriores têm falhas independentes.

## 5. ETA no caminho

O ETA legado ocorre **depois do matching e antes do CAS**. Recebe posições enriquecidas elegíveis e devolve `EtaProximaParadaSegundos`/`EtaConfianca`. Timeout HTTP é 3 s; erros iniciam cooldown e preservam a posição sem ETA. ETA V2 é enfileirado **depois do aceite e da atualização de viagem**, sem consulta PostgreSQL no hot path. Na baseline de produção: `Enabled=false`, `ShadowEnabled=false`, `CanaryPercent=0`.

## 6. Falhas e limites principais

- Falha primária não aciona Data.Rio automaticamente.
- Falha Redis impede confirmação e publicação da observação nesse ciclo; o lote SPPO permanece pendente quando houve falha de infraestrutura.
- Falha PostgreSQL no matching resulta em posição sem associação ou ciclo degradado, conforme o ramo; não transforma candidato em match.
- O backend só envia linhas com pelo menos um payload ativo/recente. Não foi encontrado broadcast de array vazio; logo a remoção do último veículo por expiração pode depender de um evento posterior que talvez nunca venha.
- Estado de assinaturas e caches do enriquecedor são locais à instância. Não há backplane SignalR nem registro distribuído de demanda demonstrado.
- O cliente usa `withAutomaticReconnect()`, mas não mantém catálogo de inscrições nem reinscreve linhas no callback `onreconnected`; grupos SignalR são ligados à conexão.

## 7. Algoritmos e custo

Deduplicação no ciclo agrupa `n` observações por veículo e ordena cada grupo por timestamp; no pior caso atual, a ordenação soma `O(n log n)`. Matching não deve ser reduzido a uma complexidade simples: seu custo depende de candidatos estruturais, funções PostGIS, índices e plano. O modo batch reduz round-trips por chunks; não prova latência produtiva. O CAS é uma operação Lua por veículo, precedida por aquisição de lock com até 60 tentativas de 25 ms.

## 8. Testes e evidência

Há testes de clients/providers (`GpsSppoClientTests`, `GpsBrtTelemetriaTests`, `GpsDatarioClientTests`), cadência/fontes/coletor, validação, matching individual/batch/set-based/circular, CAS, estado causal e viagem. No frontend, `estruturaV2.test.ts` e `veiculosMapa.test.ts` cobrem partes do contrato/cache/reconciliação. Existência não prova aprovação atual; nenhum teste de infraestrutura foi executado nesta tarefa.

## 9. Preparação para diagramas

**Sequência de polling:** Zirix → `GpsSppoCollectorService` → `GpsSppoSnapshotStore` → `GpsPollingService`; BRT → `GpsBrtPollingGate` → polling.  
**Sequência completa:** Polling → Redis leitura → `ViagemObservadaService.LerContextoAsync` → `GpsEnriquecimentoService` → PostGIS → ETA legado → CAS Redis → viagem/telemetria/ETA V2 → índice → `GpsHub` → hook → WebView.  
**Métodos-âncora:** `ColetarUmaVezAsync`, `ProcessarCicloAsync`, `EnriquecerLoteComContextoAsync`, `TentarAtualizarAsync`, `AtualizarAsync`, `SendAsync`, `reconciliarSnapshotsRodoviarios`, `updateRealtime`.

## 10. Referências e pendências

Detalhes: [aquisição/estado causal](02-aquisicao-validacao-e-estado-causal.md), [matching/viagem](03-matching-v2-e-viagem-operacional.md), [Redis/SignalR](04-redis-signalr-e-publicacao.md), [frontend](05-frontend-realtime-e-renderizacao.md) e [falhas/testes](06-falhas-casos-limite-e-validacao.md).

Pendências: validar E2E o desaparecimento do último veículo, reinscrição após reconnect, comportamento multi-instância e diferenças da configuração efetiva do frontend distribuído.
