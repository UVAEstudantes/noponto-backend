# Frontend, WebView e realtime

**Objetivo:** detalhar seleção, hidratação, push/polling e renderização. **Nível:** sequência. **Baseline:** frontend `c69a92e`.

```mermaid
sequenceDiagram
  actor U as Passageiro
  participant RN as Tela/Hook React Native
  participant API as API
  participant HUB as SignalR
  participant WV as WebView
  participant ML as MapLibre
  U->>RN: buscar e selecionar linha/sentido
  RN->>API: estrutura V2 e snapshot
  API-->>RN: versão, geometria, paradas e veículos
  RN->>HUB: conectar e InscreverseLinha
  RN->>WV: updateMap(estrutura)
  WV->>ML: criar/atualizar sources e layers
  HUB-->>RN: PosicaoAtualizada
  RN->>RN: adaptar, filtrar e reconciliar por identidade/versão
  RN->>WV: updateRealtime(veículos)
  WV->>ML: animação local
  loop ferrovia
    RN->>API: polling snapshot rail
    API-->>RN: posições inferidas + idade/confiança
    RN->>WV: updateRealtime(rail)
  end
  alt desconexão SignalR
    HUB--xRN: conexão perdida
    RN->>HUB: reconexão automática
    Note over RN,HUB: reinscrição dos grupos exige validação; não é garantia universal
  end
```

`map_ready` evita updates antes de inicializar; assinaturas estruturais reduzem reconstruções. AsyncStorage reidrata seleções/preferências. **Fonte:** index/linhas, hooks, gpsHub e mapScript. **Limitações:** MapLibre/estilo dependem de fonte externa; estados de erro não são uniformes; React Query não é usado. **Uso:** integração mobile/realtime.
