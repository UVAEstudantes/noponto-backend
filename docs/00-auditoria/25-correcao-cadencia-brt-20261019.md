# Correção da amplificação de aquisição BRT

19/10/2026 — implementada e validada offline em `audit/gps-bus-brt-pipeline`. Sem deploy/acesso produtivo, alteração de TTL, matching, ETA/ML ou frontend nesta etapa.

## Auditoria preservada primeiro

Commit `a934884`: relatório 24, seis arquivos de agregados JSON/JSONL, três sondas de leitura e histórico anterior. Revisão de sintaxe, whitespace, padrões de segredos e campos de GPS bruto aprovada; TRX anterior de 613 testes conferido novamente. Não foram incluídos TRXs privados, chaves, arquivos de ambiente, payloads GPS ou WIP do frontend. As sondas foram versionadas, não reexecutadas nesta etapa.

## Causa comprovada

Há dois limitadores independentes sobre a aquisição BRT:

1. `GpsBrtPollingGate`: usa `Stopwatch`, consulta de acordo com `IntervaloBrtSegundos` e reinicia `_timestampUltimaConsulta` em toda chamada ao adaptador, mesmo se o cliente responder de cache. Guarda o último resultado de sucesso para fail-open.
2. `GtfsRealtimeGpsClient`: usa `TimeProvider.GetUtcNow()`, `_nextRequest`, último snapshot e validadores HTTP. Define o intervalo mínimo no início do GET e pode estender o prazo após receber headers, considerando `max-age`, `Age` e `Retry-After`. Reutilização/304 preserva timestamp GPS e instante de recepção do snapshot original.

Com polling 15s, gate 20s, `max-age=30`, `Age=0` e latência de headers de 1s:

| Tempo do ciclo | Antes | Depois |
|---:|---|---|
| 0s | GET; headers em 1s; cache válido até 31s | igual |
| 15s | gate reutiliza seu resultado | cliente reutiliza cache, sem GET |
| 30s | gate chama cliente; cache ainda válido; reinicia espera do gate em 30s | cliente reutiliza cache, sem GET |
| 45s | gate bloqueia; cliente já estaria elegível | GET permitido; headers em 46s; próximo prazo 76s |
| 60s | GET atrasado pela segunda espera | cliente reutiliza cache, sem GET |
| 90s | gate chama cliente que ainda tem cache até 91s; novo adiamento | GET permitido |

O defeito é a segunda espera iniciada por uma leitura que não realizou GET, e não uma falha da deduplicação ou uma cadência física de 60s do provedor.

Prova antes da mudança do algoritmo:

- `Brt_GateAndHttpCache_ReproduceAndIsolateAcquisitionAmplification`: três controles usando gate, cliente, fonte/mapper e HTTP handler reais com relógios simulados. Com os dois fatores, GETs em `0,60,120`; removendo somente a latência ou somente o Cache-Control, GETs em `0,30,60,90,120`.
- `Brt_PollingDoesNotAddGateDelayAfterHttpCacheExpires`: primeiro executado ainda com o intervalo antigo, após apenas encaminhar o timestamp já aceito pelo gate. Falhou com **esperado `[0,45,90]`, obtido `[0,60,120]`**. Nenhum algoritmo havia sido modificado nesse momento.
- Requisições foram contadas no `HttpMessageHandler`, separadas dos timestamps GPS. Assim, snapshot repetido não pode falsear a medição do GET.

Isso comprova o mecanismo no código auditado. Não prova retrospectivamente todos os eventos da imagem produtiva, cuja revisão Git não foi vinculada na auditoria anterior.

## Menor alteração funcional

`GpsPollingService.ObterBrtAsync` mantém serialização e fail-open do gate, mas passa intervalo zero **somente quando a fonte BRT é `GtfsRealtimeGpsSource`**. Nessa fonte, o cliente HTTP existente é o responsável por autorizar cada GET. Outras fontes continuam usando `IntervaloBrtSegundos`.

O método interno é usado pelo polling real e encaminha opcionalmente o timestamp já suportado por `GpsBrtPollingGate`, permitindo testar a composição sem esperas reais. Sem argumento, mantém o `Stopwatch` do gate. Nenhuma nova dependência, flag, assinatura pública, configuração ou migração.

`GpsBrtPollingGate`, cliente/parser/fonte GTFS, coletor BUS, snapshot/ACK, mapper de identidade, deduplicação, writer Lua/CAS, TTLs e matching não foram modificados. Onze arquivos protegidos têm hashes iguais aos do commit da auditoria, registrados no [resumo de validação](gps-brt-cadence-validation-20261019.json).

Chamar o adaptador em cada ciclo de 15s **não significa fazer GET a cada 15s**. `_nextRequest`, headers e rate limit continuam no cliente sem alteração. Tampouco transforma 304 ou GPS repetido em posição nova. A semântica do contador `brt_consultas_http` continua sendo chamada do gate ao adaptador, não contador de tráfego real.

## Intervalos e limites observados nos testes

| Cenário | Resultado verificado |
|---|---|
| Antes: gate 20, polling 15, max-age 30, Age 0, headers +1s | GETs `0,60,120`: intervalo 60s |
| Depois: mesmos parâmetros | GETs `0,45,90,135`: intervalo 45s |
| Depois: headers sem latência, max-age 30 | GETs `0,30,60,90,120,150`: intervalo 30s |
| Depois: max-age 60, Age 0, headers +1s | GETs `0,75,150`: intervalo 75s, respeitando prazo 61s |
| Depois: max-age 60, Age 30, headers +1s | GETs `0,45,90,135`: Age reduz frescor, rate limit permanece |
| Fronteira, BUS e BRT | sem GET em 30,999s; GET em 31s; timestamp novo preserva identidade |
| ETag/304, BUS e BRT | GETs `0,45,90`; 304 em 45s preserva GPS/recepção e ETag original; 200 em 90s avança GPS |
| BRT: 503 sem Retry-After | GETs `0,45,75`; cache fail-open preserva GPS até recuperação |
| BRT: 503 com Retry-After 60 | GETs `0,45,120`; nenhum GET antes do prazo de retry |
| BRT: falha inicial | GETs `0,30`; ciclo 15 sem nova tentativa; nenhuma posição inventada |
| BRT legado, gate 20/polling 15 | GETs `0,30,60`, comportamento anterior preservado |
| BUS, coletor consultado em passos 11s | GETs `0,33,66,99`; lote pendente impede nova coleta até ACK |
| BUS: falha/recuperação | GETs `0,45,75`; fonte sinaliza falha sem nova tentativa antecipada |
| BUS e BRT: 200 repetindo GPS, ativo presente ou ausente | HTTP pode atualizar, mas polling real executa zero matching/CAS; watermark/ativo barram duplicata e payloads ativo/recente permanecem idênticos |

**Não se promete aquisição exatamente a cada 30s.** No cenário com headers em 1s, o cliente exige esperar até 31s e o polling de 15s oferece a próxima oportunidade em 45s. Alterar essa política do cliente ou criar um novo agendador não é necessário para remover a espera extra do gate e poderia afetar BUS. Nenhuma dessas mudanças foi feita.

## Evidência por requisito

Todos os testes novos pertencem à classe já selecionada em `.ci`, `NoPonto.Tests.GpsPollingCadenciaTests`, em novo arquivo parcial `GpsPollingCadenciaTests.Gtfs.cs`.

| Requisito | Testes / evidência |
|---|---|
| Reproduzir e isolar 60s | `Brt_GateAndHttpCache_ReproduceAndIsolateAcquisitionAmplification`; TRX de reprodução com 3 aprovados |
| Falhar antes e passar após correção no polling | `Brt_PollingDoesNotAddGateDelayAfterHttpCacheExpires`; TRX red com 1 falha esperada e matriz final com 5 aprovados |
| Respeitar cache, Age e ETag/304 | `Brt_PollingDoesNotAddGateDelayAfterHttpCacheExpires`, `Gtfs_HttpFreshnessBoundaryAndUpdatePreserveIdentity`, `Gtfs_NotModifiedPreservesGpsAndValidatorUntilNextUpdate` |
| Falha HTTP, retry e ausência inicial | `Brt_HttpFailurePreservesLastGpsAndHonorsRetryAfter`, `Brt_StartupFailureDoesNotInventPositionOrRetryBeforeRateLimit`, `Bus_HttpFailureAndRecoveryKeepClientRateLimit` |
| BUS e fonte BRT antiga preservados | `Bus_CollectorKeepsHttpCadenceAndSingleSnapshotUntilAck`, `Brt_LegacySourceKeepsConfiguredGateInterval` |
| Identidade e timestamp novo/repetido | `Gtfs_HttpFreshnessBoundaryAndUpdatePreserveIdentity`, `Gtfs_NotModifiedPreservesGpsAndValidatorUntilNextUpdate` |
| Deduplicação antes de matching/CAS, ativo ou watermark | `Gtfs_RepeatedGpsInRealPollingNeverReachesMatchingOrCas`, quatro casos BUS/BRT com/sem ativo; código CAS/Lua inalterado |
| Escopo e demais garantias | diff restrito a um arquivo funcional, testes e documentos; 635 testes offline aprovados e manifest/TRX verificados |

Os doubles não reimplementam o CAS: uma escrita indevida no teste do polling falha imediatamente. O teste exerce o filtro real e o caminho do polling, com cache de memória e watermark controlado. Não foi executado Redis real; as garantias transacionais do Lua não são recertificadas por esse teste, e seu código permanece intacto.

## Execução e resultados

SDK 9.0.306, xUnit/VSTest 17.14.1. HTTP interceptado em memória, sem sockets externos; relógios UTC e monotônico avançados sem `sleep`/`Task.Delay` de cadência. A âncora UTC da fixture apenas mantém os payloads dentro da janela de validade já existente da fonte; os intervalos comparados são controlados.

1. Reprodução antes do algoritmo: 3 aprovados; regressão desejada: 1 falha esperada com coleção `0,60,120`.
2. Suíte dirigida após correção: **40 aprovados, zero falhas/ignorados**, 22 novos casos em dez métodos. Um erro inicial de compilação na fixture (`MemoryDistributedCache` não implementa IDisposable) foi corrigido antes da rodada limpa.
3. `dotnet build NoPonto.sln -c Release --no-restore --no-incremental --verbosity quiet`: **zero erros, cinco avisos preexistentes**, dois CS8981, um CS7022 e dois xUnit2031.
4. Seleção oficial obtida por `.ci/offline_tests.py filter`; `dotnet test NoPonto/NoPonto.csproj -c Release --no-build --no-restore --filter $auditCadenceFilter --logger 'trx;LogFileName=brt-cadence-offline-release.trx' --results-directory NoPonto/lab-output/gps-brt-cadence-fix-20261019 --verbosity quiet`: **635 aprovados, zero falhas/ignorados**, execução dos testes aproximadamente 4s.
5. `.ci/offline_tests.py verify` aprovou o TRX Release: todas as classes da allowlist e nenhum teste fora da seleção. TRXs de reprodução, red, dirigida e final foram parseados; contagens, falha esperada e SHA-256 estão no resumo sanitizado versionado. Os TRXs completos permanecem em `NoPonto/lab-output/gps-brt-cadence-fix-20261019/`, ignorado pelo Git.

Nenhum teste conectado, Docker, SSH, API pública, migration, startup produtivo, instalação, treino ou teste frontend foi executado nesta etapa. As alterações WIP do frontend não foram acessadas ou modificadas.

## Riscos restantes e próxima etapa

- Latência, fase do polling, headers, 304 e Retry-After continuam determinando intervalos efetivos. Repetição legítima de GPS não é corrigida fabricando timestamps.
- **45s ainda pode exceder o TTL ativo de 40s.** Disponibilidade de predecessor/ETA BRT não está resolvida nem foi prometida. Recomenda-se tratar a retenção da evidência física separadamente do TTL de presença visual, em etapa autorizada própria.
- O adaptador GTFS BRT é avaliado mais vezes, inclusive para revalidar idade de posições. O rate limit HTTP permanece, mas falhas de crosswalk podem ser tentadas com maior frequência. Homologação posterior deve observar custo de adaptação/lookup e distribuição de idade/predecessor.
- Os contadores do gate podem crescer sem crescimento correspondente de GETs; usar logs de snapshots e, quando disponível, contador de requisição HTTP apropriado. A telemetria histórica de 10% não substitui esse denominador.
- A imagem produtiva e o APK continuam sem homologação dessa correção. Ela não foi implantada; a expectativa antes/depois é evidência determinística de código, não nova medição produtiva. Permanecem os demais achados do relatório 24, inclusive expiração visual, crosswalk e estado operacional.
