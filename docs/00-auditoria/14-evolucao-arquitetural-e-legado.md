# 14 — Evolução arquitetural e legado

**Etapa:** 1.5 · **Data:** 2026-10-06  
**Baseline:** backend `53567bdfe3dad87bf767c77b9c3f69cf67a7a73d`; frontend `c69a92ea161e749b77842d35f82e14a86cbfc66e`; ML `08c493baddb83db37c935dc459ca4d7a93aeab2d`  
**Evidência:** código e documentação local, confrontados com produção read-only  
**Escopo excluído:** `noponto-adm`

## 1. Resumo da evolução

O NoPonto evoluiu de um modelo centrado em `Linha → Sentido → Itinerario → ParadaItinerario` e importadores ArcGIS agendados para uma arquitetura estrutural V2 versionada:

```text
Modal → Linha → Sentido → PadraoOperacional
                           └─ PadraoVersao (imutável, geometria)
                              └─ OcorrenciaParadaPadrao → Parada
```

Identidades externas e importações estruturais passaram a preservar proveniência, reconciliação e versionamento. O realtime GPS foi desacoplado dos providers, passou a fixar a versão do padrão durante a viagem e a operar com matching batch, estado causal e observações shadow. O frontend já consome essa estrutura V2, mas conserva uma fachada HTTP de compatibilidade e alguns services antigos. ETA/ML ainda está no ponto de transição mais evidente: a inferência HTTP antiga permanece no fluxo de apresentação, enquanto a fundação ETA V2 existe em shadow, mas está desativada em produção.

Essa evolução é evidência direta do `.csproj`, migrations e call sites. As motivações descritas no roadmap/contexto são documentação histórica; só foram adotadas quando confirmadas pelo código.

## 2. Arquitetura backend vigente

### Composição efetiva

O projeto .NET 9 compila controllers estruturais V2, leitura de veículos, eventos de parada, tarifas, modais e ferrovia. `Program.cs` registra PostGIS/Npgsql, Redis, providers GPS, pipeline operacional, estrutura V2, schedule/runtime ferroviário, telemetria, ETA V2, SignalR e workers. Registro em DI não implica ativação: ETA V2 e vários módulos ferroviários possuem gates internos; seus estados de produção estão em [15-reconciliacao-producao-e-contexto.md](15-reconciliacao-producao-e-contexto.md).

Controllers vigentes:

- `EstruturaLinhasController`, `EstruturaSentidosController`, `EstruturaParadasController`;
- `PadroesVersoesController`, `SentidosController`, `ModaisController`;
- `VeiculosController`, `EventosParadaController`, `RailVehiclesController`;
- `TarifasController`;
- `FrontendLegacyMapaController`, compilado e registrado por convenção de controllers.

Controllers antigos de linha/itinerário/parada/POI/relacionamento e todos os controllers admin são excluídos em `NoPonto.csproj:36-41`. O admin não integra a arquitetura-alvo.

### Pipeline rodoviário atual

O caminho efetivo é:

```text
Zirix/BRT current (primários) + Data.Rio (shadow)
  → GpsSppoCollectorService / GpsPollingService
  → validação, deduplicação e correção temporal
  → snapshot da viagem + matching V2 PostGIS/batch
  → PadraoVersao pinada, progresso e próxima ocorrência
  → ETA legado opcional + aceite CAS no Redis
  → ViagemOperacional, ETA V2 shadow e telemetria
  → índices/snapshots Redis
  → SignalR PosicaoAtualizada + REST
```

Evidências: `GpsSppoCollectorService.ExecuteAsync/ColetarUmaVezAsync`; `GpsPollingService.ProcessarCicloAsync`; `GpsEnriquecimentoService.EnriquecerCoreAsync`; `GpsItinerarioRepository.*`; `PosicaoVeiculoCacheRepository.TentarAtualizarAsync`; `ViagemObservadaService.AtualizarAsync`.

O aceite Redis por timestamp é a barreira operacional: apenas posições aceitas avançam para viagem, telemetria e broadcast (`GpsPollingService.cs:452-501,1087-1138`). O broadcast usa o DTO de compatibilidade `FrontendLegacyPosicaoSignalRDto`, explicitamente output-only (`GpsPollingService.cs:623-628`).

## 3. Arquitetura frontend vigente

O app Expo 55/React Native 0.83.6 utiliza quatro rotas: mapa, linhas, rotinas e configuração. A tela chamada `favoritos.tsx` é, de fato, um placeholder textual de **Rotinas**; não existe implementação de favoritos/rotinas nessa rota.

### Fluxo estrutural

`estruturaV2.ts` chama `/linhas`, `/linhas/{codigo}/sentidos`, `/sentidos/{id}/padroes` e `/padroes-versoes/{id}/itinerario`. `useMobilidadeRio.garantirItinerario` monta segmentos e ocorrências por `PadraoVersaoId`, cacheia por versão e só então inscreve a linha no SignalR (`useMobilidadeRio.ts:139-250`). Veículos que chegam com versão ainda não carregada disparam hidratação estrutural deduplicada (`:107-130`).

### Fluxo realtime

`gpsHub.ts` mantém uma conexão SignalR compartilhada e publica snapshots aos subscribers. `useMobilidadeRio` reconcilia snapshots e filtra por versões visíveis. O REST `/veiculos/linha/{codigo}` existe como service, mas não é o caminho normal do hook atual.

Ferrovia usa polling, não SignalR: `useRailRealtime` consulta `/rail/vehicles/snapshot`, hidrata geometria via `/veiculos/padrao-versao/{id}/geometria` e repete conforme `RAIL_POLL_INTERVAL_MS`. Se não há veículos e `RAIL_DEMO` estiver ativo, gera um trem visual fictício; é **EXPERIMENTAL**, condicionado à configuração do app (`useRailRealtime.ts:18-99,118-145`).

### Mapa

O mapa vigente é MapLibre GL 3.6.1 carregado em HTML remoto dentro de `react-native-webview`. `mapOSM.tsx` só injeta comandos após `map_ready`; `mapScript.ts` administra fontes, rotas, paradas, POIs, marcadores, animação e mensagens de retorno. Atualizações estruturais e realtime são comandos separados. A dependência `react-native-maps` não possui imports encontrados e é candidata à remoção após build de confirmação.

### Compatibilidade e gaps

`mobilidadeRio.ts` conserva funções antigas. Busca e listagem já delegam a V2. `buscarDetalhesLinha` ainda é chamado pela tela de linhas e depende da fachada backend `FrontendLegacyMapaController`: trata-se de **LEGADO_ATIVO** de contrato, não de modelo legado no banco. `buscarPoisPorItinerario` e `buscarPoisPorParada` também são chamados pela tela, mas seus controllers estão excluídos e os endpoints não aparecem no Swagger de produção; hoje degradam para lista vazia. Outros exports antigos (`buscarMapaPorLinha`, `buscarMapaPorItinerario`, `buscarProximosVeiculosParada`, leitura REST de veículos) não possuem call sites de tela atuais.

## 4. Evolução da estrutura V2

| Aspecto | Implementação anterior | V2 vigente |
|---|---|---|
| Itinerário | entidade `Itinerario` mutável | `PadraoOperacional` estável + `PadraoVersao` imutável |
| Parada na rota | `ParadaItinerario` | `OcorrenciaParadaPadrao`, ordenada e apta a repetição/volta |
| Origem externa | IDs acoplados | tabelas `*IdentidadesExternas` por fonte |
| Importação | jobs ArcGIS diretos | fonte/importação/hash/relatório e publicação versionada |
| Viagem | associação corrente | `PadraoVersaoId` pinado durante a viagem |
| Frontend | DTOs de itinerário | contrato V2 por padrão/versão, com fachada de compatibilidade residual |

O modelo para diagramas do TCC deve ser o V2. `Itinerario` e `ParadaItinerario` não devem aparecer como núcleo vigente; podem constar apenas em uma nota histórica de migração.

## 5. Evolução do realtime GPS

A arquitetura antiga descrita no README — SPPO único, matching em `Itinerario`, cache e broadcast — foi ampliada por:

- abstração `IGpsSource`, primários por modal e fontes shadow;
- coletor SPPO por janela com snapshot/ACK, overlap e catch-up;
- matching V2 set-based/combinado contra padrões versionados;
- viagem observada durável, histerese e versão pinada;
- próxima **ocorrência**, inclusive circularidade;
- CAS Redis por timestamp e separação de rejeição causal/falha de infraestrutura;
- correção temporal e pipeline shadow de posição;
- hints estruturais GTFS em shadow;
- telemetria ML bounded/stream/persistência/retention;
- ETA V2 fail-open desacoplado do aceite GPS.

Em produção, o batch matching, o combinado set-based, a correção temporal e o shadow de posição estão habilitados. Data.Rio é shadow para ônibus e BRT, não fallback automático comprovado: o primário permanece Zirix direto para ônibus e BRT current para BRT.

## 6. Evolução ferroviária relevante

O código antigo em `2-Application/Trem/**` e `2-Application/Services/Trem/**` está excluído. A arquitetura atual combina:

1. schedules versionados (`RailScheduleVersion/Pattern/Run/Stop`);
2. expected runs e cache;
3. schedule gate e planejamento de probes;
4. scanner adaptativo, sentinelas, request budget e single-flight;
5. normalização de evidências externas e binding;
6. tracker e observação cross-sentinel;
7. preditor temporal/estimador espacial;
8. publicação schedule-first e snapshot REST.

O documento histórico `NoPonto/docs/rail-schedule-architecture.md` descreve essa direção, mas o estado atual deve ser obtido do código e flags. Em produção, todos os gates principais consultados estavam ativos, inclusive schedule-first e posição estimada.

## 7. Transição ETA/ML V2

### Arquitetura anterior — LEGADO_ATIVO

`GpsEtaClient` é compilado, registrado como HttpClient e chamado a cada ciclo após matching (`GpsPollingService.cs:408-438`). Ele envia lotes elegíveis a `/eta/batch`, injeta `EtaProximaParadaSegundos/EtaConfianca` no DTO e entra em cooldown de 30 s quando o serviço falha. O container antigo está desligado intencionalmente; portanto o cliente hoje retorna vazio após falha e o restante do GPS segue normalmente.

O modelo antigo não é semanticamente adequado ao ETA V2: o treino prevê tempo entre passagens anteriores, enquanto a API interpreta como tempo restante até a próxima parada; distância de treino e distância enviada também divergem. Essa constatação vem de `ml/lab-output/eta-ml-reconciliation-audit.md` e foi confirmada pelos contratos. O serviço é FastAPI/Uvicorn, não Flask.

### Componentes de transição — EM_MIGRACAO/EXPERIMENTAL

- telemetria V2 e viagem operacional fornecem dados com `PadraoVersaoId`/ocorrência;
- `EtaV2ShadowService` recebe somente posições aceitas e viagem consistente;
- `EtaV2Canary` faz seleção determinística por veículo;
- `EtaV2LongitudinalSpeedV0` calcula baseline `distância longitudinal / velocidade`;
- `EtaV2Channel` bounded isola o hot path;
- batch worker persiste, maintenance expira pendências e eventos de passagem resolvem ETA real/erro;
- `PrevisoesEtaV2` guarda preditor/versão, contexto e observação posterior.

### Estado real

O pipeline está implementado e registrado, mas `EtaV2.Enabled=false`, `ShadowEnabled=false` e canary 0 em produção; `PrevisoesEtaV2` contém zero linhas. Logo, ETA V2 é **EM_MIGRACAO**, não operacional. Para promovê-lo: habilitar shadow controladamente, verificar geração/resolução de previsões, medir baseline no mesmo conjunto, adaptar dataset/treino ao schema V2, definir gates e só depois substituir o campo exibido. ML supervisionado V2 permanece **PLANEJADO**.

## 8. Mapa de componentes significativos

| Componente | Classe | Código | Compila | DI | Call site/flag | Produção | Evidência |
|---|---|---:|---:|---:|---|---|---|
| Estrutura V2 | ATUAL | sim | sim | sim | controllers/frontend | ativa | Swagger + contagens |
| GPS polling/coletor | ATUAL | sim | sim | hosted | sempre; opções de cadência | ativo | DI + dados operacionais |
| Matching V2 batch | ATUAL | sim | sim | sim | flags batch/combinado | ambos ON | env não sensível |
| Correção temporal | ATUAL | sim | sim | sim | `PositionCorrection.Enabled` | ON | env + call site |
| SignalR + DTO compat | LEGADO_ATIVO | sim | sim | sim | assinaturas por linha | ativo | frontend + código |
| Telemetria ML | ATUAL | sim | sim | hosted | sampling | ON | env + tabela populada |
| ETA HTTP antigo | LEGADO_ATIVO | sim | sim | HttpClient | chamado; destino desligado | degrada fail-open | call site + container |
| ETA V2 | EM_MIGRACAO | sim | sim | hosted/services | enabled/shadow/canary | OFF | flags + tabela vazia |
| Ferrovia schedule-first | ATUAL | sim | sim | sim/hosted | várias flags | ON | endpoint + flags + dados |
| Modelo Itinerario antigo | LEGADO_INATIVO | sim | não | não | sem fluxo atual | não | `.csproj:68-69` |
| Importadores/POI antigos | LEGADO_INATIVO | sim | não | não | configurações residuais | não | `.csproj:56-65` |
| Fachada mapa antiga sobre V2 | LEGADO_ATIVO | sim | sim | scoped | frontend detalhe | ativa | controller/service/Swagger |
| Rail demo frontend | EXPERIMENTAL | sim | N/A | N/A | `RAIL_DEMO` | app não verificado | hook |
| Rotinas/favoritos | PLANEJADO | placeholder | sim | N/A | rota visual | não funcional | `favoritos.tsx` |
| Painel admin | FORA_DO_ESCOPO | separado | N/A | N/A | nenhum | não verificado | decisão do desenvolvedor |

## 9. Inventário do legado e remoção futura

### Legado ativo

| Candidato | Dependências/uso | Risco de remoção | Pré-requisito |
|---|---|---|---|
| `GpsEtaClient` + `ML__ETA__BASE_URL` | hot path GPS e campos ETA do DTO | médio: perda do contrato/telemetria de ETA, mesmo com serviço off | ETA V2 validado e promovido; remover chamada/campos de forma versionada |
| `FrontendLegacyMapaController/Service/DTOs` | tela de linha usa detalhes; endpoints de mapa ainda disponíveis | alto para frontend atual | migrar detalhe para contrato V2 e provar ausência de clientes antigos |
| `FrontendLegacyPosicaoSignalRDto` | payload que `gpsHub.ts` adapta | alto | contrato realtime V2 versionado e frontend publicado |
| nomes `itinerarioId` no frontend | alguns IDs agora são `PadraoVersaoId` | médio | migrar tipos, persistência local e rotas sem quebrar dados salvos |

### Legado inativo/candidatos

Arquivos explicitamente removidos da compilação (controllers antigos, serviços/repositórios de itinerário/linha/parada/POI, `Itinerario`, `ParadaItinerario`, trem antigo, importadores/jobs antigos) são candidatos fortes, mas a remoção deve considerar migrations históricas, testes/documentos, scripts externos e qualquer reflexão — não identificada nesta auditoria. Manter migrations aplicadas mesmo após remover entidades antigas.

No frontend, exports sem call site em `mobilidadeRio.ts`, dependência `react-native-maps` e tipos/DTOs antigos são candidatos de risco baixo a médio; validar build, bundle e persistência local antes de remover. Os métodos POI têm call sites, portanto não são “mortos”: são uma integração quebrada/adiada.

## 10. Configurações legadas

| Configuração | Classificação | Motivo | Risco de remover agora |
|---|---|---|---|
| `TREM__HABILITADO/HORARIO_*` | LEGADO_INATIVO provável | só Compose; consumidores em trem excluído | baixo, mas confirmar scripts externos |
| `SUPERVIA__STATIONS_URL`, `SUPERVIA__API__*` | LEGADO_INATIVO provável | só Compose/implementação antiga; runtime novo usa `TremRealtime` | médio: confirmar provider/client atual |
| `IMPORTACAO_HORA/MINUTO` | LEGADO_INATIVO | único consumidor está excluído | baixo |
| `POI__DISTANCIA_MAXIMA_METROS` | LEGADO_INATIVO no backend atual | consumidor POI excluído | médio devido a UI ainda chamar POI |
| `RELACIONAMENTO__*` antigo | LEGADO_INATIVO parcial | serviços antigos excluídos; variante BRT ativa pode ter uso próprio | médio; tratar `RELACIONAMENTO__BRT__*` separadamente |
| `ARCGIS__ITINERARIOS/PARADAS__*` | EM_MIGRACAO/indeterminado | consumidores diretos antigos excluídos; serviços V2 ArcGIS ainda registrados | alto sem auditoria por chave |
| `ML__ETA__BASE_URL` | LEGADO_ATIVO | `GpsEtaClient` registrado e chamado | alto até substituir call site |
| client `ml-admin` | LEGADO_INATIVO | único consumidor admin excluído | baixo |

Nenhuma configuração deve ser removida nesta etapa.

## 11. Arquitetura-alvo confirmada

A documentação do TCC deve usar: estrutura V2 versionada; GPS multifuente com matching V2, estado causal, viagem operacional e Redis/SignalR; ferrovia schedule-first com evidências e posição estimada; frontend MapLibre/WebView alimentado por estrutura V2 e realtime separado; ETA V2 como arquitetura em evolução, inicialmente baseline/shadow, depois modelo supervisionado V2. O painel admin e o modelo `Itinerario` antigo ficam fora da arquitetura-alvo.

## 12. Pendências

- Migrar/remover a fachada de detalhe/mapa apenas após atualizar o frontend.
- Decidir o destino da UI de POIs atualmente sem endpoint ativo.
- Habilitar ETA V2 shadow com observabilidade e janela aprovada.
- Versionar explicitamente o contrato SignalR V2.
- Confirmar flags de build do frontend (`RAIL_DEMO`) e publicação mobile.
- Mapear chaves ArcGIS/BRT uma a uma antes da limpeza do Compose.
- Separar testes do projeto web e executar em ambiente isolado.

## 13. Documentação histórica confrontada

- `README.md`: retrata majoritariamente arquitetura antiga; não é fonte do estado vigente.
- `ROADMAP_NOPONTO.md`: descreve V2 e ETA por etapas; vários estados ficaram desatualizados, especialmente trem, que já está implantado.
- `CONTEXTO_NOPONTO.md`: registro detalhado de decisões/auditorias anteriores; útil para história, mas contém fotografia de branch/worktree antiga.
- `IDEIAS_FUTURAS_NOPONTO.md`: possibilidades, não requisitos entregues.
- `NoPonto/docs/rail-schedule-architecture.md`: arquitetura ferroviária histórica próxima ao código atual.
- `ShadowPosicaoBenchmarkC6.md`: evidência experimental isolada no commit/ambiente informado, não capacidade produtiva.
- frontend `README.md`: scaffold desatualizado (versões e estrutura divergem do `package.json`/código).
- ML `eta-ml-reconciliation-audit.md`: diagnóstico tecnicamente consistente com contratos atuais; não contém validação operacional de produção.

## 14. Referências principais

`NoPonto.csproj:34-81`; `Program.cs:175-653`; `GpsPollingService.cs:163-650,1012-1177`; `GpsEnriquecimentoService.cs`; `GpsEtaClient.cs`; `EtaV2Shadow.cs`; workers/repositório ETA V2; `DbContext.cs`; controllers V2 e compatibilidade; frontend `estruturaV2.ts`, `useMobilidadeRio.ts`, `gpsHub.ts`, `useRailRealtime.ts`, `mapOSM.tsx`, `mapScript.ts`, `mobilidadeRio.ts`; documentação listada acima.
