# NoPonto — componentes backend e frontend

**Finalidade:** documentar responsabilidades, dependências e fronteiras internas sem antecipar os algoritmos detalhados da Etapa 2.3.  
**Data:** 2026-10-06 · **Baseline:** backend `53567bd`; frontend `c69a92e`; ML `08c493b`  
**Verificação:** composição, compilação e call sites; ativação produtiva conforme auditoria 15.

## Backend: camadas reais

- **1-API:** controllers, `GpsHub`, middleware de exceções e DTOs de borda. Traduz HTTP/SignalR em chamadas internas.
- **2-Application:** orquestração, regras, providers, GPS, ferrovia, importações e `BackgroundService`. É a camada mais extensa e contém tanto casos de uso quanto infraestrutura HTTP/filas.
- **3-Domain:** entidades persistidas e conceitos V2. Não é um domínio totalmente isolado de detalhes de persistência.
- **4-Data:** `DbContext`, repositórios EF/Npgsql/Redis e configuração de datasource.
- **5-Testes:** testes no mesmo projeto compilável, com exclusões pontuais; não é assembly de testes separado.

A separação melhora localização de responsabilidades, mas Application conhece DTOs operacionais, HttpClient e mecanismos de background; Data usa SQL aderente ao schema; `Program.cs` concentra grande composição. Portanto, trata-se de camadas pragmáticas, não de dependências perfeitamente invertidas.

## Subsistemas backend

| Subsistema/estado | Componentes | Entradas | Saídas | Dependências |
|---|---|---|---|---|
| Estrutura V2 — ATUAL | `EstruturaLeituraV2Repository`, entities, controllers | consultas/app, dados importados | linhas/sentidos/padrões/itinerário | PostgreSQL/PostGIS |
| Importação/reconciliação — ATUAL sob comando | GTFS parser/projection/persister/publication; ArcGIS V2 | GTFS/ArcGIS | versões e identidades | DB; não é worker automático comprovado |
| Aquisição GPS — ATUAL | clients, source adapters/resolver, collector/polling | JSON externo | observações comuns | HttpClient/options |
| Matching — ATUAL | `GpsEnriquecimentoService`, repository batch/combined | GPS + contexto de viagem | versão/progresso/ocorrência | PostGIS, hints, histórico |
| Viagem operacional — ATUAL | `ViagemObservadaService/Repository` | enriquecimento aceito | viagem pinada/eventos | Redis, PostgreSQL/outbox |
| Estado causal — ATUAL | codec/repository/coordinator | leitura e timestamp | aceite/correção/shadow | Redis, options |
| Publicação — ATUAL | cache repository, `GpsHub`, controller | posição aceita | Redis/SignalR/REST | Redis, subscribers |
| Ferrovia — ATUAL | schedule, expected runs, gate, scanner, binding, tracker, estimator, engine | schedule/evidências | snapshot com confiança | DB, provider, caches em processo |
| Telemetria — ATUAL | sampling, channel/stream publisher, workers, retention | posição/viagem aceita | Redis stream/PostgreSQL | backpressure/fail-open |
| ETA antigo — LEGADO_ATIVO | `GpsEtaClient` | posição enriquecida | campos ETA opcionais | HTTP, serviço off |
| ETA V2 — EM_MIGRACAO | shadow service, canary, channel, batch/maintenance, repository | posição/viagem | evento de previsão/erro | flags OFF, PostgreSQL |
| Compatibilidade — LEGADO_ATIVO | mapa/detalhe e DTO SignalR | modelo V2 | contrato antigo esperado pelo app | remoção coordenada |
| Erros — ATUAL | `ExceptionMiddleware`, resultados de clients | exceções/erros | respostas/logs/fail-open | tratamento varia por fronteira |

## Estrutura V2 conceitual

`Modal` agrupa o tipo de transporte; `Linha` identifica o serviço público; `Sentido` organiza direção; `PadraoOperacional` é a identidade estável de uma variante; `PadraoVersao` registra uma geometria imutável/publicada; `OcorrenciaParadaPadrao` posiciona uma parada em ordem dentro daquela versão.

Identidades externas desacoplam IDs dos providers. Fonte/importação/hash preservam proveniência. Uma viagem fixa `PadraoVersaoId`, evitando mudar de geometria quando uma nova versão se torna atual. Ocorrência, e não somente parada física, permite repetição e circularidade. O antigo `Itinerario/ParadaItinerario` está excluído e só deve aparecer como histórico.

## Workers dentro da API

O host registra coletor/polling GPS, reporters, outbox, telemetria, retention, ETA V2 e canary ferroviário. Todos competem pelos recursos do mesmo processo/container, compartilham configuração e DI e são encerrados pelo mesmo ciclo do host. Channels e streams isolam etapas e aplicam backpressure, mas não fornecem independência de deploy.

## Frontend: organização

| Área | Responsabilidade |
|---|---|
| `app/` | rotas/telas: mapa, linhas, Rotinas-placeholder e configuração |
| `src/services/api.ts` | wrapper HTTP, timeout, parâmetros e retorno padronizado |
| `estruturaV2.ts` | endpoints V2 e cache de itinerário por versão |
| `gpsHub.ts` | conexão SignalR única, subscribers e grupos por linha |
| `railRealtime.ts` | snapshot ferroviário e cache de geometria |
| `useMobilidadeRio.ts` | orquestra estrutura, inscrições e veículos rodoviários |
| `useRailRealtime.ts` | polling, hidratação e demo condicional |
| `storage.ts`/tema | AsyncStorage e estado compartilhado de preferências |
| `mapOSM/` | fronteira React Native/WebView/MapLibre |
| `mobilidadeRio.ts` | busca V2 e compatibilidades/exports antigos |

O estado é predominantemente local por hooks/context; React Query não participa dos fluxos encontrados. Caches específicos são implementados nos services/hooks.

## Arquitetura cartográfica

`buildMapHtml` monta HTML com MapLibre e estilos; a WebView sinaliza `map_ready`. Antes disso, o host não injeta updates. `mapOSM.tsx` calcula uma assinatura estrutural e conserva cache da estrutura. Mudanças pesadas chamam `window.updateMap`; posições rodoviárias/ferroviárias chamam `window.updateRealtime`; localização do usuário usa `window.updateUser`. Essa divisão evita reenviar geometria a cada posição.

Dentro da WebView, `mapScript.ts` cria mapa, sources/layers/markers, interpola coordenadas e headings, administra popups e retorna cliques/métricas por `postMessage`. A ponte exige serialização JSON e escaping correto; recarga da WebView perde estado interno; CDN/tiles são dependências externas. Marcadores e HTML possuem custo próprio, especialmente em grandes volumes.

## Gerenciamento de dados

Ao selecionar linha, o hook busca sentidos, padrões e versões atuais, carrega geometrias/ocorrências e só então inscreve o código no hub. Estruturas são cacheadas por `PadraoVersaoId`; um veículo que menciona versão desconhecida dispara hidratação deduplicada. Snapshots SignalR são reconciliados, não simplesmente anexados. Ferrovia usa polling de 5 s por padrão, pausa quando o app não está ativo e hidrata geometria separadamente. Preferências e pesquisa ficam no dispositivo.

## Acoplamentos e compatibilidade

O app depende do código da linha para grupos SignalR e de `PadraoVersaoId` para coerência estrutural. A fachada `FrontendLegacyMapaController` ainda atende detalhe/mapa e o DTO SignalR mantém aliases antigos. Remoção unilateral quebra a tela de linhas ou o realtime. POIs são um acoplamento incompleto: call sites existem, controllers ativos não.

## Limitações e aprofundamento

Telas grandes concentram orquestração; o mapa depende de CDN; workers compartilham processo; contratos não estão versionados. Algoritmos GPS/rail serão detalhados na Etapa 2.3; schema/infra na 2.4. Relacionados: [contratos](05-contratos-e-comunicacao.md) e [decisões](06-decisoes-arquiteturais.md).

## Evidências e pendências

`Program.cs`, `.csproj`, diretórios 1–5, `app/`, `src/services`, `src/hooks`, `mapOSM`. Pendente: decompor responsabilidades de telas, testar bridge sob volume e planejar contrato V2 versionado.
