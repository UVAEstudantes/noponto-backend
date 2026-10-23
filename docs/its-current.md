# ITS_CURRENT — plano e operação (2026-10-23)

## Estado atual — validação operacional e fallback

Pronta para homologação conectada ao pipeline em desenvolvimento. A aquisição real SPPO foi executada isoladamente; o pipeline e a transição foram validados com fixtures em memória. Nenhuma posição real passou por coletor, Redis, matching ou SignalR nesta sessão. As seções posteriores preservam o plano e os resultados da primeira implementação; a indicação antiga de ausência de fallback foi superada nesta continuação.

**Shadows não fornecem fallback automático.** `BusShadowSources` continua diagnóstico. O opt-in operacional é separado:

```powershell
# Somente no processo de desenvolvimento, com conexões locais já verificadas.
$env:GpsSources__BusPrimarySource = 'ITS_CURRENT'
$env:GpsSources__BusFallbackSource = 'ZIRIX_DIRECT'
# Reiniciar o backend pelo procedimento habitual de desenvolvimento.
```

Rollback por configuração (também exige novo processo):

```powershell
$env:GpsSources__BusPrimarySource = 'ZIRIX_DIRECT'
Remove-Item Env:GpsSources__BusFallbackSource -ErrorAction SilentlyContinue
```

Sem `BusFallbackSource`, ITS continua sem fallback; o padrão de fonte principal permanece ZIRIX_DIRECT. Nenhuma dessas configurações foi aplicada ao backend/servidor nesta sessão.

O resolver cria uma única `ItsFallbackGpsSource` quando BUS=ITS_CURRENT e fallback=ZIRIX_DIRECT. Ela implementa a mesma interface de snapshot do coletor. Falha, parcial e timeout de ITS ativam ZIRIX no mesmo ciclo; cancelamento do chamador se propaga e vazio válido não aciona fallback. O fallback consulta uma janela recente de 60 s, elimina duplicatas por GPS físico e não inventa watermark histórico. Nenhum produtor/publicador paralelo foi criado.

Em fallback, ITS é sondada a cada `GpsSources:BusFallbackProbeIntervalSeconds` (60 s por padrão, contado após o término da sondagem). Duas sondagens saudáveis consecutivas são necessárias para retornar a ITS; falha/parcial zera a sequência. As observações da primeira sondagem saudável não são misturadas ao fallback. Vazio comprovado conta como saudável e não publica posições. O timeout próprio ZIRIX é `BusFallbackTimeoutSeconds` (10 s por padrão). I/O tardio é observado/descartado e impede outra chamada à mesma fonte enquanto estiver pendente. Se ambas falharem, não há handoff. O coletor mantém seu ACK; o polling/CAS mantém deduplicação por veículo/GPS entre fontes, histórico espacial e TTLs existentes. Cobertura CONECTA pode cair durante fallback ZIRIX; o fallback não recompõe veículos ausentes nem garante recuperação histórica.

Foi observada resposta inicial `provider_incomplete` seguida de respostas completas, compatível com preparação temporária do minuto corrente. A correção aguarda somente estados explícitos `pending`: até três novas tentativas da mesma página (`ItsCurrent:ProviderPendingRetries`, 0–5), com 2 s de espera, sempre dentro do prazo total e do limite global de respostas. Corpos incompletos são descartados. Estado ausente, ambíguo ou erro continua parcial sem esse retry; Retry-After HTTP continua respeitado. O teste novo reproduziu RED (`Parcial` em vez de `Sucesso`) e passou após a correção. Nenhum dado parcial foi promovido a vazio ou sucesso.

### Smoke somente de leitura

[Contrato público ITS consultado](https://its.mobilidade.rio/openapi.json): filtro de minuto usa **datetime_servidor**, janela fixa de um minuto UTC, `servico` opcional, limite 1–5000 e cursor até 48 caracteres. Ambos os provedores SPPO retornaram `ok` nas páginas completas. A amostra não prova cobertura integral da rede.

`tools/ItsCurrent.Smoke` instancia apenas HttpClient, GpsDatarioClient e ItsCurrentGpsSource, sem host/configurações/serviços de aplicação. Requer flag explícita e imprime somente agregados controlados. O projeto referencia o backend e alinha a dependência transitiva EF 9.0.10 como o utilitário EtaMl.Export existente; nenhuma dependência foi adicionada ao backend.

```powershell
dotnet run --project tools/ItsCurrent.Smoke/ItsCurrent.Smoke.csproj -c Release -- --read-only
# Após compilar, repetir somente se necessário à investigação:
# dotnet run --project tools/ItsCurrent.Smoke/ItsCurrent.Smoke.csproj -c Release --no-build -- --read-only
```

| Medida | Aquisição completa inicial | Após correção de readiness |
| --- | ---: | ---: |
| Início UTC em 2026-10-23 | 21:03:12 | 21:11:27 |
| Duração da fonte | 14,5 s | 21,5 s |
| Respostas / janelas concluídas | 9 / 3 | 10 / 3 |
| Registros adquiridos / rejeitados pela normalização | 33770 / 1711 | 38120 / 3865 |
| Veículos finais / serviços distintos | 3561 / 365 | 3522 / 365 |
| Selecionados ZIRIX / CONECTA | 2754 / 807 | 2736 / 786 |
| Exclusivos ZIRIX / CONECTA disponíveis na aquisição | 162 / 242 | 759 / 212 |
| GPS até 40 s / até 60 s na entrega | 1558 / 3404 | 1224 / 3358 |
| Idade p50 / p90 na entrega | 41,2 / 54,2 s | 43,8 / 56,8 s |
| GPS físico mínimo UTC | 21:00:44 | 21:08:58 |
| GPS físico máximo UTC | 21:03:07 | 21:11:24 |
| Com timestamp de servidor / envio | 3561 / 0 | 3522 / 0 |
| GPS futuros / identidades duplicadas | 0 / 0 | 0 / 0 |

O envio ausente permanece null; servidor/recepção não substituem o GPS físico. As contagens disponíveis/exclusivas são calculadas sobre os registros válidos durante aquisição; os selecionados ainda passam pela checagem de idade na entrega. Primeira tentativa, às 21:01:16 UTC: parcial em uma resposta/1,7 s por estado de provedor, zero posições entregues. Probes limitados de contrato confirmaram páginas cheias de 5000 e cursor presente. Nenhum payload/identidade/coordenada foi salvo ou impresso pelo smoke; artefatos contêm apenas contrato público e resumos agregados.

### Testes desta continuação

- Seleção focada ITS/fallback + novas transições espaciais: **66 aprovados, 0 falhas, 0 ignorados**.
- Regressão ampliada de fontes/coletor/cadência/handoff, predecessor físico e publicação: **393 aprovados, 0 falhas, 0 ignorados**.
- Seleção offline oficial: **987 aprovados, 0 falhas, 0 ignorados**, verificador oficial aprovou todas as classes. Nenhuma exclusão adicionada.
- Compilação Release do backend pelos testes aprovada, com os cinco avisos anteriores. Build independente da solução e testes com Redis/PostgreSQL reais não executados. O teste ETA intermitente documentado na primeira etapa passou na seleção desta continuação, sem ser alterado.

São 22 casos adicionais nesta continuação: `ShadowsDoNotImplicitlyEnableOperationalFallback`, `ExplicitFallbackUsesExistingSnapshotCollectorAndOnlyHealthyZirixPositions`, `ProvenPrimaryEmptyDoesNotQueryFallback`, `UnusableFallbackNeverPublishesOrLeaksPartialPositions`, `RecoveryRequiresTwoSpacedHealthyProbesAndFailureResetsStreak`, `RecoveryAlsoAcceptsTwoProvenEmptyProbesWithoutPublishing`, `CallerCancellationDoesNotTriggerFallback`, `FallbackTimeoutBoundsNonCooperativeIoAndPreventsNewOverlappingRequest`, `ResolverRejectsUnsupportedFallbackWithoutChangingLegacyDefault`, `FallbackDeduplicatesWindowByPhysicalTimestamp`, `PendingReadinessCannotEscapeGlobalPageLimitOrAcquisitionBudget`, `PendingProviderReadinessRetriesSamePageBeforeAcceptingCompleteSnapshot` e `GpsPhysicalPredecessorTests.ItsZirixSourceTransitionsKeepMonotonicGpsAndSpatialHistory` (incluindo suas parametrizações). A última usa polling/enriquecimento reais e repositório/cache em memória: GPS anterior/empatado de outro provedor não chega à escrita; GPS novo preserva coordenadas/timestamp anteriores, projeção e identidade. O CAS persistente continua sendo a proteção entre processos/reinícios.

TRXs e resumo limpo do smoke: `artifacts/its-current-operational/its-operational-regression.trx`, `its-operational-offline.trx`, `its-fallback-directed.trx`, `its-pending-red.trx` e `smoke-final.json`. A primeira compilação alternativa do harness falhou por referência direta incompatível a Options 9/10; foi substituída pela referência de projeto com dependências transitivas alinhadas. A compilação final teve retries de cópia de DLL usada pelo testhost e concluiu; o JSON limpo foi separado dos avisos de build. Não é falha da aquisição.

```powershell
$focusedFilter = 'FullyQualifiedName~NoPonto.Tests.ItsCurrentGpsSourceTests|FullyQualifiedName~NoPonto.Tests.GpsPhysicalPredecessorTests.ItsZirixSourceTransitions'
dotnet test NoPonto/NoPonto.csproj -c Release --no-restore --filter $focusedFilter --logger 'trx;LogFileName=its-fallback-directed.trx' --results-directory artifacts/its-current-operational
$gpsClasses = @('ItsCurrentGpsSourceTests', 'GpsPhysicalPredecessorTests', 'GpsSourceAbstractionTests',
    'GpsDatarioClientTests', 'GpsSppoCollectorTests', 'GpsSppoClientTests', 'GpsPollingCadenciaTests',
    'GpsPollingFontesTests', 'GpsSignalRPublisherTests')
$gpsFilter = ($gpsClasses | ForEach-Object { 'FullyQualifiedName~NoPonto.Tests.' + $_ }) -join '|'
dotnet test NoPonto/NoPonto.csproj -c Release --no-build --no-restore --filter $gpsFilter --logger 'trx;LogFileName=its-operational-regression.trx' --results-directory artifacts/its-current-operational
$offlineFilter = & py -3.13 -B .ci/offline_tests.py filter
dotnet test NoPonto/NoPonto.csproj -c Release --no-build --no-restore --filter $offlineFilter --logger 'trx;LogFileName=its-operational-offline.trx' --results-directory artifacts/its-current-operational
py -3.13 -B .ci/offline_tests.py verify artifacts/its-current-operational/its-operational-offline.trx
```

**Antes de ativar no servidor:** concluir a homologação conectada em desenvolvimento com Redis/banco locais e observar aquisição/ACK/CAS/publicações e fontes ativas; depois autorizar a implantação/configuração. A aquisição real não escreveu no pipeline; o ensaio completo com infraestrutura local ainda não foi executado. O endpoint externo pode variar em latência/completude e o fallback reduz cobertura ao ZIRIX. Não é necessário reabrir a comparação ITS × GTFS-RT ou coletar novas linhas/dias. Sem alteração de configuração produtiva, deploy, push ou merge.

## Plano técnico

1. Implementar `ItsCurrentGpsSource : IGpsSource, ISnapshotGpsSource`. O coletor SPPO entrega o snapshot completo ao store/handoff existente; polling, ACK, matching, TTL e publicações continuam no caminho atual.
2. Reutilizar o contrato/query/DTO e a primitiva de página limitada do laboratório `feat/its-shadow-lab-cli` (0ffa286), sem importar CLI, workers ou comparações. Consultar `sistema=sppo&modo=onibus`, omitindo `servico`, `fornecedor` e `id_veiculo`. Percorrer minutos UTC atual e dois anteriores, todos os cursores, sequencialmente, com limites globais, prazo total e timeout por página.
3. Validar sistema/modal, identidade, linha, coordenadas e GPS físico com timezone explícito. Preservar identidade original, normalizada, origem, provedor, envio e servidor. Combinar ZIRIX/CONECTA por GPS físico mais recente; empate por provedor ordinal crescente (CONECTA antes de ZIRIX), ID do registro e campos do conteúdo (strings ordinais, números/instantes crescentes). Recepção não desempata. Não usar envio/servidor para fabricar frescor.
4. Separar sucesso, vazio comprovado, parcial e falha. Exigir metadados `ok` de ZIRIX e CONECTA em todas as páginas. Qualquer falha/limite/incompletude descarta o lote inteiro para publicação. Respeitar Retry-After entre ciclos e serializar leituras, inclusive I/O tardio após cancelamento. Sem fallback automático inicial: rollback pela configuração e reinício normal do processo.
5. Alterar somente contratos (nome/identidade opcional/status parcial), cliente (nova primitiva independente), nova fonte/opções, DI, testes/allowlist, este documento e histórico. Coletor/store/polling, ZIRIX, DATARIO, GTFS-RT e demais modais conservados.

## Limites iniciais

`ItsCurrent`: 3 janelas UTC de um minuto, páginas de 5000 registros, no máximo 60 páginas e 300000 registros no ciclo, 16 MiB por resposta descomprimida, intervalo mínimo de 1 s entre inícios de GET (também entre ciclos), 10 s por página e 90 s para aquisição inteira. São tetos de segurança configuráveis, não metas de latência. Por exemplo, 6000 veículos a cada 5 s geram aproximadamente 216000 registros em três minutos, antes de sobreposições entre provedores. Isso dimensiona o teto acima do laboratório de uma linha, sem afirmar volume/cobertura real da rede. Se o teto não bastar, o ciclo será explicitamente parcial, nunca truncado e publicado. Dados são reduzidos incrementalmente por identidade, sem guardar todas as páginas.

GPS máximo aceito: 180 s no término da aquisição; qualquer GPS futuro é rejeitado. Velocidade ausente vira 0; negativa/não finita invalida o registro. Velocidade finita não negativa é preservada, inclusive acima de 90 km/h, para os filtros operacionais existentes decidirem pelo limite configurado. Direção inválida vira null. Outros provedores e outros modais/sistemas são excluídos. Ausência/estado desconhecido/ambíguo de um provedor esperado significa parcial; objetos sem `data`, minuto errado, JSON inválido e lote apenas inválido/desatualizado significam falha (parcial quando uma página anterior já foi adquirida).

## Ativação e rollback (não executados)

PowerShell, para o ambiente do processo que será iniciado pelo operador:

```powershell
$env:GpsSources__BusPrimarySource = 'ITS_CURRENT'
# Iniciar/reiniciar o backend pelo procedimento habitual do ambiente.
```

Rollback:

```powershell
$env:GpsSources__BusPrimarySource = 'ZIRIX_DIRECT'
# Iniciar/reiniciar o backend pelo procedimento habitual do ambiente.
```

Em JSON, usar `"GpsSources": { "BusPrimarySource": "ITS_CURRENT" }` ou `"ZIRIX_DIRECT"`. O resolver captura opções na inicialização; troca exige novo processo. O padrão permanece ZIRIX_DIRECT; nenhuma configuração operacional foi alterada. Fallback automático fica fora desta migração inicial para evitar misturar semânticas de janela e snapshot.

## Observabilidade e limites de ativação

Um log agregado por aquisição informa estado/motivo controlados, páginas, janelas, registros, rejeições, duração, veículos, recentes <=40/60 s, p50/p90 da idade física e contribuição disponível/selecionada/exclusiva de cada provedor. Cobertura significa veículos observados, não cobertura comprovada da rede. Não registrar URLs, cursores, payloads ou veículos. Falhas não renovam GPS, TTL ou snapshot. Handoff pendente e snapshots repetidos continuam sujeitos ao ACK/deduplicação do coletor.

Antes da ativação autorizada: revisar o patch, disponibilizar o binário validado e conferir conectividade/contrato do endpoint e os limites com as métricas da aquisição de rede. Isso é homologação operacional da implementação, não outra fase de coleta comparativa por linhas/dias. Confirmar que os metadados incluem ZIRIX e CONECTA `ok`; o comportamento conservador bloqueia publicação quando não houver comprovação de completude. Nenhum deploy, acesso produtivo ou mudança de serviço faz parte desta entrega.

## Arquivos do patch

- `NoPonto/2-Application/Services/GPS/ItsCurrentGpsSource.cs`: aquisição, normalização, deduplicação, cooldown, estados e agregados.
- `NoPonto/2-Application/Services/GPS/ItsCurrentOptions.cs`: limites configuráveis e validação.
- `NoPonto/2-Application/Services/GPS/GpsDatarioClient.Bounded.cs`: página com corpo limitado e metadados Retry-After; arquivo original recebe apenas `partial`, sem mudar o método existente.
- `GpsSourceContracts.cs` e `ResultadoFonteGps.cs` no mesmo diretório: constante ITS_CURRENT, identidade original opcional e status Parcial aditivo, preservando valores do enum anterior.
- `NoPonto/Program.cs`: opções e registro singleton da fonte no resolver existente.
- `NoPonto/5-Testes/ItsCurrentGpsSourceTests.cs` e `.ci/offline-tests.json`: 44 casos novos determinísticos e inclusão na seleção offline.
- Este plano/guia e `NoPonto/HISTORICO.md`.

Nenhum ajuste necessário em `GpsSourceAdapters`, coletor, store, polling ou fontes GTFS-RT/ZIRIX/BRT. Identidade original e metadados permanecem na observação interna, já marcada JsonIgnore no DTO operacional; não se amplia o contrato HTTP/Redis/SignalR.

## Validação executada

Base: `origin/main` e HEAD `e8081f62bcd47e178c1daa40907f8c2910275a5c`, branch `feat/its-current-sppo`. Fetch e criação de branch executados; laboratório e arquivos locais preservados. Sem push/merge/commit/deploy. SDK 9.0.306, xUnit 2/VSTest, Release. Os comandos de teste compilaram o projeto sem erros, com cinco avisos anteriores (CS8981 x2, CS7022, xUnit2031 x2). Nenhuma dependência nova.

```powershell
$gpsClasses = @('ItsCurrentGpsSourceTests', 'GpsSourceAbstractionTests', 'GpsDatarioClientTests',
    'GpsSppoCollectorTests', 'GpsSppoClientTests', 'GpsPollingCadenciaTests', 'GpsPollingFontesTests')
$gpsFilter = ($gpsClasses | ForEach-Object { 'FullyQualifiedName~NoPonto.Tests.' + $_ }) -join '|'
dotnet test NoPonto/NoPonto.csproj -c Release --no-restore --filter $gpsFilter --logger 'trx;LogFileName=its-regression-final.trx' --results-directory artifacts/its-current

$offlineFilter = & py -3.13 -B .ci/offline_tests.py filter
dotnet test NoPonto/NoPonto.csproj -c Release --no-build --no-restore --filter $offlineFilter --logger 'trx;LogFileName=its-offline-final.trx' --results-directory artifacts/its-current
py -3.13 -B .ci/offline_tests.py verify artifacts/its-current/its-offline-final.trx
```

| Execução | Aprovados | Falhas | Ignorados | Resultado/artefato em `artifacts/its-current/` |
| --- | ---: | ---: | ---: | --- |
| ITS inicial | 37 | 1 | 0 | `its-directed.trx`; fixture futura de +1 s coincidia com recepção simulada +1 s; corrigida para +10 s |
| Regressão intermediária | 242 | 0 | 0 | `its-regression.trx` |
| Offline inicial | 958 | 1 | 0 | `its-offline.trx`; timeout de 3 s em ETA shadow/export_failure, BRT/batch |
| Repetição da classe ETA | 51 | 1 | 0 | `its-existing-timeout-recheck.trx`; mesmo timeout, BUS/batch |
| Controle na main intacta e isolada | 51 | 1 | 0 | `its-main-baseline-eta.trx`; mesmo timeout BRT/batch, sem código ITS |
| Regressão final | **248** | **0** | **0** | `its-regression-final.trx`, 7 s |
| Offline final | **965** | **0** | **0** | `its-offline-final.trx`, 28 s; verificador oficial aprovou todas as classes |

O controle usa cópia de `git archive origin/main NoPonto`, extraída em `artifacts/its-current/main-baseline-check`, dependências já restauradas e o mesmo comando `dotnet test ... -c Release --no-restore --filter FullyQualifiedName~NoPonto.Tests.HistoricalEtaGtfsPollingTests`. A instabilidade de ETA existe na main; não foi corrigida nem mascarada por exclusão/alteração de timeout. A seleção final continua incluindo a classe. A causa interna não foi diagnosticada conclusivamente; a reprodução diferencial delimita sua preexistência.

O sandbox bloqueou inicialmente o launcher Python e o módulo Expand-Archive; respectivas execuções autorizadas fora do sandbox concluíram. A primeira extração não produziu projeto; MSB1009 decorrente não é falha de compilação do patch. Não foram executados testes com Redis/PostgreSQL, consultas HTTP reais ao ITS, migrations, build independente da solução, frontend ou serviços do homeserver. A compilação pelo comando de testes passou. O contrato de consulta ampla foi verificado na composição real das URLs e em fixtures; seus volumes/metadados reais permanecem questão de homologação operacional.

## Matriz dos testes novos

Todos os nomes abaixo pertencem a `NoPonto.Tests.ItsCurrentGpsSourceTests`; corpo HTTP e relógio são controlados. O cenário de frota processa 35000 registros em sete páginas, acima dos tetos antigos do laboratório (30000/seis).

| Requisito | Teste |
| --- | --- |
| Rede inteira, cursor, múltiplas janelas UTC e ausência de filtro 838/provedor/veículo | `WholeNetworkTraversesAllPagesAndUtcWindowsWithoutServiceOrProviderFilter` |
| Dimensionamento além do laboratório, linhas distintas | `FleetAcquisitionExceedsOldLaboratoryCapsAndPreservesDifferentServices` |
| Duplicatas, GPS diferente/empatado, exclusividades ZIRIX/CONECTA, ordem invertida | `LatestPhysicalGpsWinsAndTiesAreIndependentOfPageOrder` |
| Empate dentro do provedor; identidade original/normalizada, linha, coordenadas, movimento e três timestamps | `SameProviderTimestampTieUsesStablePayloadAndPreservesOriginalAndSourceTimes` |
| Inválidos, futuro, antigo, timezone ausente, modal/sistema/provedor/linha, velocidade negativa/não finita | `InvalidOnlyBatchIsFailureRatherThanRealEmpty` |
| Registro válido sobrevive ao futuro inválido; direção/velocidade opcionais | `InvalidRecordsCannotSuppressValidVehicleAndOptionalMotionIsSanitized` |
| Limite operacional de velocidade preservado para downstream | `SpeedAboveOperationalLimitRemainsAvailableForExistingDownstreamFilter` |
| Vazio comprovado versus provedor ausente/pendente | `EmptyRequiresCompleteExpectedProviders` |
| Estados conflitantes do mesmo provedor | `ConflictingProviderStatusesCannotAttestCompleteness` |
| Falha intermediária HTTP, loop de cursor, páginas/registros, provedor; nenhum snapshot no coletor/polling | `IncompleteAcquisitionNeverPublishesThroughRealCollector` |
| JSON/minuto/data inválidos; limite por Content-Length e leitura do stream | `InvalidOrOversizedResponseCannotProduceSnapshot` |
| 429/503, Retry-After relativo/absoluto e recuperação | `RetryAfterSuppressesRequestsAcrossCyclesAndRecovers` |
| Timeout, orçamento, cancelamento e I/O que ignora cancelamento sem concorrência adicional | `TimeoutBudgetAndCancellationBoundNonCooperativeIo` |
| Orçamento acaba depois da primeira página, sem publicação | `BudgetDuringPaginationDropsAlreadyAcquiredRecords` |
| Intervalo mínimo entre páginas/ciclos, fronteira 999/1000 ms | `MinimumIntervalAppliesBetweenPagesAndAcrossCycles` |
| Aquisição serializada | `ConcurrentAcquisitionsAreSerialized` |
| Resolver ITS_CURRENT, snapshot real, leitura pelo polling, ACK, GPS não renovado, padrão ZIRIX | `ResolverCollectorAndPollingHandoffPreserveGpsAndPendingAckWithoutRenewal` |

A regressão direcionada também executa as classes existentes de abstração, Data.Rio, cliente/coletor SPPO, handoff e cadência; não depende de infraestrutura externa.
