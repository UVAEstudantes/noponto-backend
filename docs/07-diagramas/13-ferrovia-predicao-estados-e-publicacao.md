# Ferrovia — predição, estados e publicação

**Objetivo:** mostrar estados confirmados e transformação schedule/evidência em posição inferida. **Nível:** estados/fluxo. **Baseline:** 2026-10-06.

```mermaid
stateDiagram-v2
  [*] --> Unresolved
  Unresolved --> AwaitingDeparture: run futuro resolvido
  AwaitingDeparture --> Dwell: passagem/âncora na estação
  AwaitingDeparture --> InSegment: janela de partida
  Dwell --> InSegment: próximo trecho
  InSegment --> Dwell: estação intermediária
  InSegment --> TerminalHold: terminal alcançado
  TerminalHold --> Ended: retenção terminal concluída
  InSegment --> Unresolved: evidência incompatível/estimativa indisponível
```

```mermaid
flowchart LR
  SCH[horários programados] --> PRED[preditor temporal]
  EVI[evidência recente e atraso] --> PRED
  PRED --> POS[estado, stop anterior/próximo e progresso]
  GEO[PadraoVersao/LineString] --> EST[estimador espacial]
  POS --> EST
  EST --> ENG[RailRealtimeEngine]
  ENG --> SNAP[snapshot Live, Estimated ou Scheduled]
  SNAP --> HTTP[endpoint HTTP]
  HTTP --> POLL[polling frontend]
  POLL --> MAP[hidratação, MapLibre e animação local]
```

Em paralelo, o tracker de evidências usa `New → Active → Stale`; o coordenador adaptativo usa `Discovery`, `Acquisition`, `Tracked` e `Reacquisition`. São máquinas distintas e não foram fundidas. Qualidade/origem acompanham posição; freshness impede extrapolação indefinida.

**Fonte:** `RailRealtimeModels`, tracker, estimator/engine e frontend. **Limitações:** algumas transições dependem de condições compostas; posição é inferência. **Uso:** explicar “realtime ferroviário” sem sugerir GPS.
