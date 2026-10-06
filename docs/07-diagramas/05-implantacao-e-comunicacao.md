# Implantação e comunicação

**Objetivo:** representar produção e cadeia de artefatos sem inventar proxy/TLS/firewall. **Nível:** implantação. **Baseline:** fotografia 2026-10-06.

```mermaid
flowchart TB
  PHONE[Dispositivo móvel] -->|HTTP e SignalR pela rede| HOST
  PROVIDERS[Providers externos] <-->|HTTP HTTPS| HOST
  subgraph HOST[Host Debian 12]
    subgraph DOCKER[Docker Engine e rede noponto_default]
      API[noponto_api :8080]
      PG[transporte_postgres :5432]
      RD[transporte_redis :6379]
      API --> PG
      API --> RD
    end
    PV[(volume PostgreSQL)] --> PG
    RV[(volume Redis\nsem persistência lógica)] --> RD
    SOCK[Docker socket] --- API
  end
  GHA[GitHub Actions] -->|imagem| GHCR[GHCR]
  GHCR --> DOCKER
  GHA -.->|Tailscale SSH deploy manual| HOST
  EAS[EAS Build] -->|APK preview| PHONE
```

Portas API, PostgreSQL e Redis estavam publicadas no host; alcance externo, TLS, firewall e reverse proxy não foram confirmados. Redis possui volume, mas RDB/AOF estão desabilitados. Portainer/Dozzle e stacks externas são compartilhados, não componentes funcionais.

**Fonte:** Compose/workflows e infraestrutura 05. **Limitações:** medição pontual; topologia pública não inferida. **Uso:** implantação, riscos e capacidade.
