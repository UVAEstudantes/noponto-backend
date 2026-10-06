# Auditoria e validação dos diagramas

**Data:** 2026-10-06. **Execução:** backend HEAD `2d57ea8`; código funcional equivalente à baseline `53567bd`; frontend `c69a92e`. **Escopo:** 17 arquivos e 26 blocos Mermaid.

## Método e resultado geral

Não foram encontrados `mmdc`, pacote Mermaid local nem extensão VS Code capaz de renderizar sem instalação. Nenhuma dependência foi instalada. Foi realizada validação estática automatizada das cercas, declaração inicial e tipos, mais revisão semântica contra código, migrations e documentação reconciliada.

- 26/26 blocos com cerca fechada e tipo reconhecido (`flowchart`, `sequenceDiagram`, `stateDiagram-v2`, `erDiagram`);
- links locais válidos;
- workers dentro da API; Redis efêmero; PostgreSQL durável;
- ETA legado OFF e ETA V2 OFF distinguidos;
- ferrovia identificada como inferência;
- ExpectedRun, binding e trackers fora dos DERs;
- nenhuma correção comprovadamente necessária encontrada nesta etapa.

**Validação visual:** `PENDENTE_SEM_RENDERIZADOR`. Não se afirma aprovação de layout, cruzamentos ou legibilidade renderizada.

## Relatório por arquivo

| Arquivo | Qtde/tipo | Sintaxe estática | Visual | Semântica/resultado | Correção/pendência |
|---|---:|---|---|---|---|
| 00 catálogo | 0 | n/a | n/a | convenções coerentes | nenhuma |
| 01 contexto | 1 flowchart | aprovada | pendente | fronteiras C4 corretas | renderizar |
| 02 containers | 1 flowchart | aprovada | pendente | monólito/DB/Redis/ML corretos | renderizar |
| 03 backend | 1 flowchart | aprovada | pendente | workers internos e subsistemas corretos | avaliar densidade visual |
| 04 frontend | 1 flowchart | aprovada | pendente | hooks próprios; React Query ausente | renderizar |
| 05 implantação | 1 flowchart | aprovada | pendente | sem proxy/TLS inventado | renderizar |
| 06 DER V2 | 1 erDiagram | aprovada | pendente | cardinalidades núcleo coerentes | revisar tamanho em página |
| 07 DER rail | 1 erDiagram | aprovada | pendente | runtime em memória excluído | renderizar |
| 08 DER operacional | 1 erDiagram | aprovada | pendente | refs lógicas explicitadas | confrontar nomes físicos na versão final |
| 09 GPS | 2 sequence/flow | aprovada | pendente | aquisição/erro coerentes | renderizar |
| 10 matching/viagem | 2 flow/state | aprovada | pendente | estados reais confirmados | renderizar |
| 11 Redis/SignalR | 2 sequence/flow | aprovada | pendente | sem transação distribuída | renderizar |
| 12 coleta/binding rail | 2 flow/sequence | aprovada | pendente | ExpectedRun em memória | renderizar |
| 13 predição rail | 2 state/flow | aprovada | pendente | máquinas distintas preservadas | revisar legibilidade |
| 14 ETA/telemetria | 4 sequence/flow/state | aprovada | pendente | planejado versus OFF explícito | provável divisão na monografia |
| 15 frontend realtime | 1 sequence | aprovada | pendente | reconexão sem garantia de reinscrição | renderizar |
| 16 CI/recovery | 2 flowcharts | aprovada | pendente | gaps de testes/rollback corretos | renderizar |

## Rastreabilidade semântica

Estados confrontados: viagem `Ativa/PossivelFim/Finalizada`; rail runtime `Unresolved/AwaitingDeparture/Dwell/InSegment/TerminalHold/Ended`; tracker `New/Active/Stale`; adaptive tracking `Discovery/Acquisition/Tracked/Reacquisition`; ETA `PENDENTE/REALIZADA/EXPIRADA/INVALIDADA`. DERs foram confrontados com os documentos de migrations, não com o README histórico.

## Protocolo visual futuro

Usar Mermaid CLI ou renderizador institucional já aprovado; registrar versão; renderizar cada bloco para SVG; verificar clipping, fontes, contraste, direção, cardinalidades e tamanho A4; arquivar hash do Markdown/SVG. Falha visual não autoriza mudança sem comparar semântica.

Relacionados: [catálogo](../07-diagramas/00-catalogo-convencoes-e-rastreabilidade.md) e [relatório consolidado](09-relatorio-consolidado-de-validacao.md).
