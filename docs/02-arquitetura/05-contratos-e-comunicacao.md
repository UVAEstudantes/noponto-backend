# NoPonto — contratos e comunicação

**Finalidade:** registrar superfícies HTTP, SignalR, polling e comunicação entre processos para rastreabilidade e futuros diagramas de sequência.  
**Data:** 2026-10-06 · **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Verificação:** controllers compilados, Swagger produtivo e call sites frontend.

## Convenções HTTP

A API usa JSON e rotas sem prefixo de versão. Controllers retornam `IActionResult`; ausência de recurso resulta em respostas como 404 conforme controller, e exceções atravessam `ExceptionMiddleware`, que produz resposta de erro padronizada. O wrapper frontend `api.ts` aplica base URL/timeout e retorna resultado `ok/data/error`, permitindo degradação para listas vazias em alguns services — comportamento que pode esconder endpoint ausente.

## Matriz de rastreabilidade HTTP

| Método/rota | Controller → serviço/repositório | Parâmetros/retorno principal | Consumidor identificado | Estado |
|---|---|---|---|---|
| GET `/linhas` | `EstruturaLinhasController` → `IEstruturaLeituraV2Repository` | código/nome/modal/tipo, page/pageSize → resposta paginada `LinhaV2` | `estruturaV2.listarLinhasV2` | ATUAL |
| GET `/linhas/{codigo}` | mesmo → leitura V2 | código → detalhe estrutural | nenhum call site central localizado | ATUAL |
| GET `/linhas/{codigo}/sentidos` | mesmo → leitura V2 | código → `SentidoV2[]` | `listarSentidosV2` | ATUAL |
| GET `/sentidos/{id}/padroes` | `EstruturaSentidosController` → leitura V2 | GUID → `PadraoOperacionalV2[]` | `listarPadroesV2` | ATUAL |
| GET `/paradas/{id}` | `EstruturaParadasController` → leitura V2 | GUID → parada | nenhum consumidor direto confirmado | ATUAL |
| GET `/padroes-versoes/{id}/itinerario` | `PadroesVersoesController` → leitura V2 | GUID → geometria+ocorrências | `obterItinerarioPadraoVersaoV2` | ATUAL |
| GET `/modais` | `ModaisController` → `IModalService/Repository` | paginação/filtros → modais | service antigo existe; call site atual não confirmado | ATUAL |
| GET `/sentidos` | `SentidosController` → `ISentidoRepository` | filtros/paginação → sentidos | export antigo sem call site | COMPATIBILIDADE |
| GET `/veiculos/{ordem}` | `VeiculosController` → Redis cache | ordem → posição ativa/recente | nenhum consumidor atual confirmado | ATUAL |
| GET `/veiculos/linha/{codigo}` | controller → `IVeiculosLinhaRuntimeReader`/Redis | código → snapshot por linha | service disponível, hook usa SignalR | ATUAL/ALTERNATIVA |
| GET `/veiculos/padrao-versao/{id}/geometria` | controller → datasource/estrutura | GUID → geometria e comprimento | `railRealtime.getRailGeometry` | ATUAL |
| GET `/veiculos/historico/stats` | controller → repository histórico | janela/filtros → estatísticas | nenhum consumidor mobile conhecido | DIAGNÓSTICO |
| GET `/paradas/{id}/eventos` | `EventosParadaController` → `IEventosParadaService` | GUID → `EventoParadaDto[]` | `eventosParada.buscarEventosParada` | ATUAL |
| GET `/rail/vehicles/snapshot` | `RailVehiclesController` → `IRailPublishedSnapshotProvider` | linha/sentido opcionais → snapshot+vehicles | `fetchRailSnapshot`/hook | ATUAL |
| GET `/tarifas` | `TarifasController` → `ITarifaService/Repository` | filtros/paginação → tarifas | componente existe; fluxo não confirmado | PARCIAL |
| POST `/tarifas` | mesmo | `TarifaCriarDTO` → tarifa criada | nenhum consumidor mobile identificado | ATUAL tecnicamente; segurança pendente |
| GET `/linhas/{linhaId}/detalhes` | `FrontendLegacyMapaController` → `FrontendLegacyMapaService` → EF V2 | GUID → DTO antigo de detalhe | `app/linhas.tsx` | LEGADO_ATIVO |
| GET `/itinerarios/por-linha/{linhaId}/mapa` | fachada → EF V2 | GUID/incluirParadas → mapas antigos | export antigo sem call site atual | LEGADO_ATIVO |
| GET `/itinerarios/itinerario/{id}/mapa` | fachada → EF V2 | ID tratado como versão → mapa antigo | export antigo sem call site atual | LEGADO_ATIVO |

Controllers de POI, relacionamento, linha/itinerário/parada antigos e admin estão excluídos; não são endpoints ativos. O frontend ainda chama `/pois/por-itinerario/{id}` e `/pois/por-parada/{id}`; essas chamadas não possuem rota compilada e degradam para vazio.

## SignalR rodoviário

O hub é mapeado na rota configurável, normalmente `/hub/gps`. O cliente cria conexão com `withAutomaticReconnect`, registra handler `PosicaoAtualizada` e mantém subscribers locais.

| Direção | Método/evento | Conteúdo |
|---|---|---|
| app → hub | `InscreverseLinha(codigoLinha)` | normaliza código e adiciona conexão ao grupo |
| app → hub | `CancelarLinha(codigoLinha)` | remove conexão e contagem local |
| API → app | `PosicaoAtualizada(posicoes[])` | alias de compatibilidade de posição V2: veículo, linha, coordenadas/timestamp, status, versão/progresso/próxima parada e ETA opcional |

O backend publica somente linhas com assinantes. Na desconexão, o hub ajusta registros process-local. Reconexão automática não implica reinscrição automática comprovada em todos os cenários; isso deve ser testado. O payload usa DTO de compatibilidade e não possui versão explícita, criando risco de evolução coordenada.

## Polling ferroviário

`useRailRealtime` chama o snapshot, por padrão, a cada 5 segundos enquanto o app está ativo. Pode filtrar por linha/sentido. Cada veículo traz IDs de run/veículo/linha/sentido/versão, estado, distância na geometria, tempos de referência/alvo, destino/plataforma, `positionSource`, `positionQuality`, freshness, evidência recente e flags de estimativa/clamp. O app busca a geometria por versão, calcula coordenada/heading localmente e desenha o marcador.

Polling foi escolhido na implementação atual; não há canal SignalR ferroviário. O intervalo gera compromisso entre atualização, bateria, rede e carga; não foi comparado formalmente.

## Comunicação interna

Controllers e workers chamam serviços/repositórios por DI dentro do mesmo processo. Channels bounded conectam produção/consumo de telemetria e ETA V2. Redis streams dão durabilidade operacional limitada/replay para alguns pipelines; Npgsql batches persistem. Esses mecanismos não são chamadas de rede entre microsserviços independentes.

## ETA/ML

O caminho legado chama `POST /eta/batch` no serviço FastAPI com código da linha, hora/dia, distância, velocidade, posição na rota e IDs extras ignorados pelo schema antigo. A resposta contém segundos/minutos, confiança heurística e linha conhecida. Falhas retornam dicionário vazio após timeout/cooldown; GPS continua.

ETA V2 não usa HTTP externo no estado atual: `EtaV2ShadowService` produz request interno, `EtaV2Channel` enfileira, batch worker persiste e maintenance resolve/expira. Flags estão OFF, portanto não chega ao passageiro.

## DTOs e versionamento

Tipos TypeScript espelham DTOs, mas validação runtime central não foi demonstrada. IDs antigos chamados `itinerarioId` podem representar `PadraoVersaoId`. Recomenda-se contrato explicitamente versionado para SignalR e remoção gradual de aliases somente após publicação coordenada.

## Limitações e relacionados

Swagger observado continha 18 paths, mas não prova consumidor nem autorização. O POST de tarifa merece controle de acesso. Ver [integrações](04-integracoes-externas.md), [componentes](03-componentes-backend-frontend.md) e futura Etapa 2.3 para sequências detalhadas.

## Evidências e pendências

Controllers ativos, `GpsHub.cs`, `gpsHub.ts`, `railRealtime.ts`, `useRailRealtime.ts`, `api.ts`, DTOs e Swagger. Pendente: OpenAPI versionado, contract tests, reinscrição pós-reconnect e decisão sobre POIs/fachadas.
