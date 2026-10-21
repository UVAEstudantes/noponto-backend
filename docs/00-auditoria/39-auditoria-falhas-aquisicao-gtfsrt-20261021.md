# Continuação: conectividade, corpo parcial e recuperação GTFS-RT

Continuação do relatório 38 sobre a mesma árvore e HEAD `43b6a72776b0e9c1105bf33b5fe73d2e33d3012b`. Não houve reset, stash, commit, push ou descarte do patch anterior. As versões iniciais dos sete arquivos alterados foram preservadas em scratch fora do repositório. Relatório 38, JSON de validação e dois arquivos de testes anteriores continuam byte a byte iguais; o histórico recebe somente acréscimo.

## Evidência externa fornecida pelo usuário

Em 21/10/2026, o intervalo efetivo SPPO foi confirmado como 10s. Uma tentativa BUS recebeu HTTP/2 200, Date `Wed, 21 Oct 2026 18:02:18 GMT`, Cache-Control `public, max-age=30` e nenhum Age; terminou em timeout curl de 15s após apenas 3159/270820 bytes. Outra tentativa terminou com SSL connection timeout, HTTP 000, DNS aproximado de 2,09s e limite de conexão curl de 5s. Entre 17:56–18:03 UTC, os logs apresentados indicaram BUS Falha/count=0 e BRT consulta=Falha/cache_reutilizado=True/acquisition_timeout, com idade do cache chegando aproximadamente a 155s.

Essas medições não foram repetidas. Elas demonstram indisponibilidade/transferência incompleta nos acessos observados, sem identificar por si só DNS, rota, TLS, intermediário ou origem como causa raiz. HTTP 200 nos headers não certifica um feed recebido e validado. HTTP 000 do curl significa ausência de resposta HTTP nessa tentativa, não um status HTTP retornado pelo backend.

O limite de conexão de 5s informado pertence à sonda curl. No código inspecionado, o HttpClient nomeado tem Timeout infinito e a aquisição usa budgets finitos próprios da fonte/cliente, ambos configurados por TimeoutSeconds, padrão 15s. Não foi encontrado ConnectTimeout=5s nessa configuração do backend. Não se pode transferir o limite da sonda para a aplicação sem conferir configuração/imagem efetiva. Nenhum timeout ou configuração operacional foi alterado.

## Conclusões separadas

| Tema | Conclusão |
| --- | --- |
| Amplificação por cache | A correção 38 continua válida. O algoritmo de prazo por início/Date/Age/trânsito/Retry-After permanece literalmente igual. Os testes anteriores continuam passando. Com coleta efetiva a cada 10s após a conclusão, não há promessa de GET fixo em 30s. |
| Timeout DNS/TLS/conexão | Falhas antes dos headers são tratadas como indisponibilidade/cancelamento. Exceções de transporte foram injetadas, sem rede real. Não foi reproduzido um handshake TLS verdadeiro nem diagnosticada a causa externa. |
| Download parcial | A leitura é integral e limitada antes do parsing. Um stream com prefixo protobuf válido e Content-Length maior falha depois do prefixo; não há snapshot, recepção renovada nem promoção parcial. Timeout externo após começar o corpo expôs o bug de estado descrito abaixo. |
| Cache na indisponibilidade | BUS deve devolver Falha sem lote novo. BRT conserva seu último estado conhecido, com ResultadoConsulta=Falha, CacheReutilizado=true e timestamps originais. ResultadoEfetivo=Sucesso nesse fallback não significa aquisição bem-sucedida. |
| Recuperação | Corpo descartado não pode continuar tendo validadores ativos. O próximo GET elegível precisa obter representação completa; 304 normal continua válido quando seu corpo está retido. |
| Logs | O timeout externo da fonte não passava pelo log de ReadCore. Agora possui warning explícito. O log de aquisição informa também success/failure para diferenciar LocalCache de sucesso e LocalCache de falha. |

## Bugs reproduzidos antes dos novos ajustes

As hipóteses foram apresentadas antes dos testes. A primeira execução RED teve 0/6; a segunda, ampliada com startup e log, teve 0/8, todas falhas esperadas. O comando e os TRXs estão registrados abaixo e no JSON associado.

1. **Cancelamento externo conservava sucesso anterior.** A fonte tem budget próprio e passa seu token ao cliente. Quando esse budget cancela conexão/leitura, o cliente identifica o evento como cancelamento do chamador e relança sem substituir `_last`. A fonte devolve acquisition_timeout, mas uma consulta seguinte antes de `_nextRequest` retorna o snapshot anterior como Sucesso/LocalCache. No BRT, esse falso sucesso pode ainda zerar IdadeCache no gate. No BUS, pode republicar a posição antiga em outra geração.

   Reprodução: sucesso em t=0, request bloqueado em t=30, timeout da fonte, consulta em t=40 retornava Sucesso com corpo de t=0 e sem HTTP novo. Na primeira tentativa sem sucesso anterior, `_last` continuava null; a consulta em t=10 podia escapar do intervalo HTTP de 30s. É a mesma ausência de registro de falha, com dois estados iniciais distintos.

2. **Validadores órfãos impediam recuperação.** HTTP 503, falha de transporte, timeout interno ou corpo incompleto substituíam `_last` por falha sem corpo, mas preservavam ETag/Last-Modified. O próximo GET enviava esses validadores; com representação inalterada, uma resposta legítima 304 não podia ser aproveitada. A aplicação retornava http_304 e podia repetir o ciclo enquanto o feed permanecesse igual.

   Reprodução: 200 em t=0; falha em t=30; em t=60 o provedor falso responde 304 se receber qualquer um dos validadores anteriores. Os quatro cenários RED terminaram em http_304. A variante de timeout de conexão foi acrescentada depois como controle adicional do mesmo caminho de cancelamento interno.

3. **Lacuna diagnóstica.** Timeout externo saía antes do log de aquisição de ReadCore. A fonte retornava acquisition_timeout sem warning próprio. Um teste RED verificou a ausência de qualquer entrada de log nesse caminho. Essa lacuna não prova a fase de rede em que expirou o budget: ele também abrange espera pelo gate e crosswalk.

Os dois bugs de estado já existiam antes do patch 38: seu código anterior também relançava cancelamento externo sem invalidar resultado e mantinha validadores após falhas. Não são atribuídos ao novo cálculo de cadência nem certificados como causa de cada amostra produtiva.

## Ajuste mínimo sobre o patch anterior

Somente os mesmos dois arquivos funcionais recebem novos deltas:

- `GtfsRealtimeGps.cs`: RecordFailure registra falha sem corpo/ReceivedAtUtc e limpa ETag/Last-Modified. HTTP não exitoso, exceções de conteúdo/transporte e timeout interno usam esse caminho. Cancelamento do chamador durante aquisição também registra falha antes de relançar. Cancelamento enquanto aguarda o semáforo continua fora desse catch e não invalida outra aquisição em andamento.
- O prazo HTTP/cache/Retry-After já calculado não é alterado por RecordFailure. Não há retry extra. Como não se retém um segundo corpo, a recuperação usa GET sem validadores órfãos no próximo prazo permitido. Esse GET pode transferir mais bytes do que um 304; é o custo de recuperar a representação descartada sem introduzir outra política de retenção.
- `GtfsRealtimeGpsSource.cs`: warning agregado para acquisition_timeout externo; log existente de aquisição acrescenta success e failure. Não há ETag, URL, payload, coordenada, linha ou identidade de veículo no novo diagnóstico. O enum continua com quatro valores; os motivos são códigos controlados.

Nenhuma modificação em timeouts, opções, políticas no-cache/no-store, cálculo de validade, parser, timestamps físicos, coleta de 10s, gate BRT, handoff/ACK, matching, CAS, publicação, flags, infraestrutura ou frontend. Um timeout posterior no crosswalk não invalida um HTTP completo: o teste correspondente confirma reutilização legítima, identificada como LocalCache, sem fingir outro download.

## Testes e limites da simulação

29 casos novos em `GpsPollingCadenciaTests.Outages.cs`, na classe já pertencente à allowlist offline. Clock e handler controlam horários/respostas; streams controlam prefixo, bloqueio e exceções. Para isolar a corrida entre budgets da fonte e do cliente, somente a fixture reduz o budget da fonte para 1s e mantém o cliente no padrão 15s. Esses testes exercitam cancelamento real por token, sem exigir uma espera real de 15s ou adicionar abstração em produção. A validação não mede desempenho de TLS/rede.

O prefixo da resposta parcial é propositalmente um protobuf completo válido, seguido de erro de transporte e Content-Length=270820. Isso comprova que até um prefixo parseável não é promovido antes de completar o stream. O teste não faz uma transferência real de 270820 bytes nem afirma reproduzir os bytes específicos da sonda curl.

| Requirement | Evidence |
| --- | --- |
| Timeout antes dos headers e após corpo parcial não virar sucesso cacheado | `Gtfs_SourceTimeoutCannotBecomeCachedSuccess` (2 casos) |
| Startup cancelado respeita intervalo; timeout aparece no log | `Gtfs_StartupSourceTimeoutDoesNotBypassHttpInterval`, `Gtfs_OuterTimeoutIsLoggedWithoutNewSnapshot` |
| Recuperação de falhas sem ETag/Last-Modified órfãos | `Gtfs_FailureClearsValidatorsBeforeRecovery` (TLS, conexão expirada, corpo com IOException/timeout e HTTP 503; 5 casos) |
| BUS/BRT durante falhas consecutivas, idade de cache até 155s e recuperação | `BusBrt_ConsecutiveTransportFailuresKeepStatusAndPhysicalTime` (6 casos): GETs 0/30/60/90/120/150/180; BUS não publica lote; BRT informa falha/fallback com tempo físico original |
| Indisponibilidade desde startup, sem inventar fallback | `BusBrt_StartupOutageHasNoInventedFallbackAndRecovers` (8 casos, BUS/BRT) |
| Cancelamento ativo e em espera preserva propagação/serialização | `Gtfs_CallerCancellationPropagatesAndReleasesGate` |
| Logs de sucesso/falha cacheados com campos limitados | `Gtfs_AcquisitionLogsSeparateCachedFailureAndCachedSuccess` |
| HTTP 200 recuperado pode manter o mesmo GPS, sem movimento fabricado | `Gtfs_Recovered200WithSameGpsRemainsDuplicate`: GPS e coordenadas iguais; ReceivedAtUtc avança apenas pela nova aquisição; dedup continua ignorando |
| Timeout downstream não destrói corpo HTTP completo | `Gtfs_CrosswalkTimeoutPreservesCompletedHttpSnapshot` |
| Integração do timeout externo com gate BRT e handoff BUS | `Brt_OuterTimeoutKeepsFallbackAgeIncreasingOnFollowingRead`, `Bus_OuterTimeoutDoesNotRepublishPreviousGeneration` |
| Cadência/304/cache/limites anteriores e publicação preservados | Seleção dirigida completa e allowlist oficial; inclui `Gtfs_304RetainsOrUpdatesCachePolicyWithoutRenewingGps`, `Bus_CollectorDoesNotMissExpiredHttpSnapshotWithVariableLatency`, `Gtfs_RepeatedGpsInRealPollingNeverReachesMatchingOrCas` e testes existentes de SignalR/validação |

Red completo e green principal:

```powershell
dotnet test NoPonto\NoPonto.csproj --no-restore -c Release --filter 'FullyQualifiedName~Gtfs_SourceTimeoutCannotBecomeCachedSuccess|FullyQualifiedName~Gtfs_FailureClearsValidatorsBeforeRecovery|FullyQualifiedName~Gtfs_StartupSourceTimeoutDoesNotBypassHttpInterval|FullyQualifiedName~Gtfs_OuterTimeoutIsLoggedWithoutNewSnapshot' --logger 'trx;LogFileName=outage-red-full.trx' --results-directory NoPonto/lab-output/gps-outage-20261021
```

Antes: Passed: 0, Failed: 8, Skipped: 0. Depois, com LogFileName=outage-green.trx: Passed: 8, Failed: 0, Skipped: 0. O caso extra de conexão expirada foi acrescentado posteriormente, sem reescrever os TRXs.

Rodada ampliada intermediária: Passed: 26, Failed: 1, Skipped: 0, em outage-expanded.trx. A asserção da fixture usava o rótulo textual acquisition_duration_ms como se fosse a chave estruturada; o template usa a chave duration. Corrigido somente o teste, sem enfraquecer comparação de campos nem mudar log produtivo. Acrescentados depois os dois testes de gate/handoff.

Seleção dirigida final com compilação Release:

```powershell
dotnet test NoPonto\NoPonto.csproj --no-restore -c Release --filter 'FullyQualifiedName~NoPonto.Tests.GpsPollingCadenciaTests.|FullyQualifiedName~NoPonto.Tests.GtfsRealtimeGpsTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoCollectorTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoClientTests.|FullyQualifiedName~NoPonto.Tests.GpsPollingFontesTests.' --logger 'trx;LogFileName=outage-directed-final.trx' --results-directory NoPonto/lab-output/gps-outage-20261021
```

Passed: 201, Failed: 0, Skipped: 0. Sem erros de compilação e com cinco avisos anteriores. A primeira montagem RED teve um xUnit2031 novo no teste, corrigido antes das rodadas seguintes com a sobrecarga de Assert.Single(predicate).

Seleção offline oficial:

```powershell
$offlineFilter = python .ci/offline_tests.py filter
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test NoPonto\NoPonto.csproj --no-restore --no-build -c Release --filter $offlineFilter --logger 'trx;LogFileName=outage-offline-final.trx' --results-directory NoPonto/lab-output/gps-outage-20261021
python .ci/offline_tests.py verify NoPonto/lab-output/gps-outage-20261021/outage-offline-final.trx
```

Passed: 810, Failed: 0, Skipped: 0. Os conjuntos se sobrepõem e não são somados. Verificação de TRXs/allowlist, hashes e preservação em [gtfsrt-outage-validation-20261021.json](gtfsrt-outage-validation-20261021.json). Build independente da solução não foi repetido; sua rejeição automática de aprovação na etapa anterior permanece registrada no relatório 38. A compilação do projeto pelos testes passou.

## Leitura operacional e prontidão

O cliente ainda pode devolver Success=true para um corpo reutilizado válido ou 304 validado. A fonte pode promover essas observações como Sucesso sem HTTP novo. Isso é distinto do falso sucesso após uma aquisição cancelada, agora corrigido. No fallback BRT, ResultadoEfetivo pode ser Sucesso enquanto ResultadoConsulta é Falha e CacheReutilizado é true; não se deve apagar essa distinção ao interpretar logs.

IdadeCache BRT mede o tempo desde a última aceitação pelo gate, que pode ter recebido cache HTTP; não é idade física do GPS nem instante do último GET. A classificação de aquisição da fonte identifica o GET real. O campo ConsultaHttpReal do gate continua contando consulta ao adaptador, não necessariamente GET no socket, conforme auditoria 24. O filtro de idade física e dedup/CAS do polling continuam protegendo o pipeline. Não se renovam TimestampGps/ReceivedAtUtc por fallback ou erro; 304 conserva ambos. SignalR pode publicar estado conforme assinaturas, sem que isso constitua aquisição nova ou GPS aceito.

O patch está pronto para revisão técnica de um futuro deploy: regressões locais passaram e não há bloqueio de código conhecido nesta seleção. Permanecem pendentes de autorização/ambiente a homologação externa e a verificação do comportamento da imagem implantada. O patch não resolve nem comprova resolução de DNS lento, handshake TLS ou vazão insuficiente; tais falhas ainda podem impedir dados novos. Também não certifica mediana da API, carga de frota, comportamento do provedor real/HTTP2, réplicas ou WebSocket real. Não há evidência que justifique aumentar timeout ou alterar cache.

Nenhuma chamada a produção, fonte GPS externa, containers, Docker, Redis, PostgreSQL, migrations, frontend ou infraestrutura. Sem alteração operacional, deploy, commit ou push. Estado de rollback e artefatos anteriores preservados.
