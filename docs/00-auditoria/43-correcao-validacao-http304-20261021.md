# Correção dos bloqueios HTTP 304 da revisão 40

Os bloqueios P1/P2 foram reproduzidos na suíte padrão e corrigidos localmente. Resultado final: **238/238 direcionados e 847/847 offline**, sem falhas ou ignorados. O build completo da solução não iniciou por rejeição da política de execução. A implantação continua condicionada a esse build, à conferência de procedência/imagem e à homologação operacional previstas no plano 41. Nenhum deploy foi realizado.

Este relatório complementa os relatórios 38–40; não altera suas evidências históricas. Os planos [41](41-plano-manual-aquisicao-gtfsrt-20261021.md) e [42](42-diagnostico-readonly-homeserver-20261021.md) permanecem byte a byte preservados, inclusive comandos, critérios e rollback. Evidência estruturada: [gtfsrt-304-fix-validation-20261021.json](gtfsrt-304-fix-validation-20261021.json).

## Causas e correção mínima

**P1:** o cliente usava somente o status 304 e a existência de sucesso anterior para aceitar a revalidação. Atualizava o ETag sem identificar o corpo correspondente. O teste permanente `DifferentStrongEtagOn304MustNotRelabelOldBody` reproduziu `Success=true` para ETag forte divergente.

Agora `IdentifiesStoredResponse` exige corpo completo retido e identifica sua representação antes de modificar metadados. ETag forte exige comparação forte, inclusive distinção entre maiúsculas/minúsculas; ETag fraco usa comparação do valor opaco. Um ETag forte recebido não promove um ETag fraco armazenado. Sem ETag recebido, Last-Modified deve corresponder ao armazenado; sem qualquer validador recebido, a única representação também deve carecer de validadores. ETag incompatível não é contornado por Last-Modified coincidente. É uma estratégia conservadora para este cliente de representação única, sem inferir força adicional de Last-Modified a partir de relógios de intermediários.

Um 304 incompatível retorna falha `http_304_validator_mismatch`, sem snapshot, e usa a limpeza de corpo/validadores já existente no patch 39. Não herda a política de frescor desse 304. O próximo GET elegível solicita uma representação completa; não existe retry imediato. Retry-After é aplicado antes dessa recusa e pode prolongar a espera. Referência: [RFC 9111 §4.3.4](https://www.rfc-editor.org/rfc/rfc9111.html#section-4.3.4), [comparação de validadores, RFC 9110 §8.8](https://www.rfc-editor.org/rfc/rfc9110.html#section-8.8).

**P2:** o operador de fallback aplicado a `cachePolicy.MaxAge` confundia omissão do campo inteiro com omissão de uma diretiva dentro de um campo substituído. O teste permanente `ReplacingCacheControlOn304MustNotRetainRemovedMaxAge` reproduziu somente dois GETs até t=90, em vez de 0/60/90.

Agora só a ausência integral de Cache-Control em um 304 válido conserva a política anterior. Campo presente substitui os valores tratados pelo cliente: ausência de max-age limpa o prazo anterior, e ausência de no-cache remove a exigência anterior. No-cache exige validação antes de reutilização local. No-store descarta corpo/validadores e não acrescenta espera de frescor para um corpo que não pode ser reutilizado, inclusive quando acompanhado de max-age. O mínimo configurado entre GETs e Retry-After permanecem obrigatórios. Referência: [RFC 9111 §3.2](https://www.rfc-editor.org/rfc/rfc9111.html#section-3.2).

## Arquivos desta etapa

- `NoPonto/2-Application/Services/GPS/GtfsRealtimeGps.cs`: gate de correspondência e substituição correta da política; Retry-After antecipado para também proteger a recusa do 304.
- `NoPonto/5-Testes/GpsPollingCadenciaTests.Http304.cs`: 37 casos permanentes na parcial já incluída na seleção offline, incluindo os dois REDs da revisão.
- `NoPonto/5-Testes/GtfsRealtimeGpsTests.cs`: a fixture de 304 válido passa a fornecer seu ETag correspondente; assertions preservadas.
- `NoPonto/5-Testes/GpsPollingCadenciaTests.BusAcquisition.cs`: a fixture de 304/no-store válido passa a fornecer ETag correspondente ao corpo inicial, em vez do ETag automático do segundo request; assertions preservadas.
- `NoPonto/HISTORICO.md`: entrada acrescentada, preservando o conteúdo anterior por prefixo.
- Este relatório e o JSON de validação.

Os testes externos em `40-review-304-cases.cs.txt` permanecem como evidência histórica. Sua execução não é necessária: os dois cenários agora são métodos de `GpsPollingCadenciaTests` compilados por padrão, sem imports MSBuild, alteração de csproj ou alteração da allowlist.

## RED/GREEN e cobertura

| Requisito | Evidência permanente |
| --- | --- |
| P1: não associar validador novo a corpo antigo | `DifferentStrongEtagOn304MustNotRelabelOldBody`: RED Assert.False recebeu true; GREEN também confere recuperação com GPS posterior, GET completo e inícios 0/30/60. |
| P2: remover max-age substituído por no-store | `ReplacingCacheControlOn304MustNotRetainRemovedMaxAge`: RED esperava 3 GETs, recebeu 2; GREEN confere GET completo em t=90. |
| Forte/fraco, divergência, Last-Modified e nenhum validador | `Gtfs304_ValidatorsMustIdentifyTheRetainedRepresentation`: 18 combinações, incluindo case sensitivity, proibição de promoção weak→strong e divergência de ETag com Last-Modified igual. |
| Campo ausente versus política substituída | `Gtfs304_CacheControlReplacementUsesOnlyTheReceivedPolicy`: 6 políticas, prazo imediatamente anterior/igual, retenção/expulsão de validadores. `Gtfs304_NewPolicyCanRemoveNoCacheRequirement` cobre remoção de no-cache. |
| Retry-After e intervalo mínimo | `Gtfs304_InvalidAssociationAndNoStorePreserveRetryAfter`: 4 casos, absoluto/relativo, 304 incompatível/válido no-store; nenhum GET antes do prazo. |
| Recuperação BUS/BRT, corpo ausente e metadados incompatíveis | `Gtfs304_UnassociatedMetadataCannotPostponeFullRecovery`: 4 casos, max-age=300 no 304 recusado não adia aquisição completa em t=60. |
| Concorrência e cancelamento em fila durante 304 | `Gtfs304_QueuedCancellationDoesNotInvalidateSerializedValidation`: BUS/BRT, cancelamento determinístico de um leitor em fila e 12 leitores simultâneos; um único GET de revalidação, mesmo snapshot e recepção. |

Execuções locais, em Release, sem restore:

| Execução | Aprovados | Falhas | Ignorados |
| --- | ---: | ---: | ---: |
| Dois testes permanentes antes da correção | 0 | 2 | 0 |
| Primeira rodada direcionada após correção, antes dos 4 casos adicionais de recuperação | 234 | 0 | 0 |
| Direcionados finais, incluindo regressões 38/39 | 238 | 0 | 0 |
| Seleção offline oficial final | 847 | 0 | 0 |

O acréscimo final é de 37 casos sobre os 201/810 anteriores. Os conjuntos se sobrepõem, não devem ser somados. A verificação oficial `.ci/offline_tests.py verify` confirmou o TRX e todas as classes presentes. A comparação das seleções com os TRXs da revisão anterior confirma a preservação dos 201 e 810 casos prévios, além dos novos testes. Cinco avisos preexistentes de compilação, sem novos avisos ou erros na compilação do projeto.

Artefatos locais em `NoPonto/lab-output/gtfs-304-fix-20261021/`: `304-red.trx`, `304-directed.trx`, `304-directed-final.trx`, `304-offline.trx`. O JSON registra hashes/contadores e presença dos dois métodos na seleção padrão final.

Comandos executados:

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore -c Release --filter 'FullyQualifiedName~DifferentStrongEtagOn304MustNotRelabelOldBody|FullyQualifiedName~ReplacingCacheControlOn304MustNotRetainRemovedMaxAge' --logger 'trx;LogFileName=304-red.trx' --results-directory NoPonto/lab-output/gtfs-304-fix-20261021

dotnet test NoPonto/NoPonto.csproj --no-restore -c Release --filter 'FullyQualifiedName~NoPonto.Tests.GpsPollingCadenciaTests.|FullyQualifiedName~NoPonto.Tests.GtfsRealtimeGpsTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoCollectorTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoClientTests.|FullyQualifiedName~NoPonto.Tests.GpsPollingFontesTests.' --logger 'trx;LogFileName=304-directed-final.trx' --results-directory NoPonto/lab-output/gtfs-304-fix-20261021

$offlineFilter = python .ci/offline_tests.py filter
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test NoPonto/NoPonto.csproj --no-restore --no-build -c Release --filter $offlineFilter --logger 'trx;LogFileName=304-offline.trx' --results-directory NoPonto/lab-output/gtfs-304-fix-20261021
python .ci/offline_tests.py verify NoPonto/lab-output/gtfs-304-fix-20261021/304-offline.trx
```

O primeiro comando reproduziu RED contra a implementação anterior; contra a implementação atual seu resultado esperado é GREEN. Nenhum import temporário está envolvido.

Tentativa explícita também realizada:

```powershell
dotnet build NoPonto.sln --no-restore --no-incremental -c Release
```

**Não iniciou.** A revisão automática de aprovação rejeitou CreateProcess: `approval required by policy, but AskForApproval is set to Never`. Não houve execução do MSBuild, nem tentativa de contornar a política. A compilação do projeto realizada por `dotnet test` passou; não substitui aprovação do build independente da solução.

## Revisão de segurança, preservação e riscos restantes

Toda leitura/mutação de validadores, corpo e prazos continua dentro do mesmo SemaphoreSlim. A espera cancelada antes da aquisição do gate não invalida a operação ativa. Não foram acrescentados awaits, campos de estado, filas, buffers ou caches; permanecem os limites de corpo/entidades. Cancelamento ativo, timeout interno/externo, TLS e corpo parcial continuam cobertos pelas regressões do relatório 39. A recusa de 304 passa pelo mesmo caminho de falha que impede sucesso antigo com aparência de aquisição nova.

304 válido mantém a mesma instância de snapshot, ReceivedAtUtc e timestamps GPS; não significa download nem movimento novo. 304 incompatível registra aquisição falha, sem snapshot. Chamadas durante a espera retornam falha reutilizada, sem GET. No-store permite o retorno da revalidação atual e impede retenção para a próxima chamada. O cache operacional de fallback BRT e seus logs explícitos permanecem com a semântica anterior, sem renovar GPS físico.

`GtfsRealtimeGpsSource.cs`, a parcial de outages e a parcial de cadência anterior estão byte a byte preservados. Parser/opções GTFS, TTLs, deduplicação, persistência, Redis/PostgreSQL, SignalR, timeout, flags, coletor/store/polling, startup, projeto e infraestrutura não foram modificados. Hashes dos 16 arquivos protegidos da revisão 40 foram reconferidos. Histórico anterior mantido por prefixo; snapshot dos 15 arquivos locais iniciais preservado em pasta temporária exclusiva para esta etapa. Nenhum reset, stash, commit, push ou merge.

A recusa conservadora de validadores incompletos/incompatíveis pode exigir um corpo completo no próximo GET permitido e uma indisponibilidade transitória até esse momento. Os prazos indevidos deixam de atrasar esse GET, sem aumentar o teto configurado: mínimo de 30s significa até dois GETs/min/modal/processo; réplicas e sondas externas somam carga. Não há retry nem sonda adicional nesta etapa.

Nenhum novo bloqueio funcional foi encontrado nos testes e na revisão local. **Os dois bloqueios P1/P2 estão resolvidos; permanecem pendentes o build completo autorizado e a procedência/imagem, conectividade DNS/TLS/transferência e medição produtiva previstas em 41/42.** Este patch está pronto para revisão técnica, sem autorização de implantação. Não foram acessados fonte/REST produtivos, homeserver, containers, bancos ou redes de produção. Nenhum comando de deploy, diagnóstico do servidor, rollback ou ativação automática foi executado.
