# DER — ferrovia e schedules

**Objetivo:** separar grade persistida de estado runtime. **Nível:** lógico. **Baseline:** 2026-10-06.

```mermaid
erDiagram
  RAIL_SCHEDULE_VERSION ||--o{ RAIL_SCHEDULE_PATTERN : possui
  RAIL_SCHEDULE_PATTERN ||--o{ RAIL_SCHEDULED_RUN : instancia
  RAIL_SCHEDULED_RUN ||--o{ RAIL_SCHEDULED_STOP : ordena
  LINHA ||--o{ RAIL_SCHEDULE_PATTERN : mapeia
  SENTIDO ||--o{ RAIL_SCHEDULE_PATTERN : orienta
  PADRAO_VERSAO ||--o{ RAIL_SCHEDULE_PATTERN : geometria
  PARADA ||--o{ RAIL_SCHEDULED_STOP : estacao

  RAIL_SCHEDULE_VERSION { uuid Id PK string Hash bool Ativa }
  RAIL_SCHEDULE_PATTERN { uuid Id PK uuid VersionId FK uuid LinhaId FK uuid SentidoId FK uuid PadraoVersaoId FK }
  RAIL_SCHEDULED_RUN { uuid Id PK uuid PatternId FK string TrainCode string CalendarType }
  RAIL_SCHEDULED_STOP { uuid Id PK uuid RunId FK uuid ParadaId FK int Ordem time Horario int DayOffset }
```

ExpectedRun, binding, tracker, sentinelas e estimativas são objetos em memória, não tabelas. A ativação da versão seleciona grade vigente; calendário materializa runs esperadas por data.

**Fonte:** entidades/migration RailScheduleFoundation e [modelagem ferroviária](../04-dados/04-modelagem-ferroviaria-e-schedules.md). **Limitações:** nomes/atributos resumidos; feriado não possui regra operacional completa comprovada. **Uso:** capítulo ferroviário e prevenção de confusão entre grade e inferência.
