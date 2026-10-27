# Hybrid ITS + GTFS desacoplado — implementação local em 27/10/2026

Base: `16e72b90a65978f4b5434433138e2d0d8016cfd7`. Motivação: [janela beta de 27/10](its-homeserver/beta-diagnostic-20261027.md). Implementação local, sem SSH, feeds reais, push, merge, deploy, banco, nova imagem ou janela produtiva.

## Fluxo anterior e pontos de espera

O resolver é singleton em `Program.cs` e cria um wrapper `ItsFallbackGpsSource` uma única vez. Antes, o wrapper mantinha seu gate enquanto aguardava ITS e só depois chamava o suporte GTFS. Quando ITS falhava, a primeira sondagem saudável era descartada operacionalmente; só a segunda permitia composição. Na janela isso custou T=127,90 s até T=210,06 s para admitir uma ITS já completa.

O coletor existente aceita `ISnapshotGpsSource`, não sobrescreve geração pendente e só volta a adquirir após o ACK. O polling preserva deduplicação temporal, matching, histórico físico, CAS e publicação. São esperas distintas: desacoplar fontes não elimina a duração do matching ou a espera de uma geração pelo consumidor.

## Novo fluxo, apenas em Hybrid

1. O wrapper serializa as chamadas ao compositor e a leitura GTFS pelo gate existente. Dentro dele, inicia ou acompanha **uma única tarefa ITS**, com budget configurado em `ItsCurrent`, token do chamador que a iniciou e token de parada do host.
2. Lê GTFS imediatamente pelo cliente/cache/crosswalk existentes e seu timeout. Não aguarda a tarefa ITS e não usa `Task.WhenAll` como barreira. A tarefa ITS sobrevive ao retorno normal de uma chamada do coletor; o encerramento do `CancellationTokenSource` dessa chamada não desfaz seu vínculo direto com o host.
3. A conclusão ITS somente produz um resultado e seu timestamp monotônico/UTC; não publica dados. O próximo leitor serializado admite uma cópia defensiva de uma leitura completa validada. Se ITS terminar durante a leitura GTFS, pode entrar na mesma composição; se terminar depois, entra na próxima chamada elegível do coletor.
4. O cache contém apenas o último snapshot completo admitido, sem histórico de gerações. Uma aquisição em andamento/concluída aguardando admissão ocupa no máximo outra vaga. Um resultado parcial/falho não substitui esse cache.
5. Antes de compor, revalida idade física e idade do cache usando `ItsCurrent:MaxGpsAgeSeconds`, padrão **180 s**, sem criar um TTL configurável novo. O limite de 300 s do compositor para GTFS não estende a validade ITS. Dados futuros, inválidos ou vencidos não são reutilizados. Não se altera GPS, recebimento ou timestamps de origem.
6. Cache vazio completo pode representar vazio válido, mas não atesta ausência atual quando uma aquisição posterior está pendente ou falhou. Sem observações utilizáveis e sem leituras completas elegíveis, retorna falha explícita, não vazio fictício.
7. A composição retorna **um snapshot ao mesmo coletor/store/ACK**. Não foi criado coletor, hosted service, escritor Redis, matching ou publicador SignalR adicional.

O primeiro snapshot completo válido após falha já contribui, mesmo que a saúde permaneça `recovering`. Duas sondagens espaçadas continuam controlando a declaração de recuperação e a cadência de novas sondagens, sem bloquear a admissão por veículo. Sucessos normais também respeitam o intervalo mínimo ITS; failures preservam o intervalo de sondagem configurado. Os limites HTTP internos do cliente continuam sendo a última proteção contra novas requisições.

**GTFS-only e FullFallback preservados.** O caminho sequencial e as duas sondagens operacionais de FullFallback continuam no arquivo original. Hybrid permanece exclusivo de ITS_CURRENT + GTFSRT_BUS, sem dependência operacional de ZIRIX_DIRECT. BRT, DTOs, matching, CAS, Redis, PostgreSQL, SignalR, TTLs e cadências não mudaram.

## Seleção e continuidade

O compositor continua agrupando pela identidade normalizada e escolhendo o **maior timestamp físico válido**. Horário de recebimento não arbitra seleção nem renova GPS. No empate, divergências de coordenada, linha ou sentido conhecido impedem a observação; empates compatíveis preferem os metadados GTFS e os desempates estáveis existentes. Coordenadas e timestamps de fontes distintas nunca são misturados.

Se uma fonte desaparecer e o cache oferecer uma observação anterior à última já publicada, a deduplicação/CAS existente impede regressão. Não foi inserido watermark histórico no adaptador. Os testes de pipeline verificam a transição GTFS → ITS, histórico espacial, ACK, ausência de eventos aceitos duplicados e snapshots SignalR sem identidades duplicadas ou timestamps decrescentes. Repetir o snapshot completo de uma linha no SignalR continua sendo comportamento normal; não equivale a aceitar novamente a mesma observação no CAS.

## Ciclo de vida, cancelamento e memória

O resolver agora implementa `IDisposable` porque é dono do wrapper que constrói, enquanto as fontes registradas continuam pertencendo à DI. Recebe opcionalmente `IHostApplicationLifetime` e `IOptions<ItsCurrentOptions>`; `Program.cs` já registra esses serviços e não precisou mudar.

Parada do host, cancelamento da chamada iniciadora ou dispose impedem a admissão tardia. Dispose limpa o cache, sinaliza cancelamento e não aguarda IO do fornecedor. O cleanup tardio apenas observa o resultado e libera os tokens; não consegue atualizar cache nem publicar. A operação pendente mantém sua vaga até terminar, inclusive quando ignora cancelamento. Não se cria outra em cada polling. Uma nova instância não reutiliza tarefas/cache da anterior.

Memória adicional limitada a um cache, uma aquisição e um conjunto temporário de composição; sem fila de aquisições e sem lista histórica. Arrays admitidos são copiados defensivamente, preservando observações imutáveis. A contabilização de idades usa soma/contagem em uma passagem, sem ordenação extra por veículo. Não medimos CPU/RAM reais do homeserver nesta tarefa.

Limite conhecido: se IO não cooperativo nunca termina, a ITS fica bloqueada em sua própria vaga; GTFS continua sendo entregue. As flags `its_pending_cancelled`/idade da tarefa tornam isso visível. Não se força sobreposição para contornar o fornecedor. Um cache ainda fisicamente válido pode contribuir enquanto uma leitura nova falha; ao vencer, deixa de contribuir.

## Observabilidade agregada

Uma linha `BUS hybrid` por composição, preservando as chaves antigas e acrescentando:

| Campos | Significado |
|---|---|
| `its_duration_ms`, `gtfs_duration_ms` | Última aquisição ITS concluída e leitura GTFS desta composição, medidas separadamente |
| `its_pending`, `its_pending_cancelled`, `its_pending_elapsed_ms` | Vaga ITS ocupada, cancelamento solicitado e tempo em andamento |
| `gtfs_wait_avoided_ms` | Intervalo entre o primeiro GTFS pronto durante uma ITS pendente e a conclusão dessa ITS; mede espera de sincronização evitada, não uma simulação de todo o caminho antigo |
| `its_completed_sequence`, `its_pending_sequence` | Sequência local para correlacionar a aquisição e não somar novamente a mesma duração/espera repetida em vários logs |
| `its_cache_age_ms`, `its_cache_valid`, `its_cache_count` | Idade monotônica desde a conclusão real, elegibilidade e quantidade física válida nesta composição |
| `its_last_status`, `its_last_failure`, `its_health` | Resultado da aquisição separado do uso de cache e da histerese de saúde |
| `its_exclusive`, `gtfs_exclusive`, `shared`, `selected_its`, `selected_gtfs` | Identidades válidas exclusivas/compartilhadas e escolhas efetivas, sem equiparar seleção a publicação |
| `its_age_mean_seconds`, `gtfs_age_mean_seconds` | Idade física média das observações selecionadas no instante de composição |
| `receipt_age_mean_seconds` | Média GPS → recebimento original nas selecionadas com metadado utilizável; recebimento não é renovado pelo cache |

As durações ITS/espera são gauges da última sequência concluída, não contadores acumuláveis a cada log. `its_duration_ms` inclui a aquisição; o tempo esperando a próxima chamada do coletor não é computado como IO ITS. A sequência é local ao processo, não um identificador persistente de veículo.

Os logs ITS existentes por página continuam fornecendo headers/body/parse/normalização/rejeições e idade ao fim. A idade na publicação continua no `GpsPipelineTrace` opt-in existente, com coortes/limites configurados; o padrão rastreia apenas 898. Não foi alegada atribuição exata de toda publicação por fonte.

Verificação local de compatibilidade: o capturador de schema fechado continua aceitando a linha estendida e arquiva seus campos conhecidos; os novos campos são retidos por `parse_logs` nos eventos agregados do supervisor. O arquivo dedicado sozinho não contém os novos campos opcionais; preservar também `events.jsonl`. Nenhum script operacional ou hash dos cinco arquivos foi alterado nesta tarefa. Sem logs por veículo, URL, cursor, token ou coordenada.

## Testes e evidências

Dois testes reproduziram RED na revisão antiga: `Hybrid_SlowItsDoesNotHoldGtfsForThirtySeconds` e `Hybrid_FirstCompleteRecoverySnapshotIsAdmittedWithoutSecondProbe`. Ambos passaram após a implementação. Tempo, feeds, gates e repositórios são falsos/controlados, sem sleeps de aquisição e sem conexões externas. Timeouts de `WaitAsync` nos testes apenas limitam uma possível falha do runner.

| Requirement | Evidence |
|---|---|
| ITS lenta por 30 s; GTFS continua | `Hybrid_SlowItsDoesNotHoldGtfsForThirtySeconds` |
| Timeout/falha ITS sem interromper GTFS | `Hybrid_TimedOutNonCooperativeItsCannotOverlapOrAdmitLateData`; `Hybrid_FailedReadDoesNotReplaceLastCompleteCacheOrStopGtfs` |
| Uma aquisição por fonte sob concorrência | `Hybrid_ConcurrentCallsHaveOneItsAndOneGtfsReadInFlight` |
| Conclusão ITS admitida na próxima composição | `Hybrid_CompletedItsEntersNextCompositionWithoutMutatingGpsOrReceipt`; `Hybrid_ItsCanFinishDuringGtfsReadWithoutWaitingForAnotherPoll` |
| Cache vencido ou vazio não mascara falha | `Hybrid_CachedItsUsesConfiguredPhysicalAgeNotReceiptOrCompositionLimit`; `Hybrid_CompletedEmptyCacheExpiresWithoutClaimingUnknownItsIsEmpty`; `Hybrid_EmptyCacheCannotHidePendingOrFailedNewerAcquisition` |
| GPS físico mais novo vence, sem usar recebimento | `Hybrid_LatestPhysicalGpsWinsWithoutRenewingSourceMetadata`; `Hybrid_InvalidNewerCandidateCannotSuppressValidPeer` |
| Exclusivos ITS e 898 via GTFS | `Hybrid_HealthyItsIncludes898AndItsExclusiveThroughSingleAckHandoff` |
| Identidade/linha/sentido/coordenadas | `Hybrid_EqualTimeConflictsAreWithheldDeterministically`; `Hybrid_CompatibleTieIsOrderIndependentAndPrefersValidatedGtfs`; `Hybrid_InvalidCompletedItsCannotEnterCacheOrSuppressGtfs` |
| Cancelamento/dispose/restart/tardios | `Hybrid_CallerCancellationDropsCacheAndDiscardsLatePrimaryAndSupport`; `Hybrid_DisposeCancelsReadAfterCallerReturnedAndRejectsLateCompletionOnRestart`; `Hybrid_HostStoppingAndResolverDisposalOwnOnlyTheWrapperLifetime`; `Hybrid_DisposeInterruptsGtfsWaitWithoutJoiningNonCooperativeIts` |
| Handoff, histórico, CAS e SignalR | `Hybrid_PendingItsKeepsRealHandoffCasSpatialHistoryAndSignalRMonotonic`; `Hybrid_ItsCompletionCannotOverwriteUnacknowledgedGtfsHandoff` |
| Recuperação sem reter primeiro completo | `Hybrid_FirstCompleteRecoverySnapshotIsAdmittedWithoutSecondProbe`; `Hybrid_RecoveryAdmitsFirstCompleteProbeWhileHealthRemainsSpaced` |
| GTFS-only e FullFallback preservados | `Hybrid_StandaloneGtfsRollbackNeedsNoItsOrLegacySource`; `RecoveryRequiresTwoSpacedHealthyProbesAndFailureResetsStreak`; `ProvenPrimaryEmptyDoesNotQueryFallback` |
| Métricas sem identificação individual | `Hybrid_AcquisitionMetricsMeasureAvoidedWaitWithoutPerVehicleLogs` |
| Cache defensivo e sem acúmulo | `Hybrid_CacheIsDefensiveAndDoesNotAccumulateOldGenerations` |

Execução direcionada intermediária: **539 aprovados, 0 falhas**. A execução final oficial em Release cobre **544 testes afetados**, incluindo 160 da classe ITS e 117 de continuidade espacial/pipeline. Seleção offline final: **1.111 aprovados, 0 falhas, 0 ignorados**, verificada por `.ci/offline_tests.py verify`, com todas as classes da allowlist presentes. Os testes novos estão em classes já incluídas; não foi necessário modificar a CI.

Comando final PowerShell:

```powershell
$hybridOfflineFilter = python .ci/offline_tests.py filter
dotnet test NoPonto/NoPonto.csproj --no-restore --configuration Release --filter $hybridOfflineFilter --logger "trx;LogFileName=hybrid-offline-final.trx" --results-directory artifacts/hybrid-decoupled-tests -v quiet
python .ci/offline_tests.py verify artifacts/hybrid-decoupled-tests/hybrid-offline-final.trx
```

Incidente não ocultado: uma execução anterior offline teve 1.105 aprovados/1 falha de espera de 3 s em `HistoricalEtaGtfsPollingTests.PollingContinuesWithIndependentShadow` (`BRT`, `batch=False`, `export_failure`); repetir as 48 variações também reproduziu esse caso. O caso exato isolado passou, e as 48 variações na base 16e72b9 em worktree separado passaram. As execuções finais após ajustes locais passaram (1.108, 1.110 e 1.111, com os casos adicionais de vazio). A causa da intermitência não foi determinada. Não alteramos BRT/shadow, timeout, exclusões ou asserts para aprovar.

TRX e verificação de protocolo preservados em `artifacts/hybrid-decoupled-tests/`. Worktree base local separado também preservado para inspeção. Warnings de migration/entrypoint/analisador de testes existentes não foram tratados nesta alteração. Não foram executados testes conectados PostGIS/Redis, feeds reais, benchmark de rede, CI remota ou janela produtiva.

## Arquivos e próximos passos

Produção C#: `GpsSourceContracts.cs` (ownership/host/options), `ItsFallbackGpsSource.cs` (dispatch Hybrid/FullFallback), novo `ItsFallbackGpsSource.Hybrid.cs` (tarefa/cache/lifetime), `ItsHybridGpsComposition.cs` (validação reutilizada e métricas). Testes: novo `ItsCurrentGpsSourceTests.Decoupled.cs`, ajustes em `ItsCurrentGpsSourceTests.Hybrid.cs` e `GpsPhysicalPredecessorTests.cs`. Documentação: este relatório, `its-hybrid.md` e histórico.

Expectativa técnica: remover da entrega GTFS a espera pela aquisição ITS e pela segunda sondagem de admissão. O benefício depende do frescor físico que o GTFS realmente recebe. Não altera a idade de dados na origem, o cache HTTP, o matching, o tempo até ACK ou os TTLs, e não há percentual prometido de melhora no mapa. Retenção de um cache ITS antigo pode aumentar cobertura com poucos veículos recentes; os filtros físicos continuam obrigatórios.

Próxima sequência, mediante autorização: publicar branch/PR, exigir CI e build Release, produzir digest imutável e implantar inicialmente em GTFS-only. A imagem 151 não contém esta implementação. O supervisor e capturador atuais fixam o digest 151; uma futura janela com a imagem nova exige atualizar explicitamente o digest aprovado e seus hashes operacionais, sem contornar esse guard. Nada disso foi feito aqui.

Depois de saúde confirmada e autorização específica, uma única comparação beta com baseline GTFS de ~120 s e máximo de 600 s, nas mesmas sete BUS e três BRT de controle, com rollback obrigatório GTFS/FullFallback/trace OFF. Comparar intervalos entre gerações/ACK e renovações GTFS durante ITS pendente, idades por fonte e linha, cobertura realmente publicada e `gtfs_wait_avoided_ms` por sequência. Preservar captura, eventos, escritor único, lock e watchdog já implementados. Não é necessária outra auditoria geral para revisar este diff; a evidência com a imagem nova ainda precisa ser medida.
