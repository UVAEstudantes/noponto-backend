# Plano experimental — ferrovia

**Finalidade:** avaliar cobertura, binding e estimativas sem confundir animação com observação física.

## Cobertura de testes inventariada

`RailScheduleImportTests` cobre importação; classes TremRealtime Phase 1–4 cobrem provider fake, budget, sentinelas, scanner, tracker, topologia, pursuit/reacquisition e fairness; `RailScheduleRuntimeTests` cobre atraso, interpolação, estados, meia-noite, repetição e mapping; publication/controller tests cobrem snapshot e contrato; testes PostGIS de topologia exigem DB.

Nesta etapa, `RailScheduleRuntimeTests` e `TremRealtimePhase3TrackerTests` participaram do filtro local aprovado. Importação, PostGIS, scanner completo e frontend não foram executados. A coleta Japeri FULL é artefato de coleta e não prova importação/ativação.

## Unidades de avaliação

- schedule version/pattern/run/stop por data operacional;
- expected run materializada;
- evidência de provider por train code/par de estações;
- binding provisional/confirmed/ambiguous;
- estimativa temporal/espacial com source, quality e freshness;
- snapshot público e renderização.

## Procedimento futuro

1. congelar schedule ativo, estrutura e timezone;
2. selecionar dias/linhas/janelas com evidência autorizada;
3. criar referência independente para identidade/passagem; se não houver posição física, não calcular erro espacial;
4. reproduzir provider por corpus offline para determinismo;
5. executar gate/scanner/binding/tracker/engine com relógio controlado;
6. medir chamadas e estados durante atraso, no-service, timeout e midnight;
7. validar snapshot e UI separadamente.

## Métricas

- cobertura de runs esperadas e publicadas;
- precisão/recall do binding e taxa ambígua;
- tempo até acquisition/confirmed e reacquisition;
- idade da evidência, tempo em stale e publicação indevida;
- MAE/MedAE/P90 temporal de passagens, se ground truth existir;
- erro espacial somente com referência física adequada;
- acerto da próxima estação/estado;
- requests por período, hits úteis, rate limiting e fairness;
- disponibilidade degradada sem provider.

## Tabelas vazias

| Schedule/hash | Runs esperadas | Cobertas | Confirmadas | Ambíguas | Publicadas | Requests | Stale indevido |
|---|---:|---:|---:|---:|---:|---:|---:|
| `[PENDENTE]` |  |  |  |  |  |  |  |

| Horizonte | N | erro temporal MAE | MedAE | P90 | cobertura | origem do ground truth |
|---|---:|---:|---:|---:|---:|---|
| `[PENDENTE]` |  |  |  |  |  |  |

## Limitações e aceite

Schedule-only mede coerência temporal, não localização real. Uma coordenada interpolada é resultado do sistema, não ground truth. Prontidão exige corpus offline, referência independente, schedule importado em DB descartável e critérios aprovados.

Relacionados: [fluxo rail](../03-funcionalidades/07-realtime-ferroviario-fluxo-completo.md) e [diagramas](../07-diagramas/12-ferrovia-coleta-scanner-e-binding.md).
