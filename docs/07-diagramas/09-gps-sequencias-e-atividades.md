# GPS — polling, aquisição e normalização

**Objetivo:** representar provider até lote normalizado. **Nível:** sequência/atividade. **Baseline:** 2026-10-06.

```mermaid
sequenceDiagram
  participant W as GpsSppoCollectorService
  participant C as Fonte GPS primária
  participant P as Provider
  participant Q as Canal/coletor em memória
  participant G as GpsPollingService
  loop cadência configurada
    W->>C: consultar janela com overlap/catch-up
    C->>P: HTTP com timeout/budget
    alt sucesso
      P-->>C: observações
      C-->>W: DTOs normalizados
      W->>W: validar e deduplicar por veículo/timestamp
      W->>Q: publicar lote
      Q-->>G: consumir lote
    else timeout/erro
      C-->>W: status de falha
      W->>W: registrar métrica e próxima tentativa
    end
  end
```

```mermaid
flowchart LR
  A[Observação externa] --> B{coordenada, ordem e timestamp válidos?}
  B -- não --> X[rejeitar e contar motivo]
  B -- sim --> C[normalizar fonte/modal/linha]
  C --> D[deduplicar por identidade temporal]
  D --> E[encaminhar ao matching e estado causal]
```

**Participantes:** collector/client/provider/channel/polling. **Fonte:** `GpsSppoCollectorService`, sources e validators. **Limitações:** valores de janela são configuráveis; shadows comparam fontes, não provam fallback. **Uso:** seção de aquisição GPS.
