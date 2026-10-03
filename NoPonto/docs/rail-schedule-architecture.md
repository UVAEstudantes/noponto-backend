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
