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
