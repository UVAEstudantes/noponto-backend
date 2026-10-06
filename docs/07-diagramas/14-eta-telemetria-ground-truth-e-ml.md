# ETA, telemetria, ground truth e ML

**Objetivo:** separar legado, fundação V2 e pipeline supervisionado planejado. **Nível:** sequência/estado/dados. **Baseline:** ETA V2 OFF em produção.

```mermaid
sequenceDiagram
  participant G as GPS enriquecido
  participant C as GpsEtaClient
  participant L as FastAPI/XGBoost legado OFF
  G->>C: lote de posições
  C->>L: HTTP timeout 3 s
  alt serviço responde
    L-->>C: previsões legadas
  else indisponível/timeout
    C-->>G: vazio + cooldown fail-open
  end
  Note over G,L: serviço desligado intencionalmente na produção
```

```mermaid
flowchart LR
  A[GPS aceito + viagem] --> EL{elegível e flags ETA V2?}
  EL -- não --> N[sem previsão V2]
  EL -- sim --> BASE[baseline/canary/shadow]
  BASE --> CH[channel bounded]
  CH --> BW[BatchWorker]
  BW --> PG[(PrevisoesEtaV2)]
  PASS[passagem da ocorrência-alvo] --> CLOSE[fechar por viagem, versão, ocorrência e volta]
  CLOSE --> PG
  MW[MaintenanceWorker] -->|expirar/contar pending| PG
```

```mermaid
stateDiagram-v2
  [*] --> PENDENTE: previsão persistida
  PENDENTE --> REALIZADA: passagem-alvo válida fecha ground truth
  PENDENTE --> EXPIRADA: prazo excedido
  PENDENTE --> INVALIDADA: identidade/contexto inválido
```

```mermaid
flowchart LR
  GPS[GPS] --> SAMPLE[sampling determinístico]
  SAMPLE --> C[channel com backpressure]
  C --> RS[Redis Stream sem persistência]
  RS --> W[consumer worker]
  W -->|batch OK + ACK| DB[(TelemetriasVeiculoMl)]
  W -->|retry/pending| RS
  W -->|limite de falhas| DLQ[DLQ Redis]
  HIST[dados históricos] -.-> QUAL[qualidade/manifesto] -.-> DATA[dataset] -.-> SPLIT[split temporal] -.-> TRAIN[treino] -.-> EVAL[avaliação] -.-> PROM[promoção]
```

O último encadeamento é planejado, não pipeline supervisionado integral. Redis restart pode perder stream/pending/DLQ. **Fonte:** GpsEtaClient, EtaV2*, TelemetriaMl* e docs 13–19. **Limitações:** transições ETA devem ser confirmadas por testes ao publicar resultados; ground truth pode ser interpolado. **Uso:** evolução ML e rigor experimental.
