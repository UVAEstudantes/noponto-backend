# BUS: latência entre aquisição e processamento

Branch local `fix/gtfsrt-bus-end-to-end-latency`, criada de `origin/main` após `git fetch origin main`, base `d412723a99028d09d6a7253553a30b61d877e64b` (merge que contém os patches 38–43). Árvore inicial limpa, branch anterior preservada. A release #136 e os tempos produtivos abaixo são evidências fornecidas pelo usuário; não foram consultados servidor, banco, REST ou fonte externa nesta etapa.

**Resultado local: 518 testes direcionados e 864 offline aprovados, zero falhas/ignorados.** Patch pronto para revisão; build completo e comparação produtiva continuam pendentes. Nenhum commit, push, PR, merge ou deploy realizado.

## Diagnóstico

| Snapshot HTTP BUS recebido (UTC) | Ciclo concluído (UTC) | Diferença |
| --- | --- | ---: |
| 20:08:01 | 20:08:22 | 21s |
| 20:08:33 | 20:08:51 | 18s |
| 20:09:04 | 20:09:25 | 21s |
| 20:09:35 | 20:09:59 | 24s |

São 21s de mediana entre recebimento e conclusão nesses quatro pares, com downloads separados por 32/31/31s. O problema restante está depois da aquisição HTTP. Não é nova evidência de amplificação do max-age. As parcelas fornecidas foram espera de polling 6–11s, matching 8–9,3s e outras etapas 3–6s; sem os logs integrais correlacionados não é possível atribuir um valor exato de cada parcela a cada linha, nem somar extremos/medianas independentes como se fossem o mesmo ciclo.

O caminho encontrado no código é:

1. `GtfsRealtimeGpsSource`: após receber/validar HTTP, resolve crosswalk PostgreSQL, mapeia e filtra observações. O recebimento HTTP antecede a publicação no handoff. O custo dessa passagem não deve ser chamado de espera de polling.
2. `GpsSppoCollectorService`: para fonte snapshot, só coleta com vaga livre. Publica um snapshot atual, sem janela/watermark histórico. Antes do patch, depois do ACK aceitava novamente o mesmo conjunto de GPS, inclusive LocalCache/304 ou 200 com GPS repetido. A geração nova de 20:08:53 consumida com zero novas posições é compatível com esse caminho. O coletor espera 10s após conclusão conforme evidência anterior; esse agendamento não foi modificado.
3. `GpsSppoSnapshotStore`: uma vaga fica ocupada até ACK da geração exata. Antes do patch não havia notificação ao consumidor; o polling dormia até o timer de 15s observado em produção. Esse desalinhamento explica a espera de 6–11s informada. Se BUS chegasse durante a consulta BRT, a leitura única anterior a BRT também o deixava para o próximo ciclo.
4. `GpsPollingService`: lê BUS, aguarda BRT, normaliza/filtra idade, lê Redis/watermarks/predecessores físicos, elimina timestamps antigos/iguais e lê contextos de viagem. Só então enriquece. Todo o enriquecimento necessário termina antes de ETA e commits; portanto os 8–9,3s de matching são parte do caminho crítico, mesmo com paralelismo por entrada.
5. Matching: com batch habilitado, `GpsEnriquecimentoService.Batch.cs` registra entradas em duas barreiras, executa grupos global simples/combinado e depois o subconjunto direcionado pedido pelas decisões existentes. `GpsItinerarioRepository.Batch.cs` valida entradas, divide em chunks e executa-os sequencialmente. Cada comando inclui serialização, obtenção de conexão Npgsql, SQL espacial e leitura/materialização. Global/direcionado usam projeção, distância, bearing, score e próxima parada em LATERAL; combinado escolhe o motor já configurado, com os mesmos ramos observacional/anterior/operacional. Circuito/fallback permanecem os existentes. `matching_etapa_ms` é tempo de parede da etapa; soma de durações de comandos/consultas não é necessariamente tempo de parede quando há concorrência.
6. Restante: ETA condicional/PhysicalETA, CAS e viagens com paralelismo limitado, índices por linha, estado recente e SignalR, então ACK e log final. Os 3–6s informados podem incluir essas etapas e leituras anteriores; o material fornecido não separa conexão, SQL, Redis e escrita durável. Redis recebe commits por veículo antes da conclusão global; não há uma troca atômica de toda a frota no log final. A amostra REST de 20:09:57 durante o ciclo que terminou às 20:09:59 pode observar os predecessores dos veículos ainda não confirmados, mas o horário sozinho não prova o instante de cada CAS.

Um ciclo normal de 15,7s com intervalo de 15s leva ao descanso de recuperação de 5s: próximo início em aproximadamente 20,7s desde o anterior. Esse descanso continua intacto. Não há sobreposição nem loop de catch-up adicionado.

## Correção mínima

- **Coletor:** guarda apenas o conjunto de identidades/timestamps do último snapshot publicado. Se o próximo conjunto for igual, ignora ordem e caixa da identidade e não cria geração. A resposta da fonte continua Sucesso, explicitamente distinta de publicação. Compara por veículo, não por maior timestamp global; um veículo que avança abaixo do máximo de outro continua elegível. Mudança de membros ou timestamps publica o conjunto completo. Não faz filtro parcial nem altera decisões de matching; Redis/CAS continuam a autoridade temporal. Falha/vazio não apagam a comparação; restart começa sem histórico. O estado é substituído, nunca acumulado, e só avança após publicação bem-sucedida. A fonte GTFS mantém seu limite de entidades.
- **Store/polling:** notificação de disponibilidade por geração com TaskCompletionSource e continuações assíncronas, protegida pelo mesmo lock. Publicação notifica; ACK correto rearma. Esperar não consome nem libera vaga. Somente BUS GTFS pode antecipar o repouso de um ciclo normal abaixo do intervalo; falha, overrun/descanso de 5s, legado e GTFS somente BRT mantêm o timer. O único loop consumidor continua serial. TimeProvider interno controla exclusivamente essa espera em teste; timeouts operacionais permanecem iguais. WaitAsync remove seus registros de timeout/cancelamento, sem tarefa de delay abandonada nem cancelamento do sinal compartilhado.
- **Leitura tardia:** se não selecionou BUS no início, relê depois de BRT e antes de montar o input do ciclo. Nunca substitui uma geração já selecionada. Mantém o ACK ao final e a geração pendente após falha.
- **Diagnóstico limitado:** log `BUS current snapshot unchanged` indica supressão; `BUS handoff consumed` registra geração e `handoff_wait_ms`, da publicação à seleção do lote. Se selecionado antes de BRT, esse tempo não inclui a consulta BRT; se selecionado na segunda leitura, inclui o tempo até essa seleção. Não mede HTTP→handoff, matching ou CAS. Sem identidade, coordenadas, ETag ou payload nos logs novos. `delay_planejado_ms` existente continua um planejamento; pode ser abreviado por notificação e não deve ser interpretado como espera efetiva.

SQL, motores/flags, tamanho de chunk, limites de paralelismo e políticas de fallback não foram alterados. Sem plano/perfil de uma base representativa não há justificativa segura para reescrever matching ou aumentar concorrência. O patch reduz espera e entradas redundantes no pipeline, sem alegar reduzir os 8–9,3s de matching de uma atualização genuína.

## RED/GREEN e métricas locais

| Requirement | Evidence |
| --- | --- |
| Não republicar conjunto GPS inalterado | `BusHandoff_UnchangedGpsDoesNotPublishAnotherGeneration`: LocalCache/304/200 repetido, três REDs de geração 2 indevida; GREEN sem geração e recuperação posterior com timestamp físico novo. |
| Comparar por identidade e timestamp, não máximo global | `BusHandoff_IdentityAndPerVehicleTimestampAreComparedWithoutGlobalWatermark`: RED no conjunto apenas reordenado; GREEN também aceita avanço do veículo mais antigo e troca de membro. `BusHandoff_FailedOrEmptyReadDoesNotResetIdentityAndRestartStartsFresh` cobre falha/vazio e restart. |
| Antecipar repouso normal sem ACK/perda de sinal | `BusHandoff_PublicationWakesNormalRestWithoutConsumingOrAcknowledging`, `BusHandoff_AlreadyPendingOrArrivingBeforeWaitCannotLoseWakeup` e `BusHandoff_CancelledWaitDoesNotCancelSharedSignalOrBypassBackpressure`: RED sem despertar; GREEN preserva lote/ACK, sinais rearmados, cancelamento isolado, zero timers restantes e produtor bloqueado até ACK. |
| Manter timers nos demais caminhos | `BusHandoff_NoPublicationStillHonorsTheTimerBoundary`, `BusHandoff_FailureOverrunAndLegacyRestCannotBeInterrupted`, `BusHandoff_BrtOnlyGtfsKeepsPeriodicRest`: controles GREEN nas fronteiras imediatamente antes/igual ao prazo. |
| Consumir chegada durante BRT no mesmo ciclo | `BusHandoff_SnapshotArrivingDuringBrtIsConsumedInTheSameRealCycle`: RED deixa lote pendente; GREEN confirma no ciclo real. GPS propositalmente stale isola o handoff de banco/commit; não é prova de desempenho de matching. |

17 novos casos permanentes, incluídos automaticamente na seleção offline pela parcial existente. O relógio manual não usa sleeps; limites reais de 2s são apenas watchdog contra deadlock. Uma passagem interna inicialmente idêntica ao Task.Delay anterior tornou a espera controlável antes do RED; a implementação de antecipação veio depois da reprodução.

- RED final antes da correção: 5 aprovados / 9 falhas esperadas / 0 ignorados, 14 casos. Primeiro ensaio teve fixture legado com nome duplicado e campos required faltantes; corrigidos antes desse RED final. GREEN intermediário: 13/14, com uma fixture que ainda devolvia GPS antigo na recuperação LocalCache; corrigida para devolver corpo novo nessa oportunidade. Três controles adicionais foram acrescentados depois. Erros intermediários de compilação de novas fixtures não foram tratados como RED funcional.
- GREEN final direcionado: **518/518**, zero falhas/ignorados. Inclui matching batch diferencial/orquestração, deduplicação, handoff/ACK, PhysicalETA, viagens, SignalR e todas as regressões HTTP 38–43. Cinco avisos de compilação preexistentes, sem novos avisos/erros na rodada final.
- Offline oficial: **864/864**, zero falhas/ignorados; `.ci/offline_tests.py verify` confirmou todas as classes. São os 847 casos anteriores mais 17; as seleções se sobrepõem e não devem ser somadas.
- Simulação de espera: repouso programado até t=11; publicação em t=6. Antes, não acordava sem avançar o timer até t=11; depois, termina em t=6, mantendo o lote pendente. São **5s de espera restante removida nesse cenário controlado**, não uma medição produtiva nem garantia de dispatch imediato pelo sistema operacional.
- Geração redundante após ACK: antes, 1 geração adicional nos cenários LocalCache/304/200 repetido; depois, 0. HTTP não é suprimido nem antecipado por essa comparação; a atualização seguinte preserva o timestamp do payload.

Comandos de validação executados (SDK 9.0.306 / VSTest / xUnit):

```powershell
# RED: executado antes da correção; agora deve passar.
dotnet test NoPonto/NoPonto.csproj --no-restore -c Release --filter 'FullyQualifiedName~NoPonto.Tests.GpsPollingCadenciaTests.BusHandoff_' --logger 'trx;LogFileName=handoff-red-final.trx' --results-directory NoPonto/lab-output/bus-handoff-20261021

dotnet test NoPonto/NoPonto.csproj --no-restore -c Release --filter 'FullyQualifiedName~NoPonto.Tests.GpsPollingCadenciaTests.|FullyQualifiedName~NoPonto.Tests.GtfsRealtimeGpsTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoCollectorTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoClientTests.|FullyQualifiedName~NoPonto.Tests.GpsPollingFontesTests.|FullyQualifiedName~NoPonto.Tests.GpsMatchingBatchOrquestracaoTests.|FullyQualifiedName~GpsTimestampDeduplicationTests.|FullyQualifiedName~NoPonto.Tests.GpsSignalRPublisherTests.|FullyQualifiedName~NoPonto.Tests.GpsPhysicalPredecessorTests.|FullyQualifiedName~NoPonto.Tests.PhysicalEtaIntermittenceTests.|FullyQualifiedName~NoPonto.Tests.PhysicalEtaMvpTests.|FullyQualifiedName~NoPonto.Tests.PhysicalEtaSelectedStopTests.|FullyQualifiedName~NoPonto.Tests.ViagemOperacionalRegraTests.' --logger 'trx;LogFileName=handoff-directed.trx' --results-directory NoPonto/lab-output/bus-handoff-20261021

$offlineFilter = python .ci/offline_tests.py filter
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test NoPonto/NoPonto.csproj --no-restore --no-build -c Release --filter $offlineFilter --logger 'trx;LogFileName=handoff-offline.trx' --results-directory NoPonto/lab-output/bus-handoff-20261021
python .ci/offline_tests.py verify NoPonto/lab-output/bus-handoff-20261021/handoff-offline.trx
```

Tentativa `dotnet build NoPonto.sln --no-restore --no-incremental -c Release`: **não iniciou**, rejeitada pela revisão automática: `approval required by policy, but AskForApproval is set to Never`. Compilação Release do projeto pelos testes aprovada; build independente da solução não aprovado nem contornado.

## Preservação, riscos e validação posterior

Arquivos funcionais alterados: `GpsSppoCollectorService.cs`, `GpsSppoSnapshotStore.cs`, `GpsPollingService.cs`. Testes: nova parcial `GpsPollingCadenciaTests.Handoff.cs` e expectativas de geração nas parciais `BusAcquisition.cs`/`Gtfs.cs`; continuam verificando os mesmos GETs e timestamps, agora exigindo ausência de geração redundante. Demais entregas: este relatório, `bus-handoff-validation-20261021.json` e entrada acrescentada no HISTORICO. Snapshot inicial e plano interno guardados fora da árvore Git.

Cliente/fonte GTFS e regressões de outages/304 estão preservados por hash. Sem alteração de TTL, CAS, estado recente, parser, timestamps físicos, matching, PhysicalETA, viagens, persistência, SignalR, opções, timeout, configuração, projetos, banco, migrations, Docker, servidor ou produção. Relatórios 38–43 e planos de diagnóstico/rollback 41/42 intactos. Testes conectados com PostgreSQL/Redis não foram executados. Não foi exercido startup de containers.

A antecipação pode aumentar a frequência de ciclos em torno de atualizações BUS. Deduplicação e comparação de snapshots limitam trabalho repetido, mas há custo adicional possível de BRT/Redis/SignalR/manutenção do ciclo. Limites HTTP por cliente/modal continuam no cliente inalterado, inclusive Retry-After e no-cache/no-store. Esses limites não representam teto global entre réplicas/sondas. Não há sobreposição de ciclos; atualizações durante matching/commit ainda aguardam esse ciclo e, em overrun, o repouso de 5s. BRT continua bloqueante antes do processamento BUS. Matching genuíno continua com seu custo atual.

Após revisão/build e autorização futura de implantação manual, aplicar o comparativo do plano 41: janelas equivalentes, mesma carga/frota, idade GPS fonte versus REST838, downloads/304/LocalCache/falhas separados de geração/novas/CAS/SignalR. Medir `handoff_wait_ms` (p50/p95), HTTP→handoff por correlação temporal, matching, commit/viagem, duração e start-to-start, overruns, CPU/memória, frequência BRT e 429/503/Retry-After. Espera normal deve diminuir; matching, falhas ou carga não devem regredir. Separar períodos com falha/overrun do repouso normal e não aceitar melhora baseada em timestamps renovados ou veículos diferentes. Os critérios de interrupção/rollback existentes permanecem em 41; nenhum comando desses planos foi executado.

**Não há ganho produtivo comprovado nesta etapa.** Os bloqueios restantes são o build completo em ambiente autorizado e a validação operacional após eventual implantação autorizada. O custo interno dos 8–9,3s de matching exige perfil/planos e corpus diferencial representativo antes de qualquer otimização SQL posterior. Hashes, contadores TRX e preservação: [bus-handoff-validation-20261021.json](bus-handoff-validation-20261021.json).
