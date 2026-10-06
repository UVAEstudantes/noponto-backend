# CI/CD, armazenamento e recuperação

**Objetivo:** unir cadeia de build e fronteiras reais de recuperação. **Nível:** fluxo de implantação/estado. **Baseline:** 2026-10-06.

```mermaid
flowchart LR
  CODE[push main ou manual] --> GHA[GitHub Actions]
  GHA --> BUILD[Buildx .NET 9 linux/amd64]
  BUILD --> GHCR[GHCR: latest + SHA/time + build]
  GHCR -->|somente deploy manual| SSH[Tailscale + SSH + script]
  SSH --> API[noponto_api usa latest]
  MOBILE[push main/manual frontend] --> EAS[EAS preview]
  EAS --> APK[APK interno]
  T[tests/lint/security/smoke] -.->|não existem como gates comprovados| GHA
  RB[rollback automatizado] -.->|não comprovado| SSH
```

```mermaid
flowchart TD
  FAIL{evento} --> APIREST[restart API]
  FAIL --> RDREST[restart Redis]
  FAIL --> PGFAIL[PostgreSQL indisponível]
  FAIL --> CHFULL[channel cheio]
  APIREST --> A1[perde channels, caches e trackers; DB/Redis podem permanecer]
  RDREST --> R1[perde snapshots, locks, streams, groups e pending]
  PGFAIL --> P1[matching/persistência/viagem degradam ou falham]
  CHFULL --> C1[Wait aplica backpressure ou fila específica descarta]
  A1 --> REBUILD[reconstrução por schedule e novas evidências]
  R1 --> REBUILD
  PGFAIL --> RESTORE[backup/restore: procedimento não comprovado]
```

PostgreSQL/volume são duráveis, mas volume não é backup. Outbox é transacional no banco; não há transação distribuída. **Fonte:** workflows, Compose e docs 05/07. **Limitações:** script remoto, restore, RPO/RTO e smoke test não verificados. **Uso:** implantação, confiabilidade e limitações.
