# Matching, estado causal e viagem operacional

**Objetivo:** integrar decisão espacial, correção temporal e ciclo de viagem. **Nível:** atividades/estados. **Baseline:** 2026-10-06.

```mermaid
flowchart TD
  GPS[Posição normalizada] --> CAND[Selecionar padrões candidatos por linha/hints]
  CAND --> GEO[Filtro ST_DWithin e projeção ST_LineLocatePoint]
  GEO --> DIR[Comparar bearing e continuidade]
  DIR --> SCORE[Score e proteção contra ambiguidade]
  SCORE --> SEL{candidato seguro?}
  SEL -- não --> RAW[aceite possível sem enriquecimento]
  SEL -- sim --> FRAC[fração longitudinal e ponto projetado]
  FRAC --> NEXT[próxima ocorrência considerando volta]
  NEXT --> CAUSAL[comparar timestamp e histórico causal]
  RAW --> CAUSAL
  CAUSAL -->|antiga, impossível ou conflito| REJ[rejeitar]
  CAUSAL -->|válida/corrigida| ACC[aceitar e atualizar estado]
  ACC --> TRIP[aplicar regra de viagem]
```

```mermaid
stateDiagram-v2
  [*] --> Ativa: viagem criada
  Ativa --> Ativa: progresso/passagens
  Ativa --> PossivelFim: terminal detectado
  PossivelFim --> Ativa: saiu do terminal sem confirmação
  PossivelFim --> PossivelFim: confirmação pós-terminal insuficiente
  PossivelFim --> Finalizada: confirmações exigidas
  Finalizada --> Ativa: nova viagem identificada
```

O matching combinado é set-based quando flag habilitada. A viagem fixa `PadraoVersao`, emite passagens idempotentes e não regride por jitter. A transição pós-finalização cria/reconhece nova viagem segundo identidade/estrutura; a figura resume condições, não substitui `ViagemOperacionalRegra`.

**Fonte:** repositories PostGIS, correção temporal e `ViagemOperacional.cs`. **Limitações:** thresholds/configurações omitidos; match inconclusivo não é posição falsa. **Uso:** algoritmo rodoviário e máquina de estados.
