# Inventario e proposta de limpeza — 2026-10-15
Nenhuma branch/worktree foi excluida ou limpa. Inventario anterior a esta PR, com status/index e hashes dos arquivos WIP guardados fora do Git.
| Workspace | Branch | Arquivos WIP | Stage |
|---|---|---:|---|
| .github | main | 0 | nao |
| ml | feature/eta-history-mvp | 9 | nao |
| noponto-adm | develop | 46 | nao |
| noponto-backend | main | 70 | nao |
| noponto-backend-eta-ml-mvp | feature/eta-ml-mvp | 0 | nao |
| noponto-backend-eta-shadow-gps-current | feature/eta-shadow-gps-current | 43 | nao |
| noponto-backend-gps-diagnostics-4c4 | release/gps-diagnostics-4c4 | 31 | nao |
| noponto-backend-mvp-candidate-20261014 | release/mvp-local-20261014 | 0 | nao |
| noponto-backend-physical-eta-pr-20261015 | fix/physical-eta-recent-capture-20261015 | 0 | nao |
| noponto-docs | main | 0 | nao |
| noponto-frontend | ajustes | 21 | nao |
| noponto-frontend-mvp-candidate-20261014 | release/mvp-presence-local-20261014 | 0 | nao |
| noponto-frontend-mvp-promotion-20261014 | release/mvp-promotion-local-20261014 | 0 | nao |

## Proposta, sem execucao
- `release/mvp-local-20261014` e `fix/physical-eta-recent-capture-20261015` estao incorporadas por PR66/67 na main backend `6849bb2`. Manter worktrees/artefatos ate a revisao final; so depois confirmar status limpo e ausencia de arquivos ignorados uteis antes de considerar remocao de worktree e branch local.
- Branch incorporada nao implica WIP incorporado: backend original main tem alteracoes legitimas ainda fora de commits. Nunca usar a lista --merged para apagar um workspace sujo.
- Shadow/MVP historico e experimental3G, laboratorio ML e frontend ajustes/startup permanecem preservados. Nao transportar suas implementacoes para esta PR.
- Metadados worktree prunable de diretorios ausentes sao apenas candidatos a conferencia manual; nao foi executado prune. Branches remotas nao sao excluidas por esta proposta.
- Para qualquer limpeza posterior: conferir git branch --merged origin/main, git worktree list --porcelain, git status --short e commits exclusivos, identificar arquivos ignorados/artefatos fora do Git e obter autorizacao especifica. Nao usar reset/clean/stash como limpeza.
