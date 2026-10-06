# Ferrovia — coleta, importação, scanner e binding

**Objetivo:** representar preparação offline e aquisição runtime. **Nível:** atividades/sequência. **Baseline:** 2026-10-06.

```mermaid
flowchart LR
  CFG[Configuração de coleta] --> SRC[Provider/artefatos]
  SRC --> N[Normalizar e deduplicar]
  N --> CP[Checkpoints]
  CP --> B[Builder]
  B --> VAL{validação estrutural e temporal}
  VAL -- falha --> ERR[rejeitar dataset]
  VAL -- ok --> ART[artefato versionado]
  ART --> LOAD[Loader/import command]
  LOAD --> TX[transação PostgreSQL]
  TX --> VER[ScheduleVersion, Patterns, Runs e Stops]
  VER --> ACT[ativação explícita]
```

```mermaid
sequenceDiagram
  participant G as Schedule Gate
  participant E as ExpectedRunService em memória
  participant S as Scheduler/Sentinelas
  participant B as Budget + SingleFlight
  participant P as Provider ferroviário
  participant X as Binding
  G->>E: materializar runs na janela
  E-->>S: probes candidatos
  S->>B: selecionar discovery/pursuit/reacquisition
  B->>P: consulta limitada
  P-->>B: evidências ou estado de falha
  B-->>X: observações normalizadas
  X->>X: filtrar linha, sentido, tempo e pattern
  alt candidato único/consistente
    X-->>E: binding provisional ou confirmed
  else ambíguo/conflitante
    X-->>E: não associar
  end
```

Expected runs/bindings são memória reconstruível. Scanner usa gate, sentinelas, discovery, pursuit, backoff e reacquisition para limitar requests. **Fonte:** Schedule services e TremRealtime. **Limitações:** coleta offline possui ferramentas/artefatos externos; feriados incompletos. **Uso:** pipeline ferroviário antes da inferência.
