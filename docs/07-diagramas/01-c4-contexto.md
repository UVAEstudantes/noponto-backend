# C4 — contexto do NoPonto

**Objetivo:** situar passageiro, NoPonto e fontes externas. **Nível:** C4 Contexto. **Baseline:** 2026-10-06.

```mermaid
flowchart LR
  P[Passageiro] -->|consulta linhas, mapas e veículos| N[NoPonto]
  N -->|informação estrutural e operacional| P
  R[Providers rodoviários] -->|GPS ônibus e BRT| N
  F[Provider ferroviário] -->|ETA entre estações e evidências| N
  E[Fontes estruturais] -->|GTFS, ArcGIS e schedules| N
  C[Fontes cartográficas] -->|mapas e estilos| N
  B[GitHub, GHCR e EAS] -.->|build e artefatos| N
```

**Participantes/relações:** o passageiro usa o produto; providers entregam dados heterogêneos; fontes estruturais alimentam importações; cartografia sustenta visualização; serviços de build não são parte funcional. PostgreSQL/Redis ficam fora deste nível.

**Fonte:** arquitetura 02, integrações externas e código de clients. **Limitações:** metrô, Rotas e Rotinas não aparecem porque são planejados; painel admin está fora do escopo. **Uso na monografia:** visão inicial do sistema e fronteiras externas.
