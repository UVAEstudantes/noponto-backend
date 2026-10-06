# Componentes do backend

**Objetivo:** decompor a API modular e seus fluxos principais. **Nível:** componentes. **Baseline:** 2026-10-06.

```mermaid
flowchart TB
  subgraph API[Processo NoPonto API]
    REST[Controllers REST] --> APP[Serviços de aplicação]
    HUB[GpsHub SignalR]
    GPS[GPS polling e normalização] --> MATCH[Matching e estado causal]
    MATCH --> TRIP[Viagem operacional]
    RAIL[Ferrovia schedule-first\ngate, scanner, binding, tracker]
    ETA[ETA legado fail-open\nETA V2 experimental OFF]
    TEL[Telemetria e histórico]
    WORK[Hosted services e channels]
    STRUCT[Estrutura V2 e imports]
    GPS --> ETA
    MATCH --> TRIP --> TEL
    WORK --> GPS
    WORK --> RAIL
    WORK --> TEL
    APP --> STRUCT
    GPS --> HUB
  end
  MATCH -->|SQL espacial| PG[(PostgreSQL PostGIS)]
  TRIP --> PG
  STRUCT --> PG
  RAIL --> PG
  TEL --> PG
  GPS --> RD[(Redis efêmero)]
  TEL --> RD
  GPS --> EXT[Providers]
  RAIL --> EXT
```

REST, GPS, ferrovia e workers compartilham processo, CPU, memória, pools e falha. Camadas `1-API`, `2-Application`, `3-Domain` e `4-Data` organizam responsabilidades, mas não são unidades de deploy.

**Fonte:** Program, services/repositories e [componentes](../02-arquitetura/03-componentes-backend-frontend.md). **Limitações:** classes auxiliares foram agrupadas; flags determinam execução. **Uso:** implementação e trade-off do monólito modular.
