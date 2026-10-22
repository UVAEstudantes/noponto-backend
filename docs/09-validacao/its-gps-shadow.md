# NoPonto — laboratório ITS em modo sombra

Diagnóstico experimental da linha 838, desabilitado por padrão. Não altera a fonte
principal, o matching, a persistência GPS, os endpoints REST ou o SignalR.
Não habilitar na release #142. A escolha ITS/GTFS-RT/híbrida depende de evidências
posteriores; este patch não implementa troca de fonte.

## Configuração local

Exemplo da seção de configuração, para um ambiente local já isolado dos recursos
de produção. Nenhum appsettings, arquivo de ambiente ou configuração de release
foi modificado pelo patch.

```json
{
  "ItsGpsShadow": {
    "Enabled": true,
    "ServiceCode": "838",
    "IntervalSeconds": 60,
    "AcquisitionBudgetSeconds": 15,
    "RecentUtcWindows": 3,
    "PageLimit": 5000,
    "MaxPages": 6,
    "MaxRecords": 30000,
    "MinimumRequestIntervalSeconds": 1,
    "SpatialTimestampToleranceSeconds": 10
  }
}
```

Também é possível definir apenas no processo PowerShell local, antes de usar o
procedimento habitual de inicialização do backend de desenvolvimento:

```powershell
$env:ItsGpsShadow__Enabled = 'true'
$env:ItsGpsShadow__ServiceCode = '838'
$env:ItsGpsShadow__IntervalSeconds = '60'
# Ao encerrar o laboratório:
Remove-Item Env:ItsGpsShadow__Enabled
Remove-Item Env:ItsGpsShadow__ServiceCode
Remove-Item Env:ItsGpsShadow__IntervalSeconds
```

Não iniciar a API inteira apontando suas demais dependências para produção só
para executar o laboratório: os workers operacionais existentes continuam tendo
suas responsabilidades habituais. Este diagnóstico não é um runner independente
da aplicação. Sem habilitação explícita, não faz HTTP nem leitura do runtime.
Com a flag desligada, a DI também não resolve o probe/leitor nem cria o cliente
HTTP exclusivo; o worker termina após a checagem inicial, sem timers periódicos.

Limites aceitos: intervalo 60–3600s; orçamento 1–30s; janelas 1–3; limite por
página 1–5000; páginas totais 1–10; registros retidos 1–50000; intervalo mínimo
entre requisições 1–10s; tolerância temporal espacial 0–60s. Linha deve ter até
20 caracteres ASCII alfanuméricos ou hífen. Configuração inválida interrompe
somente o diagnóstico. Alterações de configuração exigem reiniciar o processo.

## Aquisição e isolamento

Reutiliza o parser, DTO, construção de URL e leitura HTTP de `GpsDatarioClient`,
com HttpClient nomeado exclusivo e sem logs automáticos de requisições. O método
legado que percorre todas as páginas preserva seu comportamento. Não usa o
resolver de fontes primárias nem os collectors operacionais.
O handler exclusivo impede redirects automáticos e cookies, renova conexões
após cinco minutos e tem seu cliente descartado com o probe no encerramento.

Consulta `sistema=sppo`, `modo=onibus`, `servico` configurado, em ordem: minuto
UTC corrente, anterior e penúltimo, conforme a quantidade configurada. Repassa
`next_cursor` no parâmetro **`cursor`**, conforme o
[OpenAPI ITS](https://its.mobilidade.rio/openapi.json), consultado em 22/10/2026.
`minuto_utc` refere-se a `datetime_servidor`; todas as idades e comparações usam
**`datetime` físico**, sem substituí-lo por relógio de chegada ou coleta.

Páginas/registros/orçamento são globais ao ciclo, incluindo todas as janelas.
O orçamento também limita a espera pela leitura opcional do runtime. Cada corpo
HTTP sombra é limitado a 8 MiB antes da desserialização, inclusive em transferência
sem Content-Length; esse limite não altera o método legado de todas as páginas.
O intervalo mínimo vale
entre inícios de requisições, inclusive na mudança de janela; não há retries no
ciclo. Erros HTTP respeitam `Retry-After`, inclusive 429 e 503; 429 sem header
aguarda pelo menos 60s. O worker espera o
intervalo configurado depois de terminar cada ciclo, sem sobreposição ou rajadas
para compensar atrasos. Paginação repetida ou cursor maior que 48 caracteres é
interrompido. Falhas/cancelamentos conservam os agregados das páginas já obtidas;
cancelamento de encerramento é propagado. Exceções inesperadas e falhas do logger
ficam isoladas no worker.

Uma dependência que ignore o token não prolonga a espera do ciclo: `WaitAsync`
encerra a espera no prazo e o worker suspende ciclos novos enquanto aquele I/O
continua pendente. Sua conclusão/falha tardia é observada sem logs de detalhes.
Não é possível abortar à força uma operação externa que ignore cancelamento;
essa proteção evita acumular novas operações. O prazo cobre espera HTTP, corpo,
janelas, páginas e runtime; o cálculo síncrono dos agregados ocorre depois e tem
volume limitado pelos registros retidos, sem promessa de prazo rígido de CPU.

A única interação com o runtime é `IVeiculosLinhaRuntimeReader.ListarAsync` para
a linha configurada: ativo primeiro e recente como `SemSinal`, conforme o leitor
existente. Não há dependências de escritores PostgreSQL/Redis, publicadores
SignalR ou chamadas aos endpoints REST. Há custo adicional de leitura de cache
e consumo de HTTP/CPU/memória quando habilitado.

## Interpretação dos agregados

- `ComparisonBasis=ITS_vs_NoPontoPublishedRuntime_not_raw_GTFSRT`: comparação
  contra posições **já publicadas pelo pipeline operacional**, sujeitas a seus
  filtros, atrasos, TTLs e estados. Não mede diretamente ITS × feed GTFS-RT bruto.
- Identidade: trim e maiúsculas, preservando zeros, prefixos e pontuação, como o
  runtime atual. Não se presume equivalência entre códigos diferentes.
- `Vehicles`: identidades com coordenadas e GPS válidos, deduplicadas pelo maior
  timestamp físico entre páginas/janelas/fornecedores. Cada fornecedor também é
  deduplicado por veículo. As quantidades por fornecedor podem se sobrepor e não
  devem ser somadas para obter a frota única. Empates físicos entre fornecedores
  usam ordem alfabética do fornecedor; não há decisão operacional de preferência.
- `Recent40`/`Recent60`: idade não negativa até 40/60s, inclusive. Dados antigos
  permanecem na distribuição; futuros são excluídos e contados em `FutureRecords`.
  Timestamp ausente, inválido ou até Unix epoch, identidade vazia, coordenadas
  inválidas e registros fora do sistema/modal/linha são `InvalidRecords`.
  O diagnóstico exige data/hora ISO completa e fuso explícito; não aceita que o
  parser complete data ou fuso ausentes usando o relógio/localidade do processo.
  Essa validação adicional fica no laboratório, preservando o parser legado.
- Mediana e p90 usam interpolação linear e retornam `null` em amostra vazia.
  A idade usa o instante `ObservedAtUtc`, depois da aquisição/leitura do runtime.
- `Comparable=false` se não há posições válidas no runtime ou nenhuma página ITS
  foi adquirida. Interseção, ITS mais recente/igual/antiga, somente ITS e delta
  ficam `null`, evitando transformar ausência/erro em cobertura zero. Os números
  independentes da ITS continuam disponíveis quando há amostra utilizável.
  Sem amostra ITS utilizável, `Vehicles`, frescor e contadores de validação são
  `null`; erro de runtime deixa `RuntimeVehicles=null`. `SpatialPairs=null`
  quando não há comparação possível. Zero representa uma observação vazia ou
  ausência de pares em fontes disponíveis, sem afirmar cobertura de frota zero.
  Página vazia com todos os estados `ok` é observada como vazia; apenas estados
  pending/desconhecidos sem registros não comprovam uma amostra vazia.
  `RuntimeStatus` diferencia
  `Empty`, `Unavailable` e `Available`; posições inválidas também podem tornar
  um runtime disponível não comparável.
- `BothSources`, `ItsNewer`, `EqualTimestamp`, `ItsOlder` e `ItsOnly` usam
  identidades normalizadas. Delta assinado é GPS ITS menos GPS runtime; positivo
  significa ITS mais recente. `ItsOnly` significa ausente do snapshot publicado,
  sem afirmar que o feed bruto GTFS-RT não recebeu aquele veículo.
- `SpatialPairs`: apenas pares cuja diferença absoluta de timestamps é até a
  tolerância configurada. Mediana/p90 da distância Haversine em metros; sem
  matching, projeção na rota ou escrita de posição.
- `Requests`: tentativas HTTP iniciadas; `Pages`: páginas desserializadas;
  `Records`: registros retidos antes de validação/deduplicação; `WindowsCompleted`:
  janelas cuja paginação terminou. Uma resposta que exceda o limite é truncada e
  marcada `SafetyLimit`. `AcquisitionMs` inclui HTTP, esperas de aquisição e a
  leitura/espera do runtime, sem o cálculo das métricas. Esgotamento do prazo no
  runtime é `BudgetExhausted`. Minuto divergente ou `data` ausente é
  `InvalidResponse`; os estados dos fornecedores dessa página são preservados.
- `ProviderStatuses` conta ocorrências por fornecedor/estado nas páginas, não
  veículos. Uma ocorrência `pending` não é apagada por um `ok` posterior.
  Fornecedores/estados não reconhecidos viram `UNKNOWN`/`unknown`; nenhum valor
  arbitrário ou detalhe de exceção é publicado.
- `Incomplete=true` sinaliza limites, falhas, cursor inválido, metadados ausentes,
  minuto divergente ou qualquer estado diferente de `ok`. **`Incomplete=false`
  e estado `ok` não comprovam cobertura completa do fornecedor.**

## Exemplos de logs

Exemplos sintéticos abreviados: cada mensagem real contém um JSON agregado
completo. Não contém identificadores, coordenadas, cursores ou payloads de veículos.

```text
ItsGpsShadow {"ServiceCode":"838","AcquisitionStatus":"Complete","Incomplete":false,"Requests":3,"Pages":3,"Records":42,"WindowsCompleted":3,"AcquisitionMs":2350,"RuntimeStatus":"Available","Metrics":{"ComparisonBasis":"ITS_vs_NoPontoPublishedRuntime_not_raw_GTFSRT","Comparable":true,"Vehicles":18,"Recent40":12,"Recent60":15,"MedianGpsAgeSeconds":31,"P90GpsAgeSeconds":82,"BothSources":14,"ItsNewer":8,"EqualTimestamp":4,"ItsOlder":2,"ItsOnly":4,"MedianTimestampDeltaSeconds":9,"SpatialPairs":7,"MedianDistanceMetres":18},"ProviderStatuses":{"ZIRIX/ok":3,"CONECTA/ok":3}}
ItsGpsShadow {"AcquisitionStatus":"Complete","Incomplete":true,"RuntimeStatus":"Empty","Metrics":{"Comparable":false,"Vehicles":6,"BothSources":null,"ItsOnly":null},"ProviderStatuses":{"ZIRIX/pending":1,"CONECTA/ok":3}}
ItsGpsShadow {"AcquisitionStatus":"BudgetExhausted","Incomplete":true,"Pages":1,"WindowsCompleted":0,"RuntimeStatus":"Unavailable","Metrics":{"Comparable":false,"Vehicles":5,"BothSources":null,"ItsOnly":null}}
ItsGpsShadow cycle failed; operational GPS is independent.
```

Estados de aquisição: `Complete`, `SafetyLimit`, `InvalidCursor`,
`BudgetExhausted`, `Timeout`, `InvalidResponse`, `ReadError`, `HttpError` ou `Http` + código.
O diagnóstico pode concluir a aquisição e ainda ter comparação indisponível.

## Validação e próximos passos

Os testes novos são offline, com handlers HTTP, runtime falso e relógio virtual;
incluem o worker registrado por DI e o cliente compartilhado. Estão na allowlist
offline junto dos testes existentes do cliente. Não iniciar `Program.cs` para
rodá-los nem executar a suíte inteira sem filtro: há testes conectados no projeto.

Validação inicial em 22/10/2026, SDK 9.0.306, xUnit/VSTest (antes da revisão abaixo):

- Direcionados: **66 aprovados, 0 falhas, 0 ignorados**. Incluem 60 casos novos e
  seis existentes do cliente, com TRX XML conferido por classe e resultado.
- Seleção oficial `.ci/offline-tests.json`: **949 aprovados, 0 falhas, 0 ignorados**.
  `offline_tests.py verify` confirmou todas as classes e ausência de testes extras.
- Compilação Release do projeto executada por `dotnet test`: sucesso, cinco
  avisos preexistentes (CS8981, CS7022 e xUnit2031).
- Build da solução solicitado, mas a revisão automática rejeitou o comando antes
  de executá-lo: aprovação exigida com política `Never`. Não há evidência de build
  completo da solução nesta execução.
- `git diff --check` aprovado. Nenhuma suíte conectada, startup da API, container,
  acesso Redis/PostgreSQL real ou payload GPS real executado/coletado.

Comandos efetivamente executados:

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore -c Release --filter '(FullyQualifiedName~NoPonto.Tests.ItsGpsShadow|FullyQualifiedName~NoPonto.Tests.GpsDatarioClientTests.)' --logger 'trx;LogFileName=its-shadow-targeted.trx' --results-directory NoPonto/lab-output/its-shadow
$offlineFilter = python .ci/offline_tests.py filter
dotnet test NoPonto/NoPonto.csproj --no-build --no-restore -c Release --filter $offlineFilter --logger 'trx;LogFileName=its-shadow-offline.trx' --results-directory NoPonto/lab-output/its-shadow
python .ci/offline_tests.py verify NoPonto/lab-output/its-shadow/its-shadow-offline.trx
```

Arquivos do patch: `GpsDatarioClient.cs` (operação compartilhada de uma página),
`ItsGpsShadowOptions.cs`, `ItsGpsShadowMetrics.cs`, `ItsGpsShadowProbe.cs`,
`ItsGpsShadowWorker.cs` em `NoPonto/2-Application/Services/GPS/`; os três arquivos
`ItsGpsShadow*Tests.cs` em `NoPonto/5-Testes/`; uma chamada de registro em
`NoPonto/Program.cs`; allowlist `.ci/offline-tests.json`; acréscimo em
`NoPonto/HISTORICO.md`; este documento. Nenhuma dependência de pacote adicionada.

## Revisão técnica da etapa

Revisado todo o patch em 22/10/2026, incluindo o cliente compartilhado, registro
em Program, quatro módulos novos, três arquivos de testes, seleção offline,
histórico e este guia. Foram corrigidos problemas da implementação inicial:

| Prioridade | Problema comprovado | Correção e evidência |
|---|---|---|
| Alta | Runtime que ignora cancelamento podia ultrapassar o prazo; HTTP também dependia exclusivamente do token. | Espera limitada pelo prazo compartilhado, observação de conclusão tardia e bloqueio de ciclos enquanto há I/O pendente. `RuntimeIgnoringCancellationCannotExceedCycleBudget`, `HttpIgnoringCancellationCannotExceedBudgetOrStartRuntimeRead`, `LateRuntimeReadPreventsNewCyclesAndShutdownDoesNotWaitForIt`. |
| Alta | Corpo HTTP era desserializado antes de aplicar limite de registros. | Limite fixo de 8 MiB antes de desserializar, com e sem Content-Length. `OversizedChunkedBodyStopsBeforeFullDeserialization`, `OversizedContentLengthIsRejectedBeforeReadingBody`. |
| Média | DI criava cliente mesmo com laboratório desligado. | Probe lazy após habilitação/validação; cliente/leitor não resolvidos e nenhum timer criado quando desligado. `DefaultRegistrationIsDisabledWithoutHttpOrRuntimeAccess`. |
| Média | Contadores ITS/runtime e pares espaciais indisponíveis apareciam como zero. | Contadores nullable conforme disponibilidade; estados pending vazios não comprovam amostra vazia. `HttpErrorsHaveAggregateStatusWithoutRetry`, `RuntimeFailureIsNonComparableAndDoesNotLoseItsMetrics`, `PendingOnlyEmptyDataDoesNotReportZeroVehicles`. |
| Média | Parser tolerante completava data/fuso ausentes e podia produzir frescor falso. | ISO completo com fuso exigido somente no laboratório. `PartialOrUnzonedGpsTimestampCannotFabricateFreshness`, `ExplicitIsoGpsTimestampPreservesPhysicalUtcInstant`. |
| Média | Minuto incorreto ainda era usado; item null abortava toda a métrica. | Página inválida rejeitada, estados preservados e membros null contados como inválidos. `WrongMinuteDoesNotBecomeUsableItsData`, `NullArrayMemberIsRejectedWithoutLosingValidRecords`. |
| Média | Duração excluía runtime; estouro nessa leitura aparecia como Complete. | Tempo inclui runtime e prazo esgotado nessa etapa retorna BudgetExhausted. `RuntimeReadSharesRemainingBudgetAndCancelsWithoutLosingItsMetrics`. |
| Média | Redirects automáticos podiam produzir requisições fora da contagem/cadência; cliente singleton precisava renovar conexões e ter descarte definido. | Handler exclusivo sem redirects/cookies, conexões de até cinco minutos e descarte do cliente. `NamedHttpHandlerDisablesUncountedRedirectsAndCookiesAndRefreshesConnections`. |
| Média | Retry-After absoluto era convertido com relógio ambiente e o tratamento só abrangia 429. | Preservado instante UTC ou delta; header respeitado também em outros erros, incluindo 503. `AbsoluteRetryAfterUsesUtcClockWithoutAmbientTimeConversion`, `RateLimitRetainsRetryAfterForSubsequentCycles`. |

O conjunto inicial de regressões reproduziu oito falhas antes das correções;
outros três casos reproduziram o problema de timestamps parciais/sem fuso.
Uma rodada intermediária identificou três expectativas de cancelamento que
precisavam aguardar a conclusão assíncrona do fake após o término da espera;
foram ajustadas sem retirar a exigência de cancelamento ou prazo.

Deduplicação continua pelo maior timestamp físico global e por fornecedor. Em
empate entre Zirix e Conecta, a ordem alfabética documentada é estável em ambas
as ordens de entrada, conforme `EqualPhysicalTimestampAcrossProvidersUsesStableDocumentedTie`.
Isso não avalia qual posição empatada é mais correta. Janelas UTC, limites
globais, cursor/encoding, pending persistente, shutdown, privacidade e ausência
de sobreposição permanecem testados. O cálculo síncrono dos agregados não lê
recursos externos.

Program contém somente uma chamada adicional de registro. GTFS-RT, resolver e
adaptadores operacionais, collector/polling, leitor runtime, matching, Redis,
SignalR, REST, pacotes e configuração de release permanecem inalterados. O
cliente compartilha DTO/conversores/URI/parser e mantém o método legado sem o
limite exclusivo do laboratório. A allowlist de testes é a única alteração de
seleção de CI; o workflow não foi modificado. Seu push é restrito à main e seu
evento de PR à main: enviar esta branch por push não solicita deploy.

Limites restantes: sem coleta real/carga/replicação; comparação contra runtime
publicado e não feed bruto; atrasos além das janelas; orçamento pode impedir
consultar minutos mais antigos; fan-out de leitura herdado do leitor por linha;
empates conflitantes ainda não têm análise de qualidade. Operação que ignore
cancelamento pode permanecer pendente, mas não acumula ciclos novos nem impede
encerramento do worker. O build completo da solução segue sem validação pela
recusa automática de aprovação; isso não é evidência de falha de compilação.

Validação final da revisão: **85/85 direcionados** (27 métricas, 34 probe,
18 worker, seis cliente legado) e **968/968 na seleção oficial offline**,
zero falhas/ignorados. São 19 casos adicionais em relação à entrega inicial.
TRX direcionado parseado por resultado/classe e TRX oficial aprovado por
`offline_tests.py verify`. Compilação Release do projeto passou com os mesmos
cinco avisos anteriores; nenhum aviso novo permaneceu. `git diff --check`
aprovado. Não foram executados testes conectados, startup, coleta real ou
operações em infraestrutura. O build completo foi novamente solicitado e
recusado antes de executar pela revisão automática: aprovação exigida com
política `Never`.

Os comandos de revisão repetem os comandos da validação inicial acima,
usando `LogFileName=its-shadow-review-targeted.trx` e
`LogFileName=its-shadow-review-offline.trx`. Relatórios locais ficam em
`NoPonto/lab-output/its-shadow/`, fora do versionamento. Commit/push desta
etapa são exclusivamente na `feat/its-shadow-probe`, sem merge ou deploy.

Após revisão e em ambiente autorizado, coletar ciclos em horários e condições
variados, registrando proporção comparável/incompleta, fornecedores, frescor e
limites atingidos. Monitorar o custo das leituras locais, cotas reais ITS e
`Retry-After`; a cadência por processo não coordena múltiplas réplicas. Se uma
janela esgotar o orçamento/páginas, as mais antigas podem não ser consultadas.
Dados mais atrasados que as três janelas podem ficar ausentes. Sem cache runtime
da linha, é possível avaliar a ITS isoladamente, mas não a concordância das fontes.

Antes de escolher a fonte principal ou uma política híbrida, acrescentar uma
comparação controlada contra o GTFS-RT bruto e avaliar empates/conflitos entre
fornecedores, diferenças de identidade, cobertura por veículo e qualidade
espacial. Este patch não mede carga real, cobertura real, comportamento de
réplicas ou a qualidade produtiva ITS/Zirix. Não houve coleta de payload GPS real,
deploy, migrations ou alteração da release #142 nesta implementação.
