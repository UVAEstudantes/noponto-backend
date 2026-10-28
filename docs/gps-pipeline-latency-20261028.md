# Latência do pipeline GPS — correção local após release 153

Base: `6babc1036d7467af399899a0d1596144a708e43d`, branch `feat/its-current-sppo`.

## Resultado

Implementada uma otimização da query de matching combinado **SetBased**: calcular o comprimento geográfico até a próxima parada uma vez por versão/ocorrência escolhida, dentro do mesmo statement, em vez de repetir o cálculo por veículo e ramo global/anterior. A projeção e a seleção da parada continuam individuais. Não existe cache entre chunks ou gerações.

No PostGIS 16 descartável local, uma regressão com100 veículos distintos e200 ramos executava402 chamadas de `ST_Length` antes; agora executa203. As saídas foram preservadas. Mediana do repositório .NET para300 posições/3 chunks: **1.839,4 → 1.206,4ms**. Não é uma previsão percentual de frescor em produção.

Também foram acrescentados agregados de idade física no log GTFS existente, sem mudar aquisição, cache, timestamp ou seleção. Não houve SSH, API externa, publicação, deploy ou janela nesta tarefa.

## O que os marcadores da release153 realmente medem

A correlação por geração898 foi preservada como evidência histórica, sem repetir o experimento:

|Trecho|Mediana observada|Interpretação e limite|
|---|---:|---|
|Seleção → polling|0,192s|Não foi a principal fila após snapshot pronto nesta amostra.|
|Seleção → `matched`|11,517s|Barreira da frota inteira; inclui leitura de contextos, matching, diagnóstico estrutural e ETA. O marcador fica depois destes últimos, portanto não significa somente CPU C# ou SQL.|
|Seleção → `cas_accepted`|14,821s|O método de confirmação aguarda CAS GPS **e** viagem/histórico/retry por veículo antes de concluir a barreira. Não é latência isolada do Lua/CAS.|
|Seleção → SignalR concluído|15,123s|Inclui montagem dos índices/visibilidade e publicação após confirmação.|
|Seleção → REST observado|24,531s|Inclui cadência e fase da sonda; não é tempo puro do endpoint.|

Não se deve subtrair medianas independentes para obter uma mediana de estágio. A geração2 demonstra diretamente seleção→CAS15,42s e seleção→REST18,72s. O polling primeiro elimina GPS repetido/antigo, lê estados/predecessores, prepara contextos e então espera o matching de **todos** os candidatos. Os chunks SQL são sequenciais e o CAS/publicação continuam depois dessa barreira, com um único escritor.

Nos ciclos arquivados havia matching até11,43s, commit/viagem até4,86s e leitura inicialRedis até1,80s. Uma tentativa de filtro pelo nome do arquivo `GtfsRealtimePerformanceTests` não encontrou testes: esse arquivo é parte de `GtfsRealtimeIntegratedTests`, cujo E2E foi explicitamente ignorado. Essa tentativa sem casos não entra nas contagens de aprovação. As fases de matching já distinguem preparação, conexão, `ExecuteReader`, leitura/materialização e fallback. `ExecuteReader` contém rede/buffering/servidor, não equivale sozinho a CPU ou execução SQL.

Coletor e polling preservam a vaga única até ACK. O coletor espera o intervalo existente depois de cada tentativa; um lote pendente impede sobrescrita. O polling mantém seu alvo de15s e o wake-up de snapshot já implementado. Nada disso foi alterado. ACK só continua liberando a geração depois do fluxo previsto, sem adiantar publicação ou esconder falha de histórico.

## Diagnóstico local e escolha da correção

Após o usuário disponibilizar Docker, foi criado somente `noponto-pipeline-latency-20261028`, imagem `postgis/postgis:16-3.4`, PostgreSQL16.4/PostGIS3.4.3, porta **127.0.0.1:55436**, banco/usuário de teste próprios, dados emtmpfs,768MiB e2CPUs. Não foram usados os quatro containers locais preexistentes, homeserver ou credenciais/dados produtivos. Ao terminar, somente esse container descartável foi parado/removido, após conferir seuID; os quatro preexistentes permaneceram ativos e as evidências foram preservadas.

O corpus C# usa300 posições,100 por chunk, rotas densificadas (2.001 vértices na horizontal da fixture) e IDs sintéticos; o ensaio SQL alternado usa100 posições distintas e duas rotas de4.001 vértices. É representativo do tipo de custo espacial, **não** da distribuição das459 linhas, concorrência ou hardware produtivo.

Hipóteses verificadas:

1. **Fila de conexão/C# dominante:** não ocorreu no ensaio aquecido. Conexão mediana0,087ms antes e0,077ms depois; preparação≈1–2ms. `ExecuteReader` dominou. Isso não exclui contenção em produção.
2. **Custo espacial repetido no PostGIS:** confirmado pelo plano real. As projeções da próxima parada repetiam `ST_Length(ST_LineSubstring(...))` para a mesma ocorrência/versão, nos dois ramos, por posição. O plano anterior mostrou trabalho pesado nos resultados laterais de parada.
3. **Repetição de ST_Distance do ramo anterior:** uma variante comLATERAL/OFFSET preservou resultados, mas não apresentou melhora consistente nos pares alternados; não foi aplicada.
4. **Coalescer observações com coordenadas idênticas:** uma exploração em memória foi descartada. A frequência real não foi medida e não é necessário agrupar veículos para remover o cálculo estrutural repetido. Nenhum helper ou regra de fusão dessa exploração integra o commit.

## Correção e preservação de comportamento

`proximas_paradas` materializa a escolha **por entrada/ramo**, com o mesmo filtro, ordenação eLIMIT1 anteriores. `comprimentos_paradas` calcula o comprimento até cada ocorrência distinta efetivamente escolhida, por versão, uma única vez no statement. Os resultados global/anterior reutilizam somente essa constante estrutural; distância ao veículo, fração, bearing, ranking, progressão e orçamento físico continuam individuais.

A operação preserva ausência de próxima parada comoNULL (inclusive distância), volta circular, identidade da ocorrência/linha/sentido, geometria geográfica exata e desempates existentes. Não substitui a distância por fração×comprimento nem por distância acumulada aproximada. O ramo operacional não foi alterado. Materialização é limitada ao chunk atual, no máximo dois resultados por entrada; não há invalidação de cache externo ou retenção entre gerações.

Não foram modificados motorLegacy, matching individual, tamanho de chunk, paralelismo, thresholds, Redis/CAS, histórico, ACK, SignalR, TTLs, fontes, clocks ou cadências. BUS GTFS-only, Hybrid e BRT podem se beneficiar quando usam o mesmo motorSetBased; os demais caminhos mantêm o comportamento anterior. Não houve remoção de funcionalidade.

## Medições antes/depois

|Medição real local|Antes|Depois|
|---|---:|---:|
|PostGIS `EXPLAIN ANALYZE`,100 entradas, mediana de5 pares alternados|1.108,816ms|625,792ms|
|Repetição independente da ferramenta versionada,5 pares|1.129,806ms|602,200ms|
|Repositório .NET,300 entradas distintas,3 chunks; mediana de3 execuções aquecidas|1.839,396ms|1.206,363ms|
|.NET: preparação do repositório, mediana|1,102ms|2,140ms|
|.NET: espera/abertura de conexão, mediana|0,087ms|0,077ms|
|.NET: `ExecuteReader`, mediana|1.825,378ms|1.195,228ms|
|.NET: leitura / materialização, medianas separadas|10,148 /1,777ms|5,726 /6,730ms|
|Chamadas de comprimento no teste instrumentado100 entradas/200 ramos|402|203|

O ensaioSQL comparou exatamente200 registros/colunas, ordenados por input/ramo, sem divergências. As3 chamadas SQL e600 linhas de resultado do ensaio .NET foram mantidas, semfallback. A comparação C# é sequencial; os paresSQL alternados reduzem viés de aquecimento/variação. As medianas porfase não devem ser somadas como uma única execução. O contador usa um wrapper apenas no schema da fixture que delega ao PostGIS real; os benchmarks de tempo usam as funções nativas, sem esse wrapper.

O plano permite atribuir a melhora local ao menor trabalho PostGIS, e não a eliminar matching, mudar limiar ou forçar timestamps. Não prova que os11,52s produtivos cairão na mesma proporção: número de paradas compartilhadas, geometrias, versões, distribuição porchunk, carga e geração mudam. Materializar constantes tem custo; o benefício tende a ser menor quando cada entrada escolhe uma parada diferente.

## Frescor físico GTFS: origem, cache e publicação

O parser usa `VehiclePosition.timestamp`; não troca pelo headerGTFS, DateHTTP, recebimento ou servidor. O cliente preserva o snapshot e `ReceivedAtUtc` em cache local eHTTP304. Um novoHTTP200 atualiza o recebimento, mas **não** transforma um GPS físico repetido em novo. O cache de mapeamento/crosswalk conserva o timestamp e revalida a idade antes da promoção. Deduplicação eCAS ainda rejeitam repetição/regressão.

O cliente impõe o intervalo configurado (padrão30s); Retry-After e frescorHTTP podem adiar uma nova consulta. Isso não mede a cadência real de atualização do provedor. Na release153, os intervalos físicos centrais da coorte eram≈30s; ainda assim, a idade GTFS na composição chegou a exceder60s. O arquivo não reteve uma série completa das aquisições/headersGTFS para distinguir exatamente origem desatualizada, cache local e validação304 em cada geração. Não foi repetida coleta externa nesta tarefa.

|Geração2/898 arquivada|Idade física p50|
|---|---:|
|Início da aquisição|19,359s|
|Seleção|≈20,259s|
|Após matching/diagnósticos/ETA (`matched`)|31,544s|
|Após barreira de confirmação (`cas_accepted`)|35,684s|
|SignalR concluído / ACK|36,144 /36,219s|
|REST observado|38,975s|

Um novo teste controlado demonstra: GPS com10s no primeiro body chega a71s após cache/304; umHTTP200 repetindo o GPS tem132s, apesar de recebimento novo; só uma medição física nova reduz a idade para13s.

O log de aquisição existente passa a incluir `raw_feed_count`, `body_physical_age_mean_seconds`, `read_physical_age_mean_seconds`, `body_retained_seconds` e `raw_feed_recent60`. São agregados do **feed bruto antes do crosswalk/modal**, não contagens de ônibus aceitos/publicados. Mantêm `acquisition=Downloaded/LocalCache/NotModified/Failed`, sucesso e duração. Há uma passagem sem ordenação sobre o feed já limitado, somente comInformation habilitado; nenhum identificador, coordenada ou payload é logado. O teste de allowlist de logs foi atualizado mantendo a lista exata de campos permitidos e validando ausência de idades fictícias nas falhas.

## Testes e evidências

Artefatos/TRX/planos em `artifacts/pipeline-latency-20261028/`, preservados. O RED relevante (`length-red.trx`) exerceu o repositório e PostGIS real: esperava≤210 cálculos e obteve402. GREEN registrou203 com as mesmas100 entradas e200 ramos.

|Requirement|Evidence|
|Remover cálculo redundante sem retirar resultados|`MatchingPipelineLatencyPostgisTests.SharedNextStop_ComputesGeographicLengthsOncePerStop_NotPerVehicleBranch`|
|Preservar fim de linhaNULL e volta circular|`MatchingPipelineLatencyPostgisTests.NextStop_PreservesLinearEndNull_AndCircularWrapDistance`|
|Medição em rotas densas, correlação de entradas e parada|`MatchingPipelineLatencyPostgisTests.DenseRoutes_BoundedCombinedChunks_PreserveEveryInput`|
|Equivalência geográfica, faixa, fallback e projeção operacional|`GpsMatchingLotePostgisTests` e `GpsMatchingCombinadoDiferencialPostgisTests.QueryCombinada_EquivaleAosDoisCaminhosAtuais_CampoACampo`|
|Cache/304/200 não simulam GPSnovo|`GtfsRealtimeGpsTests.CacheAndRevalidation_DoNotRenewPhysicalGps_EvenWhenHttp200ArrivesAgain`|
|Falha/vazio/futuro em diagnóstico não renovam GPS|`GtfsRealtimeGpsTests.FreshnessDiagnostic_DistinguishesMissingBodyAndFutureGpsWithoutChangingIt`|
|Privacidade e classificação do log|`GpsPollingCadenciaTests.Gtfs_AcquisitionLogsSeparateCachedFailureAndCachedSuccess`|
|ACK, backpressure e físico repetido|`GpsPollingCadenciaTests.Bus_CollectorKeepsHttpCadenceAndSingleSnapshotUntilAck`, `GpsPollingCadenciaTests.Gtfs_RepeatedGpsInRealPollingNeverReachesMatchingOrCas`|
|CAS/REST/SignalR/ACK porgeração e publicação sem duplicação|`GpsPipelineTraceTests.SameConsumedGenerationLinksPhysicalAgeCasRestSignalRAndAckWithoutPayload`, `GpsSignalRPublisherTests`, `GpsTimestampDeduplicationTests`, `GpsPhysicalPredecessorTests`|

Seleções Release:115/115PostGIS;402aprovados/1ignorado nas regressões matching/fontes/GTFS/handoff/trace/viagem;151/151 deduplicação/predecessor/SignalR. Total668 aprovados,zero falhas finais,um ignorado. O ignorado foi `GtfsRealtimeIntegratedTests.OfficialImportCrosswalkPollingAndRestartRemainIsolated`, dependente de importação/E2E externa não executada. Não há alegação de novo E2E com Redis Lua real ou replay de dados produtivos. O buildRelease final semincremental passou comzeroerros e5avisospreexistentes. A seleção offline completa1111 não foi repetida; os novos testes GTFS estão na classe já incluída na allowlist oficial e os nativosPostGIS ficam fora dela por exigirem banco descartável. Houve uma falha intermediária de allowlist de log ao acrescentar os campos agregados; foi corrigida por atualização explícita do contrato, sem remover a validação de privacidade.

## Reprodução e próximo passo

A ferramenta `tools/gps/benchmark_matching_lengths.py` compara SQL da revisão base com o checkout atual. Recusa Docker remoto, container sem prefixo descartável, imagem diferente dePostGIS16 e porta que não seja127.0.0.1:55436. Cria somente tabelas sintéticas no container de teste. O container usado foi removido ao final; para reproduzir, crie antes uma nova instância descartável com os limites/porta/banco acima (a ferramenta não inicia containers). Uso, a partir da raiz:

```powershell
python -B tools/gps/benchmark_matching_lengths.py --output artifacts/pipeline-latency-replay
```

Os testes nativos usam `POSTGIS_TEST_CONNECTION` somente para a instância descartável e criam schemas privados próprios. Não copiar conexões produtivas. Com essa variável local definida:

```powershell
dotnet test NoPonto/NoPonto.csproj -c Release --filter "FullyQualifiedName~MatchingPipelineLatencyPostgisTests|FullyQualifiedName~GpsMatchingLotePostgisTests|FullyQualifiedName~GpsMatchingCombinadoDiferencialPostgisTests"
```

A expectativa técnica é reduzir a parcela de processamento entre seleção e publicação quando há parada/versão compartilhada no lote, beneficiando também oBRT no motorSetBased. Não altera a idade recebida do provedor nem garanteGPS<60s. Posições já recebidas com≥60s continuam antigas; cadência externa, cacheHTTP legítimo, matching individual, persistência de viagem ebackpressure ainda podem consumir o orçamento.

Próxima validação útil: revisão/CI e publicação separadamente autorizadas; comparação beta posterior com as mesmas coortes, registrando fases de matching e os novos agregadosGTFS. **O capturador operacional atual não retém essas duas famílias de logs**: é necessário ampliar seu schema fechado e testar os limites antes de depender dele para essa evidência. Isso não foi alterado nesta correção do backend. Sem nova refatoração de aquisição, ajuste deTTL/cadência ou janela automática.

[Resumo de medições](gps-pipeline-latency-20261028.summary.json).
