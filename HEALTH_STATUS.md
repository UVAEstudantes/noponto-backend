# Health e fontes de transporte

Implementação aditiva na branch `feat/health-status-fontes`. Sem deploy, alteração de infraestrutura, banco, seleção de provedores ou frontend. `GET /` continua retornando `Hello World!`.

## Endpoints

| Endpoint | HTTP | O que verifica |
|---|---|---|
| `GET /health/live` | 200 | Processo HTTP respondendo. Não resolve serviços de banco/Redis, nem acessa coletores. |
| `GET /health/ready` | 200 ou 503 | PostgreSQL com extensão PostGIS e Redis; ambos essenciais. Cache compartilhado de 5 s. |
| `GET /health/fontes` | 200 | Snapshots em memória de aquisições SPPO/BRT e de ITS/GTFS-RT BUS. Nenhuma chamada externa ou de banco. |

Todos usam camelCase, timestamps UTC (`+00:00` ou `Z`) e `Cache-Control: no-store`. Os resultados de readiness têm cache **interno** curto para limitar consultas, independente do cache HTTP. Não são publicados endpoints privados, credenciais, exceções, IDs de veículos, linhas ou posições.

Liveness:

```json
{"status":"healthy","checkedAt":"2026-11-14T00:30:00+00:00"}
```

Readiness com Redis indisponível (HTTP 503):

```json
{
  "status":"unhealthy",
  "checkedAt":"2026-11-14T00:30:00+00:00",
  "dependencies":{
    "postgres":{"status":"healthy","reason":null},
    "redis":{"status":"unhealthy","reason":"dependency_unavailable"}
  }
}
```

Fontes, exemplo de uma instância com SPPO sem coleta completa e BRT com resposta vazia válida (HTTP 200):

```json
{
  "status":"degraded",
  "checkedAt":"2026-11-14T00:30:00+00:00",
  "sources":{
    "sppo":{
      "status":"unavailable",
      "lastSuccessAt":null,"ageSeconds":null,
      "lastDataAt":null,"dataAgeSeconds":null,
      "lastAttemptAt":"2026-11-14T00:30:00+00:00",
      "consecutiveFailures":3,
      "lastErrorAt":"2026-11-14T00:30:00+00:00",
      "lastError":"collection_failed","reason":"collection_failed","activeSource":null
    },
    "brt":{
      "status":"healthy",
      "lastSuccessAt":"2026-11-14T00:30:00+00:00","ageSeconds":0,
      "lastDataAt":null,"dataAgeSeconds":null,
      "lastAttemptAt":"2026-11-14T00:30:00+00:00",
      "consecutiveFailures":0,"lastErrorAt":null,"lastError":null,
      "reason":null,"activeSource":null
    }
  },
  "providers":{
    "its":{
      "status":"unknown","lastSuccessAt":null,"ageSeconds":null,
      "lastDataAt":null,"dataAgeSeconds":null,"lastAttemptAt":null,
      "consecutiveFailures":0,"lastErrorAt":null,"lastError":null,
      "reason":"no_telemetry","activeSource":null
    },
    "gtfsRt":{
      "status":"unknown","lastSuccessAt":null,"ageSeconds":null,
      "lastDataAt":null,"dataAgeSeconds":null,"lastAttemptAt":null,
      "consecutiveFailures":0,"lastErrorAt":null,"lastError":null,
      "reason":"no_telemetry","activeSource":null
    }
  }
}
```

O exemplo pressupõe warmup concluído. `providers` contém somente os dois canais fixos observados; não é uma lista extensível controlada por requisições.

## Estados e decisões de consumo

Readiness admite `healthy`, `degraded`, `unhealthy`. `unhealthy` retorna HTTP 503, os outros retornam 200. Os dois checks concretos atuais são binários: uma falha essencial é `unhealthy`. O agregador preserva `degraded` caso um check registrado produza esse resultado; atualmente não há dependência opcional para degradar isoladamente.

PostgreSQL é necessário para estrutura, matching e persistência. O check usa o `NpgsqlDataSource` compartilhado e uma consulta pequena em `pg_extension`, confirmando conexão e presença de PostGIS. Não varre tabelas nem verifica permissões de escrita, todas as tabelas, migrations pendentes ou toda função espacial.

Redis é essencial para posições, estado operacional, Radar e controles distribuídos. O check faz `PING` pelo multiplexer existente. Falhar Redis não é tratado como falha opcional de cache. Não há conexão Redis paralela ou escrita de chaves de teste.

Fontes usam:

| Estado | Significado |
|---|---|
| `healthy` | Aquisição completa recente; inclui ausência operacional validamente vazia. |
| `degraded` | Uma falha transitória, resposta parcial, fallback ativo, perda parcial de fornecedor, coleta atrasada ou GPS envelhecido. |
| `unavailable` | Falhas consecutivas com tolerância temporal satisfeita, ou ausência prolongada de tentativas concluídas após já existir telemetria. |
| `unknown` | Warmup sem sucesso, ou canal sem telemetria; não comprova queda. |

Agregado das fontes: ambas saudáveis => `healthy`; ambas indisponíveis => `unavailable`; qualquer degradação/indisponibilidade parcial => `degraded`; sem falha conhecida, mas com canal desconhecido => `unknown`. A falha de um modal não marca o outro como indisponível.

Para o aplicativo: usar `/live` para conectividade dos túneis; usar `/ready` para dependências compartilhadas; usar `sources.sppo` e `sources.brt` para avisos dos respectivos dados. Falha de readiness ou de fornecedor não justifica alternar túneis que chegam ao mesmo backend. Dar prioridade ao problema do backend antes de avisos de fontes. Trens não entram no agregado SPPO/BRT.

## Tolerâncias e configuração

Configuração opcional em `appsettings` na seção `HealthStatus`, ou variáveis ASP.NET Core equivalentes:

```text
HealthStatus__DependencyTimeoutMilliseconds=2000
HealthStatus__ReadinessCacheSeconds=5
HealthStatus__WarmupSeconds=120
HealthStatus__ConsecutiveFailures=3
HealthStatus__Sppo__DegradedAfterSeconds=120
HealthStatus__Sppo__UnavailableAfterSeconds=300
HealthStatus__Brt__DegradedAfterSeconds=120
HealthStatus__Brt__UnavailableAfterSeconds=300
```

Esses são os defaults, não exigem novas variáveis para iniciar. Timeout validado entre 100 e 5000 ms; cache entre 1 e 30 s; warmup entre 0 e 600 s; falhas consecutivas entre 2 e 20; degradação entre 30 e 600 s, indisponibilidade maior que degradação e no máximo 1800 s. Configuração inválida falha na validação de startup.

O checkout usa polling padrão de 20 s, intervalo BRT padrão de 20 s e coletor SPPO de 10 s; o ambiente pode configurar 15 s. ITS permite aquisição de 90 s por padrão, até 180 s configuráveis. Por isso 120/300 s foram escolhidos como tolerâncias conservadoras comuns, ajustáveis separadamente por modal. Ao elevar o orçamento ITS, rever os limites. Não existe suposição de timer de 15 s exato, nem regra por horário da madrugada.

Uma falha isolada pode degradar, mas não torna uma fonte indisponível. Com sucesso anterior, indisponibilidade por falhas exige **3 falhas consecutivas e pelo menos 120 s desde o último sucesso**. Sem sucesso anterior, exige 3 falhas e warmup encerrado. Sem qualquer tentativa observada, continua `unknown`, inclusive após warmup. Uma coleta bem-sucedida zera a sequência de falhas.

Sem novas tentativas concluídas: 120 s desde sucesso => `collection_delayed`; 300 s desde última tentativa => `polling_stalled`. Isso pode significar backpressure/processamento bloqueado, não necessariamente fornecedor externo desligado. GPS antigo com coleta recente causa somente `degraded/stale_data`, nunca prova sozinho uma interrupção: veículos parados/sem operação não devem ser diagnosticados como fornecedor offline.

## Métricas e composição

- `lastSuccessAt` / `ageSeconds`: última aquisição completa observada e sua idade, não idade do GPS. Releitura local de GTFS ou cache do gate BRT não renova esse horário.
- `lastDataAt` / `dataAgeSeconds`: timestamp GPS mais recente do lote completo aceito, representativo da frota observada; não é SLA por veículo, linha ou cobertura da frota inteira. Vazio válido limpa esses campos, evitando alerta de GPS antigo por falta de veículos ativos.
- `lastAttemptAt`: última tentativa **concluída** observada; não é indicação de request atualmente em andamento.
- `consecutiveFailures`: falhas/respostas incompletas consecutivas, não contador histórico cumulativo.
- `lastErrorAt` / `lastError`: último erro sanitizado, mantido mesmo após recuperação para diagnóstico. O aplicativo deve decidir avisos por `status` e `reason`, nunca pela simples existência de `lastError`.
- `activeSource`: `its`, `gtfs_rt`, `hybrid` ou `brt`, inferido somente quando o lote traz observações conhecidas; `null` em vazios, falhas ou fontes sem evidência suficiente. Não identifica obrigatoriamente toda fonte habilitada.

Motivos públicos limitados: `warming_up`, `no_telemetry`, `partial_collection`, `collection_failed`, `collection_timeout`, `collection_delayed`, `polling_stalled`, `stale_data`, `no_valid_data`, `fallback_active`, `primary_unavailable`, `support_unavailable`, `provider_degraded`. Readiness usa `dependency_unavailable` ou `check_not_registered`. Exceções e mensagens externas não são copiadas para o contrato.

`no_valid_records`/`hybrid_no_valid_observations` do pipeline indicam qualidade/ausência de GPS válido, sem comprovar falha de transporte: são expostos como `degraded/no_valid_data`, sem acumular falhas de disponibilidade. Se a união operacional não tem posições, mas um dos fornecedores atesta uma coleta completa vazia saudável, o estado composto permanece degradado em vez de afirmar indisponibilidade total. A semântica operacional/publicação original não é alterada.

SPPO é registrado no `GpsSppoCollectorService` depois da leitura da fonte selecionada, antes do handoff. Snapshot pendente não é contabilizado como uma coleta vazia; lote repetido obtido por nova aquisição real pode atualizar a saúde da coleta, preservando seu timestamp GPS. Os contadores existentes `GpsSppoCollectorMetrics` são preservados, mas eram orientados à aquisição por janela e não cobriam todo o caminho atual de snapshots; o novo estado mínimo cobre ambos sem duplicar polling.

ITS (`providers.its`) registra o resultado completo/partial já validado pelo adaptador. O sucesso vazio requer os estados completos dos fornecedores esperados no envelope ITS. ZIRIX e CONECTA **dentro do ITS** não são consultas diretas separadas e não possuem SLA individual público neste contrato.

GTFS-RT BUS (`providers.gtfsRt`) registra aquisição real do feed e o GPS bruto já parseado. HTTP 304 validado conta como contato bem-sucedido; cache local não conta. É saúde de aquisição: aceitação por crosswalk/modal e seleção operacional continuam pertencendo ao pipeline. No modo híbrido, `sources.sppo` usa o resultado composto existente: ITS/GTFS podem contribuir juntos; perda de apenas um deles resulta em degradação quando a outra fonte mantém dados válidos. No fallback completo explícito, o resultado identifica `fallback_active`, sem alterar regras de seleção/recovery. Um provedor não usado permanece desconhecido; ele não deve gerar alerta por si só.

BRT é registrado na leitura da fonte selecionada **antes** do gate de fail-open, preservando a visibilidade de falhas mesmo quando posições anteriores continuam no cache. GTFS-RT BRT também respeita a indicação de cache local. O monitor mantém as decisões de vazio/filtro já existentes dos adaptadores BRT; não altera normalização, timestamps, TTL, descarte, inscrições SignalR ou publicação.

Não foi introduzido, reativado ou priorizado `ZIRIX_DIRECT`. A seleção operacional e os defaults preexistentes de `GpsSources` não foram modificados. O uso ITS + suporte/fallback GTFS-RT depende da configuração operacional já existente; o health não seleciona fontes.

## Concorrência, custo e limitações

São quatro snapshots de cardinalidade fixa, protegidos por lock curto. Não armazenam histórico de veículos nem payloads. O endpoint lê uma cópia consistente e calcula idades no momento da consulta. Nenhum novo worker, timer, dependência NuGet ou chamada de fornecedor foi adicionado.

Readiness usa os Health Checks do ASP.NET Core, executando os dois checks em paralelo. Requisições concorrentes compartilham a mesma verificação e cache. Cancelamento de um cliente cancela apenas sua espera, sem interromper a consulta compartilhada. PostgreSQL recebe cancelamento e command timeout; `PING` Redis tem espera limitada (o comando subjacente pode terminar depois, sujeito ao timeout do cliente Redis existente). A resolução inicial de serviços e a política de startup existente não foram modificadas.

O startup atual já exige bootstrap/validação de banco e Redis antes de escutar HTTP. Se ele falhar antes de abrir a porta, nenhum endpoint estará acessível; liveness não muda essa política. Esses endpoints distinguem as condições quando o processo está atendendo. Ausência de resposta não prova, por si só, que um túnel específico caiu.

Os snapshots são **por instância**, desaparecem ao reiniciar e não são sincronizados entre réplicas. Em múltiplas réplicas, uma instância sem coletor local retorna `unknown`; cache Redis com posições de outra réplica não inventa sucesso local. Não há detecção independente de serviço desligado antes da primeira tentativa, nem monitoramento de trens neste contrato.

Não há validação de sucesso de conexão PostGIS contra banco real nesta entrega; falha de conexão foi exercitada somente em loopback sem serviço. Não foram feitos testes de falha em produção, consultas aos fornecedores reais, deploy, reinício, migrations ou Docker build.

## Diagnóstico e validação local

Com uma instância **local já configurada e em execução**, sem iniciar serviços de produção:

```powershell
curl.exe -i http://localhost:8080/health/live
curl.exe -i http://localhost:8080/health/ready
curl.exe -i http://localhost:8080/health/fontes
curl.exe -i http://localhost:8080/
```

1. Se `/live` responde e `/ready` retorna 503, observar `dependencies.postgres` e `dependencies.redis`. Consultar logs locais protegidos para a causa técnica; a API pública é sanitizada.
2. Com readiness saudável, ler `sources.sppo` e `sources.brt`. Confirmar idade de coleta, sequência de falhas e razão atual.
3. Para SPPO, comparar `providers.its` e `providers.gtfsRt`, levando em conta modo híbrido/fallback e canais não utilizados. `stale_data` é diferente de falha de aquisição.
4. `polling_stalled` com fornecedores recentes aponta para processamento/handoff, não necessariamente integração externa. Ausência de veículos com coleta vazia válida continua saudável.

Testes isolados (não executam o `Program` da aplicação, migrations, workers ou acesso ao servidor):

```powershell
Set-Location D:\repositorio_github\NoPonto\noponto-backend-health
dotnet test NoPonto/NoPonto.csproj --filter "FullyQualifiedName~TransportHealthTests|FullyQualifiedName~BackendReadinessTests|FullyQualifiedName~HealthEndpointTests|FullyQualifiedName~GpsPollingCadenciaTests.Health|FullyQualifiedName~ItsCurrentGpsSourceTests.Health" --verbosity minimal
dotnet test NoPonto/NoPonto.csproj --no-build --no-restore --filter "FullyQualifiedName~GpsSppoCollectorTests|FullyQualifiedName~GpsPollingCadenciaTests|FullyQualifiedName~ItsCurrentGpsSourceTests|FullyQualifiedName~GpsSourceResolverTests|FullyQualifiedName~GpsDiagnosticSourceCollectorTests" --verbosity minimal
```

O teste HTTP cria um host Kestrel de loopback com dependências simuladas. O teste de PostgreSQL indisponível usa somente loopback na porta 1, não a configuração de banco do projeto. Não é necessário executar a aplicação normal para validar esses testes.

## Resultados executados

Arquivos da implementação:

- `NoPonto/1-API/Controllers/HealthController.cs`.
- `NoPonto/2-Application/Services/Health/`: `HealthResponses.cs`, `HealthStatusOptions.cs`, `TransportSourceHealth.cs`, `BackendReadiness.cs`.
- `NoPonto/4-Data/Health/DependencyHealthChecks.cs`.
- `NoPonto/2-Application/Services/GPS/`: alterados `GpsPollingService.cs`, `GpsSppoCollectorService.cs`, `GpsSourceContracts.cs`, `ItsCurrentGpsSource.cs`, `ItsFallbackGpsSource.cs`, `GtfsRealtimeGpsSource.cs`.
- `NoPonto/Program.cs`, `NoPonto/HISTORICO.md`, `HEALTH_STATUS.md`.
- `NoPonto/5-Testes/`: novos `TransportHealthTests.cs`, `BackendReadinessTests.cs`, `HealthEndpointTests.cs`, `GpsPollingCadenciaTests.Health.cs`, `ItsCurrentGpsSourceTests.Health.cs`; fixtures ajustadas em `GpsPollingCadenciaTests.Gtfs.cs` e `ItsCurrentGpsSourceTests.cs`.

O commit documental `65edf76` contém exclusivamente `AUDITORIA_NGROK_NOPONTO.md` e `AUDITORIA_SIGNALR_NGROK_NOPONTO.md`. Eles foram copiados do checkout original, revisados quanto a conteúdo sensível/padrões de credenciais e commitados antes da implementação. O worktree foi confirmado limpo logo após esse commit. O histórico e os artefatos do checkout original não foram transportados ou alterados.

- 43 testes focados de health/instrumentação: passaram, sem ignorados (cerca de 0,5 s de execução, além da compilação).
- 342 testes de regressão dos módulos GPS selecionados: passaram, sem ignorados (cerca de 7 s). Há sobreposição com testes de instrumentação; os totais não devem ser somados como casos únicos.
- Compilação .NET 9 concluída pelos testes. Avisos existentes em migrations, entry point do test SDK e asserts de outros testes permanecem; nenhum pacote foi adicionado.
- `git diff --check`: sem erros.

| Requisito | Evidência automatizada |
|---|---|
| Liveness sem dependências, readiness 503 e fontes 200 | `HealthEndpointTests.HttpEndpointsSeparateLivenessReadinessAndTransportState` |
| Banco indisponível e payload sem detalhes internos | `BackendReadinessTests.EssentialDependencyFailuresAndDegradationArePreservedWithoutSecrets`; `PostgresProbeHandlesLocalConnectionRefusalWithoutLeakingDetails` |
| Redis indisponível/timeout ou saudável | `RedisProbeReportsUnavailableOnFailureOrTimeout`; `RedisProbeAcceptsSuccessfulPing` |
| Registro essencial ausente e checks externos excluídos | `BuiltInHealthChecksOnlyRunReadinessTagsAndMissingEssentialCheckFailsClosed` |
| Cache, concorrência e cancelamento | `ConcurrentRequestsShareChecksAndCancelledWaiterDoesNotCancelOthers`; `TransportHealthTests.ConcurrentWritersAndCapturesKeepBoundedConsistentSnapshots` |
| SPPO e BRT saudáveis | `TransportHealthTests.SuccessfulSppoAndBrtCollectionsAreHealthy` |
| Falha isolada e recuperação | `IsolatedFailureIsDegradedAndRecoveryClearsFailureStreak` |
| Falhas consecutivas e limite temporal | `UnavailabilityNeedsConsecutiveFailuresAndElapsedTolerance` |
| GPS antigo sem afirmar fornecedor offline | `OldGpsIsDegradedRatherThanProofOfSupplierOutage`; `NoValidGpsAloneDoesNotProveOutageAndCompleteEmptySupportRemainsAvailable` |
| Vazio válido e BRT atual | `ValidEmptyCollectionClearsStaleVehicleAgeAtNight`; `CurrentBrtValidEmptyIsHealthyAndHttpFailureDoesNotRenewSuccess` |
| Warmup e ausência de telemetria | `RepeatedStartupFailuresRemainUnknownDuringWarmup`; `MissingTelemetryStaysUnknown` |
| Polling parado e agregado por modal | `StalledPollingDiffersFromAValidEmptyCollection`; `AggregateDistinguishesOneOrBothUnavailableModes` |
| Híbrido/fallback e respostas parciais | `PartialHybridAvailabilityIsDegraded`; `ExplicitFallbackAndPartialCollectionsAreNotFullSuccess` |
| Handoff não cria sucesso fictício | `CollectorRecordsActualReadsButNotPendingHandoffAsEmptySuccess` |
| Gate BRT e cache GTFS não escondem falhas | `GpsPollingCadenciaTests.HealthBrtFailureIsVisibleEvenWhenOperationalGateReusesPositions`; `HealthBrtLocalCacheDoesNotRenewCollectionTimestamp`; `HealthGtfsProviderDoesNotCountLocalCacheAsANewAcquisition` |
| Semântica real de vazio ITS | `ItsCurrentGpsSourceTests.HealthItsDistinguishesValidEmptyFromIncompleteProviderWarmup` |
| Validação dos limites | `TransportHealthTests.InvalidLimitsAreRejectedAndDefaultsAreValid` |
