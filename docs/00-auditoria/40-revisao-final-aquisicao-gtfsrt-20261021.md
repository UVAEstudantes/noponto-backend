# Revisão técnica final dos patches 38/39

**Parecer: não aprovar implantação neste estado.** Dois casos novos de HTTP 304 falharam contra o cliente real. O build independente da solução continua impedido pela política de aprovação do ambiente. Também não foi verificada a correspondência entre imagem produtiva e fonte local; os comandos para isso estão no plano 41. Este parecer substitui a prontidão condicional do relatório 39, sem reescrever a evidência histórica.

## Achados, por prioridade

### P1 — ETag forte divergente em 304 é associado ao corpo anterior

`GtfsRealtimeGps.cs:163` considera 304 utilizável somente por existir `_last.Success`; em `:187`, atualiza `_etag` antes de conferir vínculo com a representação armazenada. Com 200/ETag old em t=0, 304/ETag new em t=30 e 304 para If-None-Match new em t=60, o corpo de t=0 continua sendo usado e o validador novo passa a protegê-lo. A implementação deve recusar a associação e recuperar uma representação completa no próximo prazo elegível, sem retry imediato.

`DifferentStrongEtagOn304MustNotRelabelOldBody` falhou: esperava recusa da revalidação, mas `Success=true`. A resposta inconsistente é uma condição de provedor/intermediário simulada, não uma ocorrência comprovada em produção. O novo comportamento de atualizar ETag em 304 torna a associação errada persistente enquanto esse validador continuar recebendo 304. O filtro de idade física pode retirar posições antigas, mas não recupera o corpo correto. Não há fabricação de timestamp.

A [RFC 9111, seção 4.3.4](https://www.rfc-editor.org/rfc/rfc9111.html#section-4.3.4) exige identificar a representação correspondente antes de atualizar seus metadados. Falta um gate explícito para essa correspondência. A correção deve cobrir ETag forte incompatível, weak ETag correspondente/incompatível, revalidação por Last-Modified e 304 sem validador, sem invalidar os casos legítimos já testados.

### P2 — Cache-Control substituído em 304 conserva max-age removido

Em `GtfsRealtimeGps.cs:167`, `cachePolicy?.MaxAge ?? (notModified ? _cacheMaxAge : null)` confunde ausência de todo o campo Cache-Control com ausência da diretiva max-age em um campo novo. Assim, após 200/max-age=60 em t=0, 304/Cache-Control:no-store em t=60 descarta o corpo, mas preserva no prazo a espera de 60s. Em t=90 não há nova aquisição nem corpo reutilizável, embora o intervalo mínimo de 30s já tenha decorrido.

`ReplacingCacheControlOn304MustNotRetainRemovedMaxAge` falhou: esperava três GETs (0/60/90), mas houve dois. A próxima aquisição fica adiada para t=120. Com o max-age=30 observado atualmente, esse caso específico não acrescenta espera além do mínimo; a regressão aparece quando a política anterior é maior.

A atualização deve tratar o campo recebido como substituição; reter a política anterior é correto quando o campo inteiro é omitido. Referência: [RFC 9111, seção 3.2](https://www.rfc-editor.org/rfc/rfc9111.html#section-3.2). A revisão não alterou essa lógica nem a política do provedor.

## Resultado da revisão integral do diff funcional

| Área | Conclusão |
| --- | --- |
| Integração 38/39 | Os hashes dos dois fontes e da parcial de outages coincidem com os finais do relatório 39. Os dez arquivos iniciais foram preservados. Mesma árvore/HEAD43b6a72776b0e9c1105bf33b5fe73d2e33d3012b; sem reset, stash ou commit. |
| Correção da cadência | Mantém intervalo entre inícios, idade Date/Age/trânsito e maior prazo de Retry-After. Coleta de 10s após conclusão ainda quantiza oportunidades. Testes anteriores passaram novamente. |
| Concorrência | O gate da fonte envolve leitura/mapping e sempre libera após aquisição. O gate HTTP serializa todos os campos de cache/validadores. Cancelamento antes de adquirir o gate HTTP não altera a aquisição alheia. Não foi identificada regressão funcional de concorrência no diff. |
| Cancelamento | Timeout externo registra falha e relança ao source; timeout interno retorna falha. Shutdown continua propagado. Não se republica sucesso antigo após aquisição cancelada. Budget da fonte também inclui fila/crosswalk: acquisition_timeout sozinho não identifica DNS, TLS ou corpo. |
| Corpo parcial | Buffer só é parseado após EOF; limites/validação anteriores intactos. IOException/timeout/cancelamento descartam resultado operacional e validadores. Nenhuma promoção parcial encontrada. |
| Recuperação após falha | Limpar ETag/Last-Modified junto do corpo impede 304 órfão. O próximo GET permanece no prazo já planejado; não foi introduzido retry imediato. |
| Reutilização | LocalCache/Downloaded/NotModified/Failed, success/failure e logs do gate permitem distinguir reutilização de falha. Sucesso do adaptador/gate não certifica GET novo ou avanço GPS. |
| HTTP 304 | Permanecem os achados P1/P2. Os 201/810 testes aprovados não os cobriam. Não é possível confirmar ausência de regressão nas políticas de cache do provedor. |
| Timestamps/mapping | TimestampGps continua vindo do payload. ReceivedAtUtc original permanece em cache/304/fallback; novo 200 pode renovar recepção sem renovar GPS, e continua deduplicado. Mapping reaplica filtros de idade existentes. |
| Memória/logs | Continua um resultado HTTP e um mapping por modal, buffers/entidades limitados e logs escalares sem dimensão por veículo. Nenhuma fila ou retenção nova por identidade. |

Comparados com HEAD e evidências anteriores: parser/opções GTFS (prefixo de arquivo), GpsSppoCollectorService, GpsSppoSnapshotStore, GpsPollingService, GpsSppoCollectorOptions, GpsTimestampDeduplication, GpsSignalRPublisher e GpsBrtPollingGate intactos. Não há delta em TTLs/CAS/SignalR/persistência/migrations. Program.cs, DatabaseStartupPolicy, csproj, Dockerfile, Compose, scripts de deploy e workflow também intactos. As únicas mudanças funcionais acumuladas continuam nos dois arquivos GPS revisados; esta revisão não acrescentou correção funcional.

## Risco da maior frequência

O limite é por cliente/modal e por processo. Com intervalo mínimo30s, o teto sustentado é dois GETs/min/modal/réplica, ou quatro/min para BUS+BRT em uma instância, antes de cache maior/Retry-After. Não é um rate limit global por IP. Duas réplicas ou sondas adicionais somam tráfego. A passagem de60 para30s pode dobrar a aquisição efetiva;30s para30s não altera o teto.

Com timeout15s, até30 segundos de tempo de espera de rede por minuto/modal no limite de falhas, sem paralelismo dentro do mesmo cliente. Isso não significa30s de CPU. Após falha, a recuperação sem validador pode baixar o corpo completo, cerca de270820 bytes na amostra fornecida, mais bytes que304. O payload/provedor pode variar; não se certifica capacidade por esse tamanho único. Corpo mais frequente implica mais parsing/mapping e gerações; dedup ainda impede commits de GPS repetido, mas não elimina custo de leitura. Coletor de10s e polling15s não foram acelerados.

O rollout deve observar429/503, Retry-After, timeouts, bytes/latência, CPU/memória e duração do ciclo. Sondas devem respeitar política e somar sua carga ao backend. Não há evidência para aumentar timeouts, mudar flags ou contornar cache. Critérios numéricos propostos estão no plano41.

## Verificação executada

- `dotnet build NoPonto.sln --no-restore --no-incremental -c Release`: **não iniciou**. Rejeição automática: aprovação exigida com AskForApproval=Never. Não foi tratado como falha de compilação nem como build aprovado; não foi tentado contornar o bloqueio.
- Revisão 304 em scratch, por import MSBuild opt-in sem editar o projeto: Passed0, Failed2, Skipped0;66ms. Casos preservados em [40-review-304-cases.cs.txt](40-review-304-cases.cs.txt). Não integram a suíte padrão nem o patch funcional; são provas de bloqueio.
- Recompilação padrão do projeto, sem o import, e execução dirigida: Passed201, Failed0, Skipped0. Confirmação de que a assembly padrão não contém os testes de revisão. Cinco avisos anteriores, sem erros.
- Seleção offline oficial: Passed810, Failed0, Skipped0; verificação da allowlist e dos TRXs. Os conjuntos se sobrepõem e não são somados. O green dessas seleções não invalida os dois achados novos.

Comando de reprodução isolada, a executar localmente somente quando se quiser confirmar os REDs; resultado esperado: duas falhas enquanto não corrigidos:

```powershell
$reviewImport = Join-Path ([IO.Path]::GetTempPath()) 'noponto-review-304.props'
$reviewCase = (Resolve-Path 'docs/00-auditoria/40-review-304-cases.cs.txt').Path
$escapedCase = [Security.SecurityElement]::Escape($reviewCase)
[IO.File]::WriteAllText($reviewImport, '<Project><ItemGroup><Compile Include="' + $escapedCase + '" /></ItemGroup></Project>')
dotnet test NoPonto/NoPonto.csproj --no-restore -c Release "-p:CustomBeforeMicrosoftCommonTargets=$reviewImport" --filter 'FullyQualifiedName~NoPonto.Tests.GtfsFinalReview304Tests.' --logger 'trx;LogFileName=review-304-red.trx' --results-directory NoPonto/lab-output/gtfs-final-review-20261021
# Depois, recompilar sem esse parâmetro antes de usar a saída padrão.
```

Comando dirigido executado novamente:

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore -c Release --filter 'FullyQualifiedName~NoPonto.Tests.GpsPollingCadenciaTests.|FullyQualifiedName~NoPonto.Tests.GtfsRealtimeGpsTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoCollectorTests.|FullyQualifiedName~NoPonto.Tests.GpsSppoClientTests.|FullyQualifiedName~NoPonto.Tests.GpsPollingFontesTests.' --logger 'trx;LogFileName=review-directed.trx' --results-directory NoPonto/lab-output/gtfs-final-review-20261021
$offlineFilter = python .ci/offline_tests.py filter
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test NoPonto/NoPonto.csproj --no-restore --no-build -c Release --filter $offlineFilter --logger 'trx;LogFileName=review-offline.trx' --results-directory NoPonto/lab-output/gtfs-final-review-20261021
python .ci/offline_tests.py verify NoPonto/lab-output/gtfs-final-review-20261021/review-offline.trx
```

Comandos acima documentam validação local. Nenhum comando do plano41 ou diagnóstico42 foi executado no homeserver. Sem Docker local/servidor, fonte pública, REST produtivo, Redis/PostgreSQL, deploy, restart, commit, push, merge ou ativação automática. Os artefatos/estados anteriores permanecem preservados.

Evidências e hashes: [gtfsrt-final-review-validation-20261021.json](gtfsrt-final-review-validation-20261021.json). Plano manual: [41](41-plano-manual-aquisicao-gtfsrt-20261021.md). Diagnóstico independente: [42](42-diagnostico-readonly-homeserver-20261021.md).
