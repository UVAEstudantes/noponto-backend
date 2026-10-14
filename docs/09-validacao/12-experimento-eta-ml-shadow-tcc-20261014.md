# ETA/ML Shadow — experimento local para TCC

Implementado laboratório executável em `tools/eta_ml/tcc`, sem integração à DI
ou ao polling público. Reutiliza fontes existentes entre worktrees por import
explícito de laboratório; nenhum arquivo GPS antigo foi transportado.

## Estado efetivamente encontrado

| Projeto | Branch / revisão | Implementação presente |
|---|---|---|
| Backend ativo | fix/gps / 6f64538 | GPS 4C.5–4C.9 e físico/parada consultada locais; sem Shadow registrado |
| Backend MVP | feature/eta-ml-mvp / 5a13e52 | integração histórica anterior ao GTFS-RT, tracked limpo |
| Backend Shadow atual | feature/eta-shadow-gps-current / 6f64538 | adapter, worker, fila, estatísticas, exportador e evaluator locais |
| ML | feature/eta-history-mvp / 6c0f7b5 | pipeline ExtraTrees/baselines/qualificação e FastAPI; outputs locais preexistentes |
| Frontend | ajustes | trabalho local de startup e presença preservado, sem alteração nesta tarefa |

Branch, status, staged, untracked e diffs foram inventariados antes das edições.
Nenhum staged encontrado. Working trees sujos foram tratados como legítimos.

## Resultado executável

Saída final: `D:/repositorio_github/NoPonto/tcc-shadow-demo-20261014-04`.
Comando e metodologia completos em `tools/eta_ml/tcc/README.md`.

- Treino e repetição com seed 20261007: 1.920 observações sintéticas, 240 viagens,
  cinco dias. TRAIN 1.152/144 viagens, VALIDATION 384/48, TEST 384/48. Viagens
  disjuntas, ordem cronológica e estatísticas/preprocessamento somente TRAIN.
- FastAPI real em loopback: inferência para todas as observações, lotes <=200,
  digest e correlação conferidos; entrada com destino inválido recusada HTTP422.
- Teste C# usa `PhysicalEta.Apply` e `HistoricalEtaAdapter.Capture/Payload` reais,
  mesma observação/ocorrência/distância fixture de 1.000m, média causal36km/h:
  físico100s, ExtraTrees169,6013s, mediana histórica TRAIN170,6881s. Sem label
  real desse bridge: diferenças entre métodos não são erros de chegada.
- Modelo/repetição SHA-256:
  `9c0c374df867c448ca569715df572a8b7dab769bb7f0adcb973081f20eeecf6d`.
  As duas execuções de treino produziram previsões idênticas e mesmo digest.
- Python3.13.14, sklearn1.6.1, numpy2.2.6, scipy1.16.1. Fontes, revisões,
  paths sujos, configuração e hashes registrados em `experiment.json`.

## Métricas exclusivamente sintéticas

TEST comum: 288 observações/48 viagens/4 ocorrências. Sem alegação de precisão
no Rio. O gerador existente cria labels independentes por observação, não uma
simulação física consistente de chegada única por viagem/ocorrência.

| Método | MAE s (min) | MedAE s | P90 s | Cobertura individual TEST |
|---|---:|---:|---:|---:|
| Físico causal | 7,04 (0,117) | 5,54 | 18,50 | 288/384 =75% |
| Histórico TRAIN | 38,90 (0,648) | 32,45 | 81,24 | 384/384 =100% |
| ExtraTrees HTTP | 6,04 (0,101) | 3,84 | 12,88 | 384/384 =100% |

Na cobertura individual, ExtraTrees MAE8,04s e histórico38,71s: não confundir
com a comparação comum. No conjunto comum, dentro de60/120s: físico100%/100%,
histórico80,90%/95,83%, ExtraTrees100%/100%. JSON inclui segundos/minutos,
recortes por horizonte/linha/modal e divergência entre previsões separada dos
erros contra labels. Recortes exigem >=30 observações e >=3 viagens.

## Validação efetivamente executada

- **56 C# aprovados**: bridge integrado, Shadow original e físico MVP/parada
  consultada; inclui recusa de sentido/versão/destino, resposta sintética pelo
  worker normal, indisponibilidade, timeout, fila cheia e invariantes públicos.
- **40 C# aprovados** na worktree Shadow: polling BUS/BRT individual/lote, CAS,
  eventos/telemetria com Shadow OFF ou indisponível e falhas de exportação.
- **41 Python aprovados**, suíte ML existente: inferência, falta de modelo,
  contrato/hash, qualificação, separação temporal, gates e pipeline.
- **9 Python novos aprovados**: identidade integral, incompatibilidades,
  duplicatas, ausência de label, proibição de certificar reais, fórmula física,
  cobertura comum/individual, métricas e releitura após reinício.
- Sem PostgreSQL/Redis, Docker, fontes operacionais, homeserver ou produção.
  Avisos de compilação preexistentes mantidos; nenhum teste ignorado nos lotes C#.

## Coleta real e bloqueios concretos

O sink Shadow existente é limitado e transitório; o exportador opcional na
outra worktree já retém JSONL limitado em diretório explicitamente configurado.
O evaluator existente associa exatamente observação/viagem/volta/estrutura/
timestamp com manifesto/hash e auditoria externa; não por proximidade temporal.
O laboratório separa previsões e labels e **recusa dados reais**, não emite
auditoria ou certificação. Nenhuma PassagemParada foi promovida a label.

Pendências para homologação real:

1. Alvo: Shadow captura próxima ocorrência **operacional**, físico atende também
   parada consultada. Selecionar o mesmo destino validado antes de integrar.
2. Distância: compartilhar a distância longitudinal efetivamente validada;
   conferir diferença entre substring geográfica Shadow e prefixos do matching.
3. Modelo: worker normal exige `X-Model-Data-Kind=real`; demonstração faz HTTP
   pelo adapter e mantém essa proteção intacta. Modelo real qualificado ausente.
4. Dados: sem dataset real auditado elegível fornecido. Fechamento06 não comprovou
   amostras certificadas; gates reais e regime pós-correção das passagens continuam
   exigidos. O relatório de insuficiência fica também em `experiment.json`.
5. Registrar os três métodos no mesmo alvo: `HistoricalEta` do sink atual é a
   saída ExtraTrees, não a mediana TRAIN. Preparar captura comparativa offline
   somente após resolver os itens anteriores, sem trocar o ETA público.

## Alterações e preservação

Novos: `experiment.py`, `test_experiment.py`, `ShadowTccTests.cs`, `Lab.targets`,
`README.md` sob `tools/eta_ml/tcc/`, e este relatório. Único arquivo preexistente
alterado: `NoPonto/HISTORICO.md`, somente por acréscimo. Nenhuma alteração em
fontes funcionais, .csproj, flags, ML, frontend ou outras worktrees.

Inventário SHA anterior: `D:/repositorio_github/NoPonto/tcc-shadow-before.json`
(119 arquivos). Antes do acréscimo ao histórico, os119 hashes permaneceram
idênticos após testes/treinos. Conferência final em
`D:/repositorio_github/NoPonto/tcc-shadow-preservation.json`.
Saídas01/02 são execuções intermediárias preservadas. A03 concluiu testes e
métricas, mas foi interrompida na enumeração de proveniência de fontes ignorados;
o executor passou a enumerar apenas fontes Git versionados/não ignorados.
Usar04 como entrega final, com manifesto completo.
Sem reset/clean/stash/checkout/cherry-pick/merge/commit/push ou deploy.
