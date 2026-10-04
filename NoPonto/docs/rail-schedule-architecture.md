# Grade ferroviária e próxima etapa operacional

A grade persistida é um snapshot temporal observado. Ela não altera `Linha`, `Sentido`,
`PadraoOperacional`, `PadraoVersao` ou `OcorrenciaParadaPadrao`. Seus patterns são
assinaturas de atendimento e apenas referenciam uma `PadraoVersao` quando o mapeamento
estrutural é seguro.

O calendário V1 distingue `WEEKDAY`, `SATURDAY` e `SUNDAY`. Feriados, grades especiais
e alterações temporárias não são inferidos por esta versão.

## ExpectedRun derivado

`ExpectedRun` é materializado sob demanda a partir da versão ativa e de uma data civil de
serviço em `America/Sao_Paulo`. Não possui tabela própria: a projeção é determinística e
fica em cache de memória limitado a oito pares `ScheduleVersionId + ServiceDate`. A troca
da versão ativa produz outra chave e impede reutilização cruzada.

O service day é o dia da partida. Stops com `DayOffset=1` permanecem associados ao dia
anterior, permitindo encontrar uma execução após a meia-noite. `WEEKDAY`, `SATURDAY` e
`SUNDAY` são suportados; feriados e serviços especiais continuam desconhecidos.

Patterns `EXACT` e `SUBSET_COMPATIBLE` preservam o mapping estrutural seguro, mas os stops
da grade continuam sendo a autoridade de atendimento. `UNRESOLVED` é materializado sem
`MappedPadraoVersaoId`; `CONFLICT` é reportado e não é materializado silenciosamente.

## Evolução futura

```text
RailScheduledRun → ExpectedRun derivado
    ↓ instanciação para uma data real de serviço
ExpectedRun (prior temporal; estado EXPECTED)
    ↓ evidência realtime suficiente associada a TrainCode
RailRun
```

Um `RailScheduledRun` isolado nunca deve publicar trem no mapa. `TrainCode` não pertence
à grade e somente poderá ser associado na futura camada realtime. O scanner atual não
consulta nem depende destas tabelas.

## Runtime V1 schedule-aware (shadow)

O binding usa `Provider + TrackingDate (America/Sao_Paulo) + TrainCode.Trim()` e somente uma
segunda anchor temporal e estruturalmente progressiva promove `PROVISIONAL` para `CONFIRMED`.
O estado é process-local, limitado a 1.024 bindings, oito anchors por binding e TTL de três horas.

Para binding confirmado, o atraso é a mediana das cinco anchors recentes após excluir valores
mais de 600 segundos distantes da mediana inicial. A posição temporal compara `now` com os horários
programados acrescidos desse atraso. Evidência com cinco minutos fica `STALE`; com quinze minutos,
`UNAVAILABLE`.

Posições espaciais existem somente para mapping `EXACT` ou `SUBSET_COMPATIBLE` único. A grade define
as paradas atendidas e a geometria apenas interpola o caminho entre duas paradas atendidas; estações
puladas não são acrescentadas ao run. `UNRESOLVED` mantém estimativa temporal sem ponto espacial.
Toda posição usa `IsEstimated=true` e origem `SCHEDULE_REALTIME_ESTIMATE`; nada é publicado por
SignalR nesta fase.

`RailScheduleRuntime.Enabled`, `ScheduleAwareProbesEnabled` e `SpatialEstimationEnabled` são gates
independentes e desligados por padrão. O planner apenas dá prioridade adicional a probes existentes
cuja origem coincide com a próxima parada programada; consultas equivalentes são deduplicadas por
probe. Discovery genérico, pursuit, backoff e reversal permanecem como fallback inalterado.

### Publicação estimada

`RailScheduleRuntime.PublishEstimatedPositions` é um gate independente, desligado por padrão.
Quando ligado, um provedor de snapshot combina `RailRealtimeEngine.PublicVehicles` com posições
schedule-aware confirmadas, sem criar endpoint, DTO ou SignalR novo. A arbitragem é por
`TrainCode.Trim()`: vence a fonte elegível com evidência realtime mais recente; empate preserva o
caminho antigo. A posição schedule-aware usa `RailPositionSource.ScheduleEstimated`,
`RailPositionQuality.ScheduleAnchored` e `IsEstimated=true`.

Somente bindings `CONFIRMED`, em `IN_PROGRESS`, com ponto espacial e mapping `EXACT` ou
`SUBSET_COMPATIBLE` podem entrar. O candidato expira em `LastRealtimeEvidenceUtc +
StaleAfterSeconds`; `STALE`, `UNAVAILABLE`, `BEFORE_START`, `AFTER_EXPECTED_END`, `UNRESOLVED` e
ausência de TrainCode não são publicados. Reinício começa sem candidatos schedule-aware.

## Runtime schedule-first / realtime-corrected

`RailScheduleRuntime.ScheduleFirstPublicationEnabled` habilita, de forma independente e desligada
por padrão, um lifecycle process-local por `ExpectedRunId`. Com a flag desligada, o caminho descrito
acima permanece idêntico. `ScheduledGraceAfterEndMinutes` vale 30 minutos por padrão e controla por
quanto tempo uma execução continua operacionalmente plausível depois do fim programado corrigido
pelo último atraso conhecido.

Antes, a observação realtime sustentava simultaneamente a existência e a posição pública do trem.
No modo schedule-first, a grade sustenta a existência: cada `ExpectedRun` operacional pode produzir
uma posição `ScheduledEstimated`/`ScheduleOnly`, sempre com `IsEstimated=true`, mesmo sem `TrainCode`.
Realtime apenas confirma a execução, associa o código e corrige o atraso e a posição.

O lifecycle distingue:

- `Scheduled`: existe apenas pela grade, nunca foi confirmado e tem confiança baixa;
- `ConfirmedLive`: possui evidência realtime com menos de `StaleAfterSeconds`;
- `ConfirmedEstimated`: já foi confirmado, mas a evidência deixou de ser fresca; continua projetado
  pela grade e último atraso conhecido;
- `CompletedOrExpired`: ultrapassou o fim corrigido mais a margem operacional e deixa de ser publicado.

Perder freshness não elimina a viagem. `StaleAfterSeconds` passa a separar live de estimated; não é o
TTL de existência no modo schedule-first. `UnavailableAfterSeconds` continua válido para o estimator
legado, mas não apaga uma execução confirmada ainda dentro da janela operacional.

`ExpectedRunId` é também a identidade pública determinística (`RailRunId` e `RailVehicleId`) antes e
depois do binding. `TrainCode` é atributo operacional posterior. Quando uma posição do runtime
realtime vence o merge, sua identidade pública é alinhada ao `ExpectedRunId`, evitando um segundo
ícone na transição de programado para confirmado.

Somente mappings `EXACT` e `SUBSET_COMPATIBLE` com ocorrências físicas não ambíguas produzem ponto.
Runs `UNRESOLVED` ou `CONFLICT` podem existir conceitualmente, mas não recebem coordenada inventada.
Nenhuma dessas posições é GPS: `ScheduledEstimated` indica grade pura; `ScheduleEstimated` indica
grade corrigida por evidência; `RealtimeEstimated` indica a fonte realtime mais forte disponível.

O scanner e seus limites não mudam. A atualização do lifecycle reutiliza a materialização em cache e
as respostas já obtidas; uma resposta continua alimentando todos os `TrainCode` válidos, não apenas
o alvo que motivou a consulta.
