# C4 — containers e responsabilidades

**Objetivo:** mostrar unidades executáveis/de dados sem chamar o monólito de microsserviços. **Nível:** C4 Container. **Baseline:** 2026-10-06.

```mermaid
flowchart LR
  APP[App Expo React Native] -->|HTTP JSON e SignalR| API[API ASP.NET Core .NET 9\nREST, Hub e workers internos]
  API -->|SQL e PostGIS| PG[(PostgreSQL 16 + PostGIS 3.4\ndurável)]
  API -->|RESP| RD[(Redis 7\nefêmero)]
  API -->|HTTP| EXT[Providers externos]
  APP -->|WebView| MAP[MapLibre GL JS]
  MAP -->|tiles e estilo| CART[Fontes cartográficas]
  API -.->|HTTP legado, serviço OFF| ML[FastAPI + XGBoost legado]
  OFF[Importadores e builders offline] -->|artefatos/transações controladas| PG
```

**Relações:** API concentra controllers, Hub e BackgroundServices; PostgreSQL é autoridade durável; Redis mantém projeções/streams sem RDB/AOF; ML antigo está desligado. Ferramentas offline não são serviços permanentes.

**Fonte:** Dockerfile, Compose, Program e frontend. **Limitações:** publicação de portas e ferramentas do host aparecem em 05; ETA V2 fica dentro da API. **Uso:** capítulo de arquitetura lógica.
