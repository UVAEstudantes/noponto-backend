# Modelagem ferroviária e schedules

**Análise:** 2026-10-06 · **Commit:** `53567bd` · **Estado:** tabelas/importador/runtime verificados; nenhuma construção ou ativação executada.

## Finalidade e conceitos

A grade ferroviária é uma camada temporal durável ligada à estrutura espacial V2. Ela não armazena posição realtime. Uma versão contém patterns, runs e stops; runtime materializa expected runs e combina-os com evidências do provider.

Matematicamente, um pattern `P=(s₁,…,sₙ)` é sequência ordenada de estações. Um run `R=(P,c,t₁,…,tₙ)` instancia o pattern num calendário `c` e horários. Dois runs podem compartilhar `P` e diferir apenas nos horários. Serviço expresso que pula estações possui assinatura/pattern diferente; short start começa em subsequência e pode mapear `SUBSET_COMPATIBLE` ao padrão V2 mais amplo.

## Modelo físico

`RailScheduleVersion` pertence a uma `Linha`; SHA-256 do dataset gera `ContentHash`. Unique `(LineId,ContentHash)` torna reimportação idempotente e unique parcial em `LineId WHERE IsActive` limita uma versão ativa. Datas weekday/saturday/sunday são referências observadas, não regras universais de feriado.

`RailSchedulePattern` pertence à versão, linha e sentido; tem external id/assinatura, número de estações e mapeamento opcional a `PadraoVersao`. Estados permitidos: `EXACT`, `SUBSET_COMPATIBLE`, `UNRESOLVED`, `CONFLICT`. Só os dois primeiros sustentam projeção espacial.

`RailScheduledRun` referencia versão e pattern, linha/sentido, calendário, partida/chegada `TimeOnly`, day offsets, terminais, short start, midnight, confiança/status e data-fonte. `DepartureDayOffset≥0` e chegada não precede offset da partida.

`RailScheduledStop` referencia run e `Parada`; `StopSequence`, `StationOrder`, hora, day offset e minuto absoluto. O check impõe:

```text
AbsoluteMinute = hour(ScheduledTime)×60 + minute(ScheduledTime) + DayOffset×1440
```

Unique run+sequência ordena; unique run+parada rejeita repetição dentro do run ferroviário importado.

## Importação, ativação e transação

`RailScheduleDatasetLoader` valida schema/status/cardinalidade/referências/monotonicidade antes do banco. `RailScheduleImportService.ImportAsync` abre transação, resolve unicamente linha, sentidos e estações pela estrutura V2, reutiliza conteúdo já importado, cria IDs determinísticos e mapeia patterns. `activate=true` desativa versões anteriores da linha e ativa a nova na mesma transação. Falha reverte a unidade PostgreSQL; não altera Redis.

Cascade remove filhos de versão/run, enquanto referências a linha/sentido/parada/versão espacial usam `Restrict`. Isso protege estrutura compartilhada, mas uma exclusão deliberada da schedule version remove sua grade.

## Expected runs e runtime

`ExpectedRunService` lê versão ativa e materializa `(schedule version, scheduled run, service date)` em memória. Portanto não existe tabela `ExpectedRuns`. Cache limitado também é em memória. Binding, tracker, estimativas e snapshots igualmente não usam Redis no código vigente; reinício reconstrói grade e perde correlações quentes.

## Calendário e limites

Há somente `WEEKDAY`, `SATURDAY`, `SUNDAY`; feriados/exceções não existem. Day offset e consulta da data anterior preservam meia-noite. Short starts e subset-compatible são explícitos. Conflitos/ambiguidade impedem posição espacial. Ausência de GPS continua característica do modal.

## Estado produtivo e volumes

Auditoria anterior: uma versão ativa, 23 patterns, 246 runs e 7.433 stops de Santa Cruz. Japeri FULL foi coletada localmente, mas builder/auditoria/importação/ativação permanecem etapas distintas. Não houve nova consulta produtiva.

## Índices, testes, evidências e pendências

Índices principais: linha+hash, ativa parcial, versão+external pattern, versão+status, versão+calendário+sentido, calendário+partida e uniques por stop. Favorecem carga da versão, materialização e integridade; ganhos não foram benchmarkados. Referências: `railSchedule.cs`, `DbContext.ConfigurarRailSchedule`, migration `RailScheduleFoundation`, import/runtime e testes `RailScheduleImportTests`, `RailScheduleRuntimeTests`, `ExpectedRunBindingTests`. Não executados.

