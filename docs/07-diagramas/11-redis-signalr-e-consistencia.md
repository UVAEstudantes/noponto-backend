# Redis, SignalR e consistência operacional

**Objetivo:** mostrar aceite, efeitos secundários e ausência de transação distribuída. **Nível:** sequência/recuperação. **Baseline:** 2026-10-06.

```mermaid
sequenceDiagram
  participant G as GPS polling
  participant R as Redis CAS
  participant V as Viagem operacional
  participant P as PostgreSQL
  participant T as Telemetria/streams
  participant H as SignalR Hub
  participant A as App
  G->>R: Lua CAS(timestamp, snapshots, índices, TTL)
  alt rejeitado
    R-->>G: antigo/igual/conflito
  else aceito
    R-->>G: snapshot atualizado atomicamente no Redis
    G->>V: aplicar posição aceita
    V->>P: transação viagem + outbox
    G->>T: enqueue não bloqueante/stream
    G->>H: publicar grupo da linha
    H-->>A: PosicaoAtualizada
  end
  Note over R,P: não existe transação distribuída Redis–PostgreSQL
```

```mermaid
flowchart TD
  F{falha} -->|Redis| R1[realtime/CAS indisponível; estado quente pode sumir]
  F -->|PostgreSQL| P1[viagem e persistência falham/degradam]
  F -->|telemetria| T1[hot path tenta continuar; backlog/perda conforme estágio]
  F -->|SignalR| S1[cliente perde push; reconexão automática]
  S1 --> S2[reinscrição por linha não é garantida em todos os casos]
```

Redis é localmente atômico pelo script, mas sem RDB/AOF. Outbox protege efeitos PostgreSQL posteriores, não sincroniza Redis. **Fonte:** repository cache/Lua, polling, outbox e Hub. **Limitações:** ordem entre efeitos é abstraída; recovery não foi testada ponta a ponta. **Uso:** consistência, falhas e trade-offs.
