# Consolidação pré-release — 14/10/2026

**APTO para revisão local; BLOQUEADO para publicação/deploy automático.**
Não houve mudança funcional, remoção de código, integração de GPS antigo ou
ativação de flag. A entrega acrescenta uma seleção local reproduzível de testes,
reutilizando o filtro e o verificador TRX de `.ci/offline_tests.py`. Nenhuma
alteração no workflow, allowlist CI, Docker, dependências ou contratos públicos.

## Base efetiva e preservação

Inventário anterior completo de branch/HEAD/status/staged/diffs e SHA-256 dos
arquivos sujos/untracked em:
`D:/repositorio_github/NoPonto/pre-release-20261014-01/before.json`.

| Repositório/worktree | Branch | HEAD | Arquivos inventariados |
|---|---|---|---:|
| Backend ativo | fix/gps | 6f64538 | 57 |
| Frontend | ajustes | registrado no inventário | 21 |
| ML | feature/eta-history-mvp | 6c0f7b5 | 9 |
| Backend MVP | feature/eta-ml-mvp | 5a13e52 | 0 |
| Shadow GPS atual | feature/eta-shadow-gps-current | 6f64538 | 38 |
| Diagnóstico congelado | release/gps-diagnostics-4c4 | 6f64538 | 31 |

Nenhum staged. São156 arquivos preexistentes protegidos por hash:155 ficaram
idênticos; o histórico ativo recebeu somente acréscimo, com prefixo original
validado. Resultado em `validation-preservation.json` no diretório de evidências.
Worktrees
prunable foram apenas listadas; não houve manutenção ou mudança de branch.

## O que está integrado

| Componente | Situação real |
|---|---|
| GTFS-RT BUS/BRT, matching, CAS, eventos, proteções de cursor/tempo | Presentes no backend ativo; alterações GPS locais preservadas |
| PhysicalEta e velocidade causal congelada | No polling ativo antes do CAS, opcional/OFF |
| Destino consultado e resolver longitudinal | `EventosParadaService` + `IRoadEtaDistances` registrados no Program; lotes200, limite1000, READ ONLY/timeouts |
| GET /paradas/{paradaId}/eventos | Controller atual preservado, mesmo DTO HTTP |
| Radar | Frontend `buscarEventosParada` consome esse endpoint; tipos aceitam campos nullable; usa estimatedAt/scheduledAt e desconta tempo no cliente |
| Presença BUS/BRT nos terminais | Frontend ajustes; flag visual independente, sem ETA inventado ou alteração de TTL GPS |
| HistoricalEtaShadow/estatísticas/exportador | Somente worktree Shadow; não registrados/compilados no runtime normal fix/gps |
| ExtraTrees/FastAPI | Serviço separado no ML; `Dockerfile.eta-history`, sem promoção ao ETA público |
| Comparação físico/histórico/ExtraTrees | Laboratório `tools/eta_ml/tcc`; import explícito de fontes Shadow, HTTP local, evidência sintética da entrega12 |

O físico e o resolver usam a mesma distância longitudinal do matching: diferença
entre comprimentos geográficos de prefixos da versão exata. O endpoint não
depende de PassagemParada para essa previsão. O fallback legado usa o ETA do DTO
quando o alvo coincide; nos demais casos distância/velocidade média ou instantânea
>2km/h, senão15km/h. Mantido quando o físico está OFF.

### Limite da integração Shadow

O laboratório já prova identidade/destino/distância únicos em fixtures e recusa
versão/sentido/alvo divergentes. Isso não resolve automaticamente a captura real:

- O adapter exige viagem confiável, timestamp causal e **próxima ocorrência
  operacional**. O DTO físico no cache não certifica uma execução operacional
  para qualquer parada consultada; seu funcionamento independe dessa viagem.
- O resolver histórico usa substring geográfica; o físico usa diferença de
  prefixos. Não basta igualar frações ou selecionar alvos com nomes parecidos.
- Para runtime, precisa existir um contexto confiável comum de viagem/volta/
  observação/alvo e a **mesma distância já validada** pelo resolver, respeitando
  os limites de fila, timeout e fail-open. Não há esse contexto geral no contrato
  atual da consulta; criá-lo seria ampliar a funcionalidade nesta consolidação.
- Worker normal continua exigindo modelo real qualificado; o sintético só serve
  à demonstração local. Não existem labels reais auditados suficientes fornecidos.

Portanto, mantido o laboratório como entrega integrada isolada, conforme a
alternativa autorizada. Nenhuma DI parcial que pudesse sugerir captura real
operante foi adicionada. Os testes Shadow são da sua worktree, não do polling
ativo fix/gps; não são apresentados como teste de uma integração ainda ausente.

## Flags e configurações

| Chave | Default/configuração local | Precedência e cuidado |
|---|---|---|
| PhysicalEta__Enabled | false no tipo; sem override base | ON evita chamada pública ao GpsEtaClient; não ativa ML |
| ML__ETA__Enabled | **true se ausente**, legado por compatibilidade | Exigir false explícito no futuro preflight; não inferir OFF de BASE_URL |
| EtaV2__Enabled / EtaV2__ShadowEnabled | false no tipo e appsettings base | Independentes do físico/histórico |
| HistoricalEtaShadow:Enabled | false no tipo da outra worktree | Sem runtime registrado no backend ativo |
| HistoricalEtaOfflineExport:Enabled | false no tipo da outra worktree | Exige configuração explícita e Shadow; sem retenção produtiva implícita |
| ETA_ALLOW_SYNTHETIC | só literal1 no laboratório | Nunca ativar no serviço real; worker continua recusando synthetic |
| EXPO_PUBLIC_ROAD_PRESENCE_ENABLED | apenas literal true habilita | Flag de build frontend; ausência mantém caminho anterior |
| GpsCorrelatedDiagnostics__Enabled | false no tipo | 4C.7 OFF; não confundir com seis logs agregados4C.4 |
| GpsPolling__MudancaOperacionalHabilitada | false | Mantido; nenhuma habilitação para tentar resolver cursor |

Os seis registros `Funil GPS:` continuam emitidos por ciclo em Information;
não têm o gate da instrumentação correlacionada. Removê-los/gateá-los exige
revisão posterior. Considerar volume/rotação na próxima release. Não se
consultaram providers efetivos ou flags remotas nesta tarefa.

## Legado: inventário e decisão

| Candidato concreto | Classificação / evidência | Decisão |
|---|---|---|
| `ParadaService`, interfaces/DTOs/controller de paradas antigos | Excluídos explicitamente pelo csproj; referências residuais no grupo legado | Mortos para **este build**, não declarar remoção global segura; revisar bloco coordenado depois |
| `Controllers/Admin/AdminMlController.cs` | Admin excluído do Compile; único consumidor nominal de `ml-admin` encontrado | Controller inativo; registro ml-admin em Program é candidato a limpeza conjunta, preservado |
| `GpsEtaClient`, `GpsEtaOptions` | DI e chamada real no polling com PhysicalEta OFF; testes ativos | Legado necessário/fallback; não remover |
| `EstimateRoadSeconds` no EventosParadaService | Chamado na projeção quando físico OFF; testes ativos | Fallback necessário, não duplicata removível |
| `EtaV2Shadow` e workers | DI ativa, flags próprias e testes; estado persistido existente | Caminho opcional, remoção exige plano próprio; sem migrations/downgrade |
| Fontes legacy GPS Zirix/BrtCurrent/Datario | Registradas como IGpsSource; resolver escolhe providers | Fallbacks não comprovadamente mortos, preservar |
| `GpsCicloPerformance.Operacional` / `GpsCorrelatedDiagnostics` | Diagnóstico4C.4/4C.7, testes e relatório de evidência | Temporários; manter até encerrar regressão, remover separadamente |
| Frontend `veiculosMapa`/projeção anterior e caminho sem presença | Usados com flag visual OFF e em veículos em movimento | Necessários; presença nova não os substitui integralmente |
| ML `server.py`/`treinar.py`/Dockerfile original | Dockerfile inicia server:app; server chama treino; distinto da FastAPI histórica | Legado ainda executável, não remover nem trocar imagem implicitamente |
| ML `modelo_eta.joblib` / `linha_encoder.joblib` | Artefatos versionados consumidos pelo servidor legado | Gerados, mas necessários ao legado/retorno; não apagar |
| ML `__pycache__/server.cpython-313.pyc` | Bytecode versionado; fontes presentes, não copiado pelo Dockerfile | Resíduo gerado, candidato concreto à retirada do versionamento depois; não apagado nesta revisão |
| ML `dataset_v1.py`, validacao_temporal/posicao_corrigida e CLIs | Imports e testes existentes; contrato distinto do pipeline histórico | Não são duplicatas mortas; preservar gates/fluxos |
| Cópias de pipeline/Shadow nas worktrees MVP | Manifest de transferência3C e referência histórica; laboratório usa fontes explicitamente | Duplicação entre worktrees, não runtime duplicado; preservar até consolidar linhagem |
| Relatórios4C.5–4C.9, snapshots, TARs, executores/rollback e manifests | Evidência e mecanismo operacional de retorno | Histórico/recuperação, preservar integralmente |
| Outputs ML/laboratório, bin/obj/node_modules, TRX e TAR | Gerados; nenhum bin/obj equivalente versionado no backend/frontend encontrado | Não incluir em commit; arquivar evidências com hashes, sem limpeza automática |

**Nada foi excluído.** Código fora do Compile pode fazer parte de uma retomada
administrativa e deve ser removido com seu conjunto de referências, em revisão
separada. O bytecode é um resíduo claro, porém não justifica misturar exclusões
de outro repositório com a consolidação atual.

## Validação local reproduzível

Arquivos novos: `tools/validation/pre_release.py` e `pre-release-tests.json`.
Seleção de35 classes:18 já aprovadas pelo CI +17 regressões GPS/físico/terminal.
O verificador exige todas as classes, zero falhas e zero ignorados. Nenhuma
seleção por substring ampla de Integration/Postgres/Redis.

```powershell
dotnet build NoPonto/NoPonto.csproj --no-restore
$preReleaseFilter = python tools/validation/pre_release.py filter
dotnet test NoPonto/NoPonto.csproj --no-build --no-restore --filter $preReleaseFilter --logger 'trx;LogFileName=backend.trx' --results-directory D:/repositorio_github/NoPonto/pre-release-nova-execucao
python tools/validation/pre_release.py verify D:/repositorio_github/NoPonto/pre-release-nova-execucao/backend.trx
```

Execução desta tarefa em `D:/repositorio_github/NoPonto/pre-release-20261014-01`:

- Build C# normal aprovado, sem import do Lab.targets, warnings preexistentes.
- **763 C# aprovados**,35 classes verificadas, zero falhas/ignorados. Inclui
  matching/GPS, CAS em memória, diagnósticos/cursor, múltiplas paradas, HTTP
  efêmero no controller, flags OFF e compatibilidade frontend.
- **76 C# Shadow aprovados** na outra worktree: fila, contrato, synthetic
  recusado, falhas/timeout, BUS/BRT individual/lote, sink/exportador e polling.
- **24 Node aprovados**: terminal/presença, favoritos/SignalR/Radar, inclusive
  sintaxe do JavaScript WebView. Comando no frontend: `node --test
  src/services/terminalPresence.test.cjs src/services/favoritosEtapaB.test.cjs`.
- **50 Python aprovados**:41 ML existentes +9 do experimento; mesmo comando do
  README TCC com `test_experiment` no PYTHONPATH. Sem treino real.
- Laboratório anterior reconferido por508 hashes de fontes e18 hashes de
  artefatos, incluindo modelo e resultados. Sem novo treino/inferência HTTP;
  evidência em `lab-recheck.json`, sem apresentar execução anterior como nova.
- Verificador CI existente:6 aprovados/4 ignorados por suporte de shell/ambiente
  nesta execução Windows. Esse lote não equivale à execução Linux do workflow.
- **TypeScript --noEmit aprovado**, sem instalação de dependências ou build
  de publicação. Comando: `node node_modules/typescript/bin/tsc --noEmit`.

TRX e logs permanecem fora do repositório. Não executados testes contra banco,
API operacional, Redis existente, Docker/build de publicação ou dispositivo.
As fixtures de HTTP/geometria/cache não comprovam precisão ou desempenho real.

Nesta tarefa foram criados somente o seletor local, seu JSON e este relatório;
o único arquivo preexistente editado foi `NoPonto/HISTORICO.md`, por acréscimo.
Backend funcional, frontend, ML e demais worktrees não receberam edições.

## Bloqueios e sequência posterior — NÃO EXECUTADA

1. **GPS:**4C.9 não corrigiu a causa das novas execuções LINEAR. Cursor adiantado
   confirmado, mas falta prova auditada de fim/partida ou do primeiro par
   regressivo para escolher correção sem duplicar passagens. Não anunciar
   restauração de PassagemParada nesta release.
2. **Git:** base ainda é HEAD6f64538 + deltas não commitados e fontes untracked;
   não há revisão imutável implantável. Selecionar explicitamente arquivos/hunks
   para commits futuros: GPS/diagnóstico; físico/resolver/testes; frontend
   presença separada do WIP startup; laboratório/validação/docs. Não usar add .
3. **Homologação:** validar físico/terminal em ambiente descartável e dispositivo;
   confirmar flags/providers e rotação na futura configuração. Sem evidência
   real nova nesta tarefa; flags permanecem OFF até decisão independente.
4. **Shadow:** para release pode permanecer fora do runtime e OFF. Para anunciar
   Shadow operacional, resolver o contexto comum e qualificar dados/modelo antes.
5. **Publicação:** workflow local publica imagem em push main mesmo com deploy
   desabilitado; deploy depende de `NOPONTO_DEPLOY_ENABLED=true`. Estado remoto
   atual desconhecido, não consultado. Não presumir que bloquear deploy bloqueia
   publicação. Revisão/commit/PR/push somente com autorização separada e após
   decidir as proteções de promoção.
6. **Implantação futura:** congelar revisão/contexto, gerar imagem imutável,
   revisar config sem migrations/startup indevido, preservar imagem/config
   anteriores e lock, aprovar janela/health/retorno. Reaproveitar o procedimento
   operacional validado4C.5; não reaplicar automaticamente snapshots antigos.
7. **Limpeza posterior:** retirar bytecode gerado do versionamento e ajustar
   ignore em mudança própria; remover conjunto Admin/Parada excluído após
   conferir consumidores; só descontinuar XGBoost/EtaV2/providers após provar
   substituição/fallback e preservar rollback. Relatórios/migrations permanecem.

Até completar esses pontos, a consolidação local é revisável e reproduzível,
mas não autoriza publicação, deploy ou remoção estrutural do legado.
