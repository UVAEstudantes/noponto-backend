# ETA/ML — fechamento do MVP em 11/10/2026

**O núcleo de engenharia já existe. Não há dados locais demonstradamente auditados para iniciar treino real, nem modelo histórico qualificado para Shadow real.** Reutilizar as branches MVP; não importar a arquitetura experimental. Este relatório separa preparação técnica, qualificação dos dados e avaliação real.

## A — Componentes efetivamente prontos

Estado local consultado sem fetch, merge ou checkout:

| Árvore | Branch / HEAD local | Estado inicial |
| --- | --- | --- |
| `noponto-backend` | `fix/gps` / `6f64538` | Alterações anteriores de GPS/funil, entidade e documentos preservadas. Não contém a integração Shadow histórica. |
| `noponto-backend-eta-ml-mvp` | `feature/eta-ml-mvp` / `5a13e52` | Fontes rastreados limpos; contém dataset/exporter 3G.2 e integração Shadow. Base `70d7bcb`, anterior ao GPS atual. |
| `ml` (repositório ML solicitado) | `feature/eta-history-mvp` / `6c0f7b5` | Fontes rastreados limpos; `lab-output/` e `outputs/` preexistentes não rastreados preservados. |
| Referência experimental | `experimental/eta-evidence-3g` / `27e1aec` | Consultada somente como referência de distância/qualificação; nenhum código transportado. |

Implementações reaproveitáveis:

- `eta_history_service.py`: FastAPI independente do legado; `/health`, `/ready`, `/eta/batch`, limite de 200 itens/256 KiB, hash/contrato/perfil/recibo/versão sklearn verificados antes de carregar artefato real. Ausência de modelo mantém readiness e inferência em 503; nenhuma substituição por XGBoost. Sintético exige opt-in local explícito.
- `pipeline.py`: ExtraTrees de 64 árvores, seed configurada, `n_jobs=1`, preprocessing ajustado somente em TRAIN, baselines físico e mediana histórica, MAE/RMSE/mediana/P90/cobertura e recortes. Manifesto vincula artefato, dataset, config, bibliotecas, splits e política. Reprodutibilidade depende dos mesmos insumos e versões; não é garantia de precisão real.
- `qualify_real.py` e `offline_cycle.py`: gates existentes, validação de evidências externas, congelamento de inputs, treino/avaliação offline e candidata em diretório novo com hashes, sem promoção automática. Insuficiência ou evidência ausente impedem fit.
- `HistoricalEtaShadow`: captura após atualização operacional, fila limitada com `TryWrite`, worker separado, consulta READ ONLY, timeout/circuito, correlação de lote e digest real. Features completas e identidade versionada; hora/dia derivados do GPS em UTC−3. Distância por `ST_Length(ST_LineSubstring(GeometriaVersionada, posiçãoGPS, posiçãoDestino)::geography)`, igual ao treino. Fila cheia, timeout, 503 e falha de transporte não substituem nem alteram ETA público. Fallback preserva o fluxo GPS e seu ETA existente, sem fabricar previsão histórica.
- Configuração `HistoricalEtaShadow` independente de `ML:BASE_URL`, desligada por padrão. Não precisa reescrever o cliente quando houver modelo. `GpsEtaClient` público continua semanticamente incompatível com o modelo histórico e não foi conectado a ele.

**Validação desta rodada:** 115 testes C# offline aprovados, zero falhas/ignorados, com recompilação na worktree MVP e `--no-restore`; cinco avisos preexistentes. Cobrem dataset, identidade ML, Shadow, fontes e cadência. Resultado privado: `tools/eta_ml/outputs/fechamento-20261011/eta-mvp.trx`.

Python: **34/34 aprovados**, zero falhas/ignorados, 27,451 s; pipeline, qualificação, ciclo offline, contrato leve, política e hashes de migração. Mais **cinco verificações HTTP reais** sem modelo: health 200, ready 503, lote vazio 200, lote completo 503 e health final 200. Imagem já local `noponto-eta-history:mvp-local-20261008`, fontes locais montados somente leitura, container novo com remoção automática, rede none/sem portas, 1 CPU/768 MiB/128 PIDs/tmpfs 128 MiB e prazo Python de 180 s. Fit exclusivamente sintético nos testes; nenhum dado real montado. Primeira preparação por `git archive` mudou finais de linha: 33 testes passaram e somente a conferência de hashes falhou. A repetição usou cópia byte a byte da árvore local, com os 15 hashes conferidos, sem modificar fontes.

Não executados: sete testes de serviço via TestClient (`httpx` ausente na imagem de inferência; nenhum pacote instalado), testes conectados PostgreSQL/Redis, inferência/readiness com modelo real e benchmark real. As cinco verificações HTTP acima não substituem a cobertura completa do TestClient; há evidência histórica dessa suíte no README, sem contabilizá-la como execução desta rodada. O Python Windows também não tinha sklearn/FastAPI/httpx, motivando o uso da imagem local isolada.

Fontes principais: `tools/eta_ml/MVP_BACKEND_ISOLADO.md`, `SHADOW_HISTORICO_3E.md`, `OBSERVABILIDADE_SHADOW_3F.md`, `REVISAO_DISTANCIA_3G_2.md` na referência experimental, e `ml/README_ETA_3C.md`. Os documentos antigos de fechamento descrevem também árvores experimentais históricas; não representam automaticamente a árvore ativa.

## B — Trabalho mínimo restante para MVP técnico completo

1. **Consolidar a integração sobre o GPS atual por revisão dos deltas MVP.** A worktree MVP já resolve o Shadow, mas sua base é anterior ao GTFS-RT, à política atual de startup e à instrumentação. Reaplicar somente o conjunto revisado de adapter/worker/estatísticas/testes/DI/hook causal em uma candidata isolada, preservando as implementações atuais. Não fazer merge nesta tarefa, nem implantar a base antiga: seu startup ainda chama `Database.Migrate()`. Isso é integração de código existente, não uma nova arquitetura.
2. **Decisão de procedência antes de editar o catálogo:** acrescentar um perfil GTFS-RT explícito, coordenado entre exporter e ML, quando forem comprovados release/imagem, migration, sampling, configuração das fontes e instante de início. Manter os perfis anteriores e todos os gates; não rebatizar a imagem nova como coleta de 07/10. Os catálogos atuais dos dois MVPs têm o mesmo SHA-256 e permitem somente `historical-pre-fix` e `official-post-fix`. O exporter da árvore ativa ainda é anterior a essa evolução.
3. **Decisão de retenção para avaliar Shadow contra chegadas:** o sink atual é memória limitada, 500 resultados/TTL 300 s por padrão, perdida em reinício/evicção. Logs periódicos agregam diferença entre previsões, não erro de chegada (`arrival_evaluation_state=awaiting_trusted_labels`). Labels podem chegar até 3.600 s depois. Portanto não existe avaliação real completa apenas habilitando logs. A menor opção é uma captura privada e limitada dos resultados existentes durante homologação controlada, conservada antes da evicção e associada ao CSV canônico por observação/alvo/versão/volta, recuperando a viagem do dataset e comparando com `TimestampPassagem`. Definir destino, limites, retenção e perdas antes de implementar; nenhuma nova tabela, dashboard ou ledger é indispensável por definição.

Não foi encontrada uma correção pequena que resolvesse esses três itens sem uma decisão de procedência/retenção ou sem consolidar branches. Nenhum contrato, schema, política de qualidade ou fonte produtiva foi alterado para contornar isso. A ausência de modelo não bloqueia concluir essas preparações de engenharia; bloqueia validar inferência e precisão reais.

## C — Condições exatas para primeiro treino real e primeiro Shadow real

Inventário de arquivos existentes, sem abrir banco/restaurar snapshot/criar captura nova:

- Bundle `snapshot-3b2b-v2-real-01`: **24/24 hashes conferidos**, 775 GPS, 188 históricos e 220 eventos. Pedidos/versões anteriores encontrados representam a mesma coorte, não volume adicional. São **16 viagens com fronteiras início/fim**, de 07/10/2026 21:02:04–22:07:58 UTC; GPS até 22:07:43 UTC. Apenas **um dia UTC**, seis linhas, 15 viagens BRT e uma ônibus.
- Comparações `comparison-3g2-real-01` e `02`: hashes dos relatórios e do arquivo de candidatos conferidos. Revisão 3G.2 remove 401 rejeições por comparação entre distância direta e rota: **357 → 758 candidatas técnicas preliminares**, com 17 sem label. A distância direta permanece diagnóstico; identidade, geometria, alvo adiante/mesma volta, journal, label e fronteiras continuam obrigatórios. O outlier de matching não ganha validade física por essa mudança.
- **Zero observações e zero viagens demonstradamente certificadas para treino** nos arquivos verificados: todas as 16 continuam `NaoVerificada`, sem prova de ausência de proteção/completude durante a execução inteira. As 758 não são exportação canônica nem volume após splits. Não atribuir `AuditadaSemProtecao` pela presença de journal ou pelo estado corrente.

| Modal / linha | GPS brutos | Candidatas preliminares 3G.2 | Viagens com fronteiras |
| --- | ---: | ---: | ---: |
| BRT / 28 | 103 | 103 | 1 |
| BRT / 50 | 121 | 118 | 3 |
| BRT / 60 | 16 | 15 | 1 |
| BRT / 73 | 457 | 447 | 9 |
| BRT / 80 | 7 | 5 | 1 |
| Ônibus / 764 | 71 | 70 | 1 |
| **Total** | **775** | **758** | **16** |

Não há TRAIN/VALIDATION/TEST real certificado. Mesmo o teto preliminar fica 9.242 observações abaixo de 10.000 e cobre apenas um dos 14 dias exigidos. As 16 viagens não satisfazem 200/50/50; a linha mais coberta tem nove viagens em cerca de uma hora. **Não foi encontrada coorte menor genuinamente auditada que justifique treino exploratório agora.** Uma coorte futura menor só poderá ser proposta separadamente, sem qualificação/promoção/uso público e sem enfraquecer o gate oficial.

Separação temporal: `historical-pre-fix` começa em 07/10 14:53:59.225082 UTC; `official-post-fix`, em 07/10 21:00:57.997530 UTC. São perfis anteriores ao GTFS-RT. Todo o bundle verificado é anterior à release GTFS-RT documentada em 09/10. Isso não autoriza atribuir qualquer janela posterior ao perfil de 07/10: faltam corte efetivo da mudança, continuidade e procedência por release. Separar regimes/fontes e excluir viagens que atravessem o corte ou os limites de split. Agregados operacionais posteriores e relatórios de posição do ML legado não são datasets ETA certificados nem volume adicional desta coorte. Não acessados produção ou dados novos para resolver essas lacunas.

**Primeiro treino qualificado:** CSV/exportação real com política `eta-route-versioned-3g2-v2`, perfil verdadeiro aceito e evidências externas genuínas de viagem inteira; pelo menos **10.000 observações elegíveis, 14 dias UTC, 200/50/50 viagens completas TRAIN/VALIDATION/TEST**, uma linha com **30/10/10 viagens e oito horas distintas de TRAIN**, após purgar viagens que cruzam cortes. Config e exportação devem ter os mesmos limites. `qualify_real --dry-run` precisa retornar `ready=true` antes de fit. A via existente de sessão controlada externa pode atender à auditoria; a nova arquitetura experimental de certificação não é pré-requisito.

**Primeiro Shadow real:** candidata offline qualificada, hashes/manifesto/recibo válidos, métricas comparadas com os dois baselines nos mesmos splits e revisão da cobertura; `/ready` 200 com digest real esperado e batch causal completo homologado, incluindo latência/RSS com modelo. Depois, integração revisada em ambiente autorizado, com capturas retidas e labels confiáveis para calcular erro contra chegadas. Disponibilidade técnica não comprova que o modelo supera baselines. Nenhuma flag produtiva foi habilitada.

**Próximo evento de reavaliação:** recebimento autorizado de uma coorte local com procedência e evidências integrais, seguida de inventário/exportação canônica e dry-run. Para dados GTFS-RT, primeiro comprovar e aprovar seu perfil/corte. Não há data de conclusão demonstrável: as datas 18/21/24 de outubro da config exemplo são limites ilustrativos, não previsão de coleta suficiente; não somar dias de regimes incompatíveis para atingir 14.

## D — Sequência mais curta, por dependências

| Ordem | Pode ser feito agora | Depende de novos dados/evidências |
| --- | --- | --- |
| 1 | Reutilizar os MVPs e os testes acima; revisar somente a integração com GPS atual em candidata isolada. | Sem deploy/merge nesta tarefa. |
| 2 | Revisar a proposta de perfil explícito e de captura privada limitada de resultados; manter gates e flags. | Procedência/corte real fornecidos pelo operador antes de cadastrar o perfil. |
| 3 | Preparar config com splits reais e pacote de evidências no formato existente, sem atestados fictícios. | Coorte completa, auditada, com cobertura e volume medidos após exclusões. |
| 4 | Reutilizar `qualify_real.py --dry-run`, depois `offline_cycle.py`, no PC/runner. | Dry-run verde; treino/avaliação/hash/candidata, sem promoção automática. |
| 5 | Reutilizar homologação isolada de serviço/Shadow. | Modelo qualificado, `/ready` real, budget validado, captura e chegadas associadas. Só então decidir habilitação autorizada do Shadow. |

Sem GNN/DCRNN, gateway, ingress novo, ACLs, snapshot v3, dashboard, treino no homeserver ou mudanças em tarifas/ferrovias/POIs. Nenhum commit/push, publicação, deploy, migration, banco real ou nova extração foi executado. Alteração rastreável desta rodada: este relatório e acréscimo ao `NoPonto/HISTORICO.md`; fontes dos dois MVPs e trabalho anterior preservados.
