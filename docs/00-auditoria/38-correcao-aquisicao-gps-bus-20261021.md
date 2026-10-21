# Aquisição GTFS-RT BUS: cache, latência e oportunidades perdidas

Base local: `43b6a72776b0e9c1105bf33b5fe73d2e33d3012b`, árvore inicial limpa. Consultados AGENTS.md, HISTORICO.md, auditoria 24 e regressões da correção BRT de 19/10. Nenhum acesso à produção ou fonte GPS externa foi feito nesta tarefa. A evidência produtiva abaixo foi fornecida pelo usuário, não medida novamente.

## Causa reproduzida antes da correção

O cliente já limitava GETs a `início + IntervalSeconds`, mas, ao receber `max-age` positivo, substituía esse prazo por `recepção dos headers + max(max-age − Age, IntervalSeconds)`. Com intervalo/cache de 30s e headers após 1s, o próximo GET ficava bloqueado até 31s, inclusive com `Age=10`. Uma oportunidade em 30s reutilizava o corpo e era perdida.

O teste usa cliente HTTP, parser, fonte, mapper, coletor BUS e store reais, handler HTTP falso e TimeProvider controlado. Cada lote recebe ACK antes da próxima leitura; não há espera por vaga nem serviço externo. Os horários das chamadas são impostos para isolar o cliente, sem afirmar que sejam a configuração efetiva do coletor em produção. Latências alternam 1, 9 e 3s.

| Oportunidades de coleta | Antes: inícios dos GETs | Depois: inícios dos GETs |
| --- | --- | --- |
| 0, 30, 60, 90, 120s | 0, 60, 120s | 0, 30, 60, 90, 120s |
| 0, 15, 30, ..., 120s | 0, 45, 90s | 0, 30, 60, 90, 120s |

Ambos os casos foram reproduzidos com `Age=0` e `Age=10`. O controle sem Cache-Control adquiriu em 30s mesmo com latência. Duas execuções RED: 1 aprovado / 6 falhas / 0 ignorados; a segunda registra as sequências completas. Após a correção, a mesma regressão principal passou 7/7.

Hipóteses analisadas: (1) prazo HTTP deslocado pela latência, confirmado; (2) espera após conclusão do coletor, existente e caracterizada separadamente; (3) backpressure do handoff, excluído na reprodução por ACK imediato, mas preservado em regressões; (4) dados repetidos/304 confundidos com aquisição nova, explicitados no diagnóstico. A reprodução local comprova um mecanismo capaz de amplificar 30 para 60s; não demonstra que todos os intervalos produtivos de 30, 31, 42 e 61s tenham essa mesma causa.

## Correção e política HTTP

Alterações funcionais restritas a `GtfsRealtimeGps.cs` e `GtfsRealtimeGpsSource.cs`:

- O intervalo mínimo continua sendo contado entre inícios de GET, sem baixar a configuração mínima de 30s.
- A validade considera idade aparente por Date, Age e trânsito até os headers. Na recepção: `idade = max(max(0, recepção − Date), Age + max(0, recepção − início))`; `validade = recepção + max-age − idade`. O próximo GET respeita o maior entre intervalo mínimo, validade futura e Retry-After. Ausências de Date/Age usam idade aparente/Age zero.
- Esse cálculo segue a [RFC 9111, seção 4.2.3](https://www.rfc-editor.org/rfc/rfc9111.html#section-4.2.3). O corpo consumido após os headers não reinicia a validade. `ReceivedAtUtc` continua indicando a conclusão da leitura/parsing do corpo; timestamps GPS e do feed não são reescritos.
- 304 conserva o corpo e seu instante original, retém max-age omitido e usa políticas/validadores fornecidos na revalidação. A atualização de metadados segue a [RFC 9111, seção 4.3.4](https://www.rfc-editor.org/rfc/rfc9111.html#section-4.3.4). No-cache continua exigindo validação sem violar o intervalo. No-store também remove corpo/validadores após 304, impedindo reutilização posterior. Retry-After absoluto e relativo continuam podendo ampliar a espera.
- Não há retry novo, flag, timer, fila ou cache por veículo. O cliente continua serializado, com um snapshot, mesmos limites de bytes/entidades e timeout. Parser/validação permanecem literalmente iguais.

O cliente é compartilhado por BUS e BRT; ambos recebem a correção. As expectativas antigas de 45/75s nos testes BRT foram substituídas por 30/60s, mantendo asserções de validade, identidade, deduplicação e falhas. Os documentos anteriores permanecem como evidência histórica do algoritmo anterior.

## Aquisição, dados aceitos e publicação

O novo campo interno enumera quatro resultados fixos, registrados uma vez por leitura no logger já existente da fonte. Só contém modal/fonte, resultado, duração e reutilização; não registra URL, ETag, coordenadas, linha ou identidade de veículo. Não foi criado sistema de métricas paralelo nem estado de cardinalidade variável.

| Resultado | Significado | O que não comprova |
| --- | --- | --- |
| Downloaded | Resposta HTTP com corpo validado pelo parser | Avanço de timestamp GPS ou movimento |
| LocalCache | Nenhum GET nesta leitura; reutilização de resultado local, inclusive falha/validação obrigatória | Aquisição nova, mesmo com status Sucesso e mesma contagem |
| NotModified | GET real recebeu 304; reutilização do snapshot anterior | Novo corpo, novo GPS ou renovação de ReceivedAtUtc |
| Failed | Aquisição falhou, incluindo HTTP, timeout ou conteúdo inválido | Disponibilidade de posições operacionais |

`BUS current snapshot: status=Sucesso` continua significando observações promovidas pela fonte, que podem vir do cache. A geração do handoff é um lote publicado para consumo, não um identificador de corpo HTTP. O caminho cacheado mantém essas leituras para não alterar recuperação/ACK ou semântica operacional.

Dados novos seguem passando pela deduplicação temporal, matching e CAS. `Novas` no desempenho do ciclo mede elegibilidade após deduplicação; o resultado Accepted do CAS determina aceitação. `linhas_publicadas` e `veiculos_enviados` continuam medindo publicação SignalR separadamente. Não se deve inferir aceitação ou publicação a partir de Downloaded ou Sucesso; SignalR também pode reenviar estado conforme assinaturas.

## Cobertura e resultados

23 casos novos na parcial `GpsPollingCadenciaTests.BusAcquisition.cs`, automaticamente incluída na classe já presente na allowlist offline. Nenhuma alteração em CI ou projeto.

| Requirement | Evidence |
| --- | --- |
| Amplificação BUS, latência variável, cache 30s e timestamps reais do payload | `Bus_CollectorDoesNotMissExpiredHttpSnapshotWithVariableLatency`: quatro casos RED/GREEN, ACK a cada coleta, sequência de GETs e timestamps completos |
| Limite entre GETs, com/sem cache; antes e na fronteira | `Gtfs_HttpRateLimitStaysAnchoredToRequestStart`: 29.999s não adquire, 30s adquire; três casos |
| Date antigo/futuro, Age zero/positivo/expirado, cache 60s | `Gtfs_DateAgeAndTransitRespectFreshnessAndMinimumInterval`: seis casos e fronteiras exatas em 30, 40, 50 e 60s |
| 304 com política omitida/alterada, mudança de ETag e corpo novo | `Gtfs_304RetainsOrUpdatesCachePolicyWithoutRenewingGps`: duas variantes, weak ETag na revalidação e ETag novo enviado no próximo GET; identidade do corpo e recepção preservadas em 304/cache |
| Rate limit e Retry-After com falhas | `Gtfs_RetryAfterIsNeverShortenedByAgeCorrection`: quatro casos 429/503, data/delta, antes e na fronteira; também diferencia Failed de LocalCache |
| Latência do corpo e semântica GPS | `Gtfs_BodyLatencyDoesNotRestartFreshnessOrChangeGpsTime`: headers 2s, corpo 7s, ReceivedAtUtc=9s, GPS original e GET seguinte em 30s |
| Políticas restritivas em 304 | `Gtfs_304NoStoreEvictsBodyAndValidatorsWithoutBypassingInterval` e `Gtfs_NoCache304KeepsValidationRequirementAndMinimumInterval` |
| Horários do coletor e limite da melhoria | `Bus_CollectorCompletionDelayStillQuantizesAcquisition`: espera padrão 10s após conclusão, GETs 0/31/70/103, mais leituras Sucesso que aquisições |
| Backpressure, serialização, cancelamento e compatibilidade legada | `Bus_CollectorKeepsHttpCadenceAndSingleSnapshotUntilAck`, `CicloAcimaIntervalo_SnapshotMantemUmaVagaAteAck`, `ApenasUmaColetaSppoFicaEmAndamento`, `ShutdownEhPropagadoENaoPublicaLote`, `Brt_LegacySourceKeepsConfiguredGateInterval` |
| Deduplicação/ordem e publicação independentes | `Gtfs_RepeatedGpsInRealPollingNeverReachesMatchingOrCas` (BUS/BRT, ativo/watermark), `DuplicateVehicleLatestWinsButSameTimestampConflictIsRejected`, `RealPolling_ReusedBrt_StopsBeforeMatchingCommitEtaAndPublication`, `ActivePayloadWinsOverRecentAndCycleCandidate`, `AcceptedCandidateWithoutPersistedPayload_DoesNotBecomeActive` |
| Validação e memória limitadas | `OversizedAndTruncatedBodiesNeverPublish`, `EmptyFullFeedIsValidAndTruncationFails`, `ConcurrentRequestsReuseSameImmutableSnapshot`, `InvalidFeedCannotBeUsed`; comparação literal do parser com HEAD |

Comando RED/GREEN principal (mudando apenas o nome do TRX a cada execução):

```powershell
dotnet test NoPonto\NoPonto.csproj --no-restore -c Release --filter 'FullyQualifiedName~Bus_CollectorDoesNotMissExpiredHttpSnapshotWithVariableLatency|FullyQualifiedName~Gtfs_HttpRateLimitStaysAnchoredToRequestStart' --logger 'trx;LogFileName=bus-acquisition-green.trx' --results-directory NoPonto/lab-output/bus-acquisition-20261021
```

Seleção final direcionada, com compilação Release concluída:

```powershell
dotnet test NoPonto\NoPonto.csproj --no-restore -c Release --filter 'FullyQualifiedName~NoPonto.Tests.GpsPollingCadenciaTests.|FullyQualifiedName~NoPonto.Tests.GtfsRealtimeGpsTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoCollectorTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoClientTests.|FullyQualifiedName~NoPonto.Tests.GpsPollingFontesTests.' --logger 'trx;LogFileName=bus-acquisition-directed-final.trx' --results-directory NoPonto/lab-output/bus-acquisition-20261021
```

`Passed: 172, Failed: 0, Skipped: 0`. A seleção anterior, antes dos dois últimos controles, passou 170/170. Compilação do projeto sem erros e com os cinco avisos anteriores: dois CS8981, CS7022 e dois xUnit2031.

Seleção offline oficial:

```powershell
$offlineFilter = python .ci/offline_tests.py filter
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test NoPonto\NoPonto.csproj --no-restore --no-build -c Release --filter $offlineFilter --logger 'trx;LogFileName=bus-acquisition-offline.trx' --results-directory NoPonto/lab-output/bus-acquisition-20261021
python .ci/offline_tests.py verify NoPonto/lab-output/bus-acquisition-20261021/bus-acquisition-offline.trx
```

`Passed: 781, Failed: 0, Skipped: 0`. Verificador confirmou todas as classes da allowlist e nenhuma fora dela. Os conjuntos direcionado/offline se sobrepõem; as contagens não são somadas. Os seis TRXs foram parseados, com contagens, outcomes, nomes e hashes conferidos em [bus-acquisition-validation-20261021.json](bus-acquisition-validation-20261021.json). TRXs locais ficam em lab-output ignorado; agregados sanitizados ficam no JSON versionável.

O comando `dotnet build NoPonto.sln --no-restore --no-incremental -c Release` foi rejeitado antes de executar pela revisão automática de aprovação: aprovação exigida, indisponível nesta sessão. Não houve build independente da solução. A compilação do projeto ocorreu pelo comando autorizado de testes acima; isso não é relatado como build completo da solução.

## Riscos, resultado esperado e limites

A correção remove a perda de oportunidades causada pelo cálculo HTTP. Com oportunidades a cada 15/30s, headers atrasados e max-age=30, a reprodução cai de 45/60s para 30s entre inícios dos GETs. Não força atualização do provedor nem transforma 304/ETag diferente em avanço GPS. O corpo pode permanecer idêntico, com mesma quantidade de posições, apesar de um GET válido novo.

A espera após conclusão do coletor não mudou. Com a configuração padrão de 10s e ACK imediato, a fixture com latências 1/9/3s ainda adquire em 0/31/70/103s; não há promessa de GETs fixos em 30s nessa configuração. Backpressure também continua impedindo coleta com geração pendente. Polling de 15s e processamento de 9–13s ainda podem adicionar espera ao consumo/publicação. A melhoria é mais visível quando o próximo ciclo coincide com a expiração antes bloqueada; outras fases podem continuar dominando a idade da API.

Para atribuir os intervalos exatos produtivos faltam o intervalo efetivo do coletor e uma sequência correlacionada de início/fim de HTTP, Date/Age/Cache-Control, 200/304, ACK/consumo e publicação. Foram solicitados os valores/headers já disponíveis ao usuário; não houve autorização inferida para coletar novos dados em produção. A imagem instalada não foi comparada com este código. Não é possível prometer a nova mediana de idade em `/veiculos/linha/838` sem futura homologação autorizada.

Risco principal: maior frequência efetiva de GETs BUS/BRT até o limite já configurado, sem antecipar o intervalo mínimo ou Retry-After. Date incorreto do provedor pode mudar a validade calculada, preservando o mínimo local. Há um log agregado por leitura da fonte, com apenas quatro valores de aquisição e duas fontes fixas; volume acompanha a frequência existente do coletor/polling.

Seis fontes protegidas foram comparadas com HEAD: coletor, store, polling, opções do coletor, deduplicação e publicador SignalR. Permanecem iguais. Nenhuma alteração em frontend, flags, configurações operacionais, infraestrutura, Docker, Redis/PostgreSQL, migrations ou estado de rollback. Não executados: integração conectada, API/SignalR por rede real, carga de frota, fonte pública GPS, deploy, commit ou push. Mudanças ficam locais para revisão; histórico anterior preservado e acrescido.
