# Matching GPS em lote: diagnóstico por fase

**Entrega instrumental, sem otimização especulativa.** Branch `perf/gps-batch-phase-diagnostics`, criada de `origin/main` após fetch, base `1fcbad48b47faa0ef874ade5f338c2d91594710f`. Árvore inicial limpa; correções 38–44 e branch anterior preservadas. Produção #138 e seus números são informações fornecidas pelo usuário, sem acesso ao servidor nesta tarefa.

## Conclusão e oportunidade

Os 17 ciclos informados têm medianas de 14,37s total, 8,72s matching, 2,79s commit/viagens e 0,91s Redis inicial, com 2.206–3.118 posições novas. A participação informada de 62,4% e a razão das medianas (60,7%) são agregações distintas; não permitem inferir o custo de SQL ou de conexão. Handoff mediano de 1ms e um overrun de 16,81s com repouso de 5s não foram reavaliados em produção.

O código em `GpsEnriquecimentoService.Batch.cs` prepara IDs/TCS e aplica duas barreiras, mantendo grupos simple/combined/directed e correlação por InputId. O repository filtra, serializa JSON e executa chunks sequenciais de 100 por padrão; abre/retorna conexão por comando e materializa antes de liberar a decisão. Score, faixa, histerese, temporal, próximo ponto/ocorrência e fallback estão fora do escopo de alteração. `CombinedSetBased.cs` já materializa contextos e reutiliza primitivas no GLOBAL via LATERAL/OFFSET 0; os ramos anterior/operacional e próxima parada também têm custo espacial.

O perfil local anterior `NoPonto/lab-output/postgis-matching-final-profile.md` apontava CPU/PostGIS no GLOBAL, com catálogo real e inputs reconstruídos, sem representar a distribuição produtiva. Isso torna SQL espacial uma hipótese prioritária para investigação, mas não prova a maior oportunidade na #138. O motor efetivamente selecionado, espera por conexão, transferência e materialização atuais não vieram separados na evidência produtiva. A instrumentação agora expõe inclusive `engine=Legacy/SetBased`.

**Decisão:** não alterar SQL, tamanho de chunk, pool, paralelismo, motor ou fallback. As fixtures PostGIS disponíveis exigem conexão explícita e criam schema; não foi criado/conectado banco para esta investigação. Sem corpus espacial/catalogo equivalente e planos atuais, não há ganho seguro a demonstrar. A maior oportunidade comprovada nesta etapa é fechar a lacuna de atribuição antes de escolher a otimização.

## Patch e significado das medidas

Um acumulador de duração monotônica por operação, com sete contadores fixos, agrega todos os chunks e emite um log ao sair, inclusive em falha/cancelamento. O stage emite um resumo adicional. No polling atual são até três resumos de repository e um de stage, não um log por posição/chunk. Dimensões fixas: operação Global/Combined/Directed/Stage, motor e outcome. Quantidades/durações são valores escalares; nenhum veículo, linha, padrão, SQL, parâmetro, URL ou conexão aparece no resumo.

| Campo | Fronteira medida e limite |
| --- | --- |
| `prepare_ms` | No repository: filtros, JSON e configuração de comando entre checkpoints; a validação de argumentos e estruturas iniciais anteriores ao acumulador não estão incluídas. No stage: IDs/TCS/executor, antes das decisões assíncronas. Não é CPU puro. |
| `connection_ms` | Await de `OpenConnectionAsync`: checkout/pool ou conexão física conforme Npgsql, sem presumir qual ocorreu. |
| `execute_reader_ms` | Await de `ExecuteReaderAsync`; inclui envio, protocolo, servidor, rede/buffering. **Não é SQL puro.** |
| `read_ms` | Awaits de `ReadAsync`, incluindo EOF. Pode incluir rede, buffering e trabalho pendente do servidor. |
| `materialize_ms` | Acesso/decodificação de campos, `LerRota`, montagem de resultados e dicionários por linha; tempo de parede .NET, não medição de CPU de thread. |
| `fallback_ms` | Tratamento de falha, circuito/sonda/fallback individual, sem atribuir suas consultas às fases nominais do batch. |
| `other_ms` | Repository: limpeza/Dispose, administração e montagem final entre checkpoints. Stage: decisões/barreiras/awaits e processamento restante; contém espera pelos repositories e **não deve ser somado aos seus logs**. |
| `wall_ms` | Soma disjunta das fases dentro do mesmo acumulador, até sua finalização, excluindo a emissão do próprio log. Não somar stage e repository, nem interpretar somas de operações concorrentes como latência do ciclo. |

`commands`/`fallback_commands` mantêm a definição dos contadores existentes; `rows` conta linhas iniciadas na materialização, não veículos aceitos; `chunk_failures` registra entradas no catch operacional. `Completed` significa retorno normal do método, não matching Found; `Degraded` distingue retorno após falha de chunk; `Failed`/`Cancelled` preservam saídas interrompidas. Cancelamento posterior à conclusão não relabela o retorno como Cancelled. Cancelamento já solicitado antes de iniciar o acumulador continua propagando sem resumo novo. Em erro, o último intervalo pode incluir limpeza assíncrona até o catch/finally; excluir essas amostras de comparações nominais.

Npgsql lê para buffer e pode decodificar em getters, conforme a [documentação de desempenho](https://www.npgsql.org/doc/performance.html). A separação cliente não substitui Planning/Execution do servidor e não permite chamar toda a duração de ExecuteReader de SQL. O [EXPLAIN do PostgreSQL](https://www.postgresql.org/docs/16/using-explain.html) oferece a análise de planos necessária para essa próxima etapa. Nenhuma configuração de driver/banco foi modificada.

Com Information desabilitado, não são tomados timestamps nem emitido resumo; os checkpoints permanecem no caminho. Falha do provedor de logging é isolada no diagnóstico e não provoca fallback operacional. TimeProvider interno controla somente o contador diagnóstico em testes; não muda timestamps GPS, timeouts ou DI público. Nenhum estado por veículo, tarefa HTTP/SQL ou paralelismo foi acrescentado.

## Provas e medições

| Requirement | Evidence |
| --- | --- |
| Instrumentar os três repositories e o stage | `PhaseDiagnostics_RepositoryReportsEmptyBatchWithoutConnecting` (3 casos) e `PhaseDiagnostics_StageReportsSetupSeparatelyWithoutChangingResults`: RED 0/4 por ausência dos resumos; GREEN. Casos sem bearing demonstram zero conexão/comando, não custo PostGIS. |
| Separação monotônica exata e sem dupla contagem | `PhaseDiagnostics_MonotonicPhasesAreDisjointAndEmitOnce`: preparação 7ms, conexão 3ms, ExecuteReader 7ms, leitura 28ms, materialização 13ms e restante 19ms, total 77ms, com relógio sintético. Não são tempos de banco. |
| Falha/cancelamento preservados | `PhaseDiagnostics_FailureAndCancellationPreserveOriginalControlFlow`: seis casos nos três repositories; hooks preexistentes interrompem antes de conexão, verificando exceção original, ausência de fallback ao cancelar e tempos parciais. `PhaseDiagnostics_CompletedFallbackRemainsExplicitlyDegraded` cobre resumo degradado. |
| Isolamento e observabilidade opcional | `PhaseDiagnostics_ConcurrentOperationsKeepIndependentClocksAndAggregates` (32 acumuladores) e `PhaseDiagnostics_DisabledOrBrokenLoggerCannotChangeMatching` (2 casos). |
| DTO, diagnóstico, ordenação e fallback equivalentes | `PhaseDiagnostics_HeavyOfflineCorpusKeepsFullDtoDiagnosticsOrderAndFallback`: 2.206/3.118 entradas, normal/outage simulado, duas gerações com identidades novas e continuidade, respostas invertidas. Comparação exata de JSON completo, incluindo todos os campos de rota/sentido/padrão/versão, projeção, bearing, parada, timestamps e diagnóstico; contadores/circuito/fallback iguais. Fakes não comprovam equivalência espacial no PostGIS. |

O A/B abaixo mede o mesmo stage .NET sobre fakes, diagnóstico desligado/ligado **na mesma revisão**, após aquecimento e seis pares alternados. Exclui criação da fixture e primeira geração; inclui saída do método e resumo em memória. É controle do observador, não ganho antes/depois de uma otimização. Não mede os checkpoints por linha Npgsql, rede ou SQL. Tem GC/escalonamento/JIT e não possui assertion de limite de desempenho.

| Entradas | Observador desligado, mediana | Ligado, mediana | Setup instrumentado, mediana |
| ---: | ---: | ---: | ---: |
| 2.206 | 11,71525ms | 13,0949ms | 1,017ms |
| 3.118 | 20,70535ms | 18,21115ms | 1,835ms |

Fonte: `phases-final-check.trx`; amostras brutas no JSON. Há outliers e medianas invertidas entre as rodadas (a anterior registrou 9,9382→10,5312ms e 19,3754→19,68755ms); nenhuma dessas variações foi apresentada como aceleração ou overhead produtivo certificado. Os resumos da rodada offline podem diferir por carga do processo. **SQL puro, conexão e materialização reais da #138 continuam não medidos separadamente. Não há ganho de produção comprovado.**

Validação final: **411/411 direcionados e 883/883 offline oficiais**, zero falhas/ignorados. São 864 casos offline anteriores mais 19 novos, em parcial já incluída na allowlist. Baseline de orquestração 39/39. Cinco avisos de compilação preexistentes. Um filtro intermediário incluiu cinco testes `GpsMatchingDifferentialHarnessTests` com PostGIS: falharam no guard por falta de `POSTGIS_TEST_CONNECTION`, antes de conexão/DDL. Foram registrados como bloqueados, não enfraquecidos nem transformados em skip; seleção final corrigida. Uma falha inicial de compilação de fixture por argumento Faixa e um aviso de assertion foram corrigidos antes do RED funcional. A revisão final reproduziu RED adicional no rótulo diagnóstico de cancelamento tardio; a precedência foi corrigida, e ambas as seleções foram repetidas após esse ajuste.

Comandos finais (artefatos em `NoPonto/lab-output/matching-phases-20261021/`):

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore -c Release --filter 'FullyQualifiedName~NoPonto.Tests.GpsPerformanceMetricsTests.|FullyQualifiedName~NoPonto.Tests.GpsMatchingBatchOrquestracaoTests.|FullyQualifiedName~NoPonto.Tests.GpsEnriquecimentoServiceTests.|FullyQualifiedName~NoPonto.Tests.GpsPollingCadenciaTests.|FullyQualifiedName~NoPonto.Tests.GtfsRealtimeGpsTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoCollectorTests.|FullyQualifiedName~NoPonto.Tests.GpsPollingFontesTests.' --logger 'trx;LogFileName=phases-final-check.trx' --results-directory NoPonto/lab-output/matching-phases-20261021
$offlineFilter = python .ci/offline_tests.py filter
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test NoPonto/NoPonto.csproj --no-restore --no-build -c Release --filter $offlineFilter --logger 'trx;LogFileName=phases-offline-final.trx' --results-directory NoPonto/lab-output/matching-phases-20261021
python .ci/offline_tests.py verify NoPonto/lab-output/matching-phases-20261021/phases-offline-final.trx
```

Build solicitado `dotnet build NoPonto.sln --no-restore --no-incremental -c Release`: **não iniciou**, rejeitado pela revisão automática: `approval required by policy, but AskForApproval is set to Never`. A compilação Release do projeto pelos testes passou; não substitui o build independente da solução.

## Próxima medição e condição para otimizar

1. Em etapa separada e autorizada, preparar fixture descartável já isolada, com versão PostgreSQL/PostGIS, catálogo, estatísticas e corpus anonimizados/fingerprints equivalentes; nunca apontar `POSTGIS_TEST_CONNECTION` para banco real. Os runners existentes de benchmark/EXPLAIN/validação representativa não foram ativados aqui.
2. Reproduzir 2.206 e 3.118 entradas, distribuição natural/concentrada/dispersa, mistura de novos/continuidade/operacional, inelegíveis, circular, empates, paradas repetidas e versões pinadas. Conservar chunk, motor, pool, paralelismo e ambiente. Registrar contagens por modal e candidatos sem criar dimensão por identidade.
3. Aquecer e executar pelo menos sete pares alternados baseline/candidato com mesma carga. Guardar p50/p95, fases nominais por operação/motor, linhas/comandos/fallback, alocações/GC, CPU e waits/pool. Comparar observer ligado/desligado para medir também os checkpoints Npgsql e emissão real do log. As fases assíncronas são tempo de parede; CPU exige perfil próprio.
4. Na mesma fixture, obter `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` das SQLs já extraídas pelos harnesses, sem alterar settings ou SQL produtivos. Separar Planning/Execution, loops/rows, buffers/I/O e transferência. Identificar a fase dominante atual antes de propor materialização adicional, reuse de conexão ou concorrência. Não assumir flags efetivas em produção a partir de defaults locais.
5. Exigir zero diferenças de identidade, elegibilidade, nulls, ramo operacional, ordenação e fallback; usar o contrato numérico já existente no harness, sem ampliar tolerância. Reexecutar corpus diferencial, cancelamento/outage e regressões operacionais. Só aceitar otimização com redução repetível de tempo para carga equivalente e sem regressão de p95/carga/consistência. Métricas coletadas em falha/Degraded devem permanecer separadas.

Arquivos alterados: duas inserções instrumentais nos fontes batch existentes; novo `MatchingBatchPhaseDiagnostics.cs`; `GpsPerformanceMetricsTests.cs` tornado partial e nova parcial `BatchPhases`; HISTORICO acrescentado, este relatório e JSON de evidências. Não foram alterados contratos HTTP/de matching, `CombinedSetBased.cs`, SQLs/serialização, decisões, CAS, deduplicação, PhysicalETA, viagens, TTLs, Redis, SignalR, configuração, driver, timeouts, projetos, infraestrutura, Docker ou frontend.

A reconstrução dos dois fontes, retirando somente as instruções instrumentais e normalizando o retorno equivalente, coincide exatamente com a baseline. SQLs e todas as chamadas Npgsql existentes permanecem na mesma quantidade/ordem; os demais arquivos protegidos e relatórios 38–44 foram conferidos por hash. Isso sustenta a equivalência estrutural do patch, sem substituir execução espacial contra fixture real. Riscos restantes: overhead por linha/log e vazão real ainda não medidos no driver; fases em falha têm limpeza inclusiva; resumos são multimodais e devem ser correlacionados por processo/ciclo com logs já existentes, sem somar latências concorrentes.

**Pronto para revisão da instrumentação; bloqueados o build completo e a atribuição/equivalência PostGIS representativa antes de qualquer otimização futura.** Sem acesso ou alteração de produção/banco real, servidor, Docker, configuração, commit, push, PR, merge ou deploy. Evidências e hashes: [matching-phase-validation-20261021.json](matching-phase-validation-20261021.json).
