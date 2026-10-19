# Auditoria D0 — ETA operacional versus ExtraTrees — 17/10/2026

Parecer: **NO-GO hoje para comparação real em TEST**. Existe engenharia reutilizável; não foi demonstrado nesta auditoria um dataset real auditado nem modelo real verificável. A rota prioritária é offline. Sete dias permitem preparar e executar um estudo exploratório somente se forem disponibilizadas evidências locais suficientes; não há base para prometer qualificação oficial nesse prazo.

Escopo cumprido: leitura de fontes/documentação e metadados de diretórios/Git; somente este relatório criado. Sem execução de serviços, testes, treino, exportação, coleta, Docker, conexão a banco/Redis, produção, deploy, migrations, leitura de segredos ou conteúdo de telemetria privada. Sem alteração de flags, cursor ou histórico: a instrução específica de gerar apenas este arquivo prevalece sobre o registro geral de alterações. “Causal” significa features disponíveis até o relógio da previsão; não identifica efeito causal de uma intervenção.

## 1. Estado atual e documentação

Raiz comum dos caminhos: `D:/repositorio_github/NoPonto/`. B = `noponto-backend/`; M = `ml/`; D = `noponto-docs/`; V = `noponto-backend-eta-ml-mvp/`. Referências arquivo:linha são dessas árvores locais, sem checkout/fetch.

| Repositório | Branch / HEAD observado | Status inicial | Worktrees registradas |
|---|---|---|---|
| B | main / `90f7be72e4b55198bc721f4f104a58f37def3f2d` | dois untracked: `DIARIO_ETA_ML_SHADOW_NOPONTO_2026-10-17.md`, `PLANO_ETA_ML_SHADOW_NOPONTO_2026-10-17.md` | principal e oito adicionais abaixo |
| M | feature/eta-history-mvp / `6c0f7b5313f1d953105d0211f08df6cdaf098e48` | `?? lab-output/`, `?? outputs/`; rastreados limpos | somente `ml` |
| D | main / `ca6f49b05d7882d4ffd724199601993a4487ad3d` | ZIP `noponto-docs-atualizacao-20261016.zip` deletado; `noponto-docs.rar` modificado; mesmos dois MD untracked | somente `noponto-docs` |

Worktrees backend, conforme `git worktree list` (status interno das árvores adicionais não auditado):

| Caminho | HEAD curto / branch |
|---|---|
| `D:/np-backend-cleanup-admin-di` | 69c5fc4 / chore/backend-cleanup-admin-di |
| `D:/np-backend-cleanup-arcgis-trem` | 8ee4d32 / chore/backend-cleanup-arcgis-trem |
| `D:/np-backend-cleanup-excluded-code` | efdba0f / chore/backend-cleanup-excluded-code |
| `D:/np-backend-cleanup-redis-legacy` | 0708c9b / chore/backend-cleanup-redis-legacy |
| V | 5a13e52 / feature/eta-ml-mvp |
| `noponto-backend-eta-shadow-gps-current` | 6f64538 / feature/eta-shadow-gps-current |
| `noponto-backend-gps-diagnostics-4c4` | 6f64538 / release/gps-diagnostics-4c4 |
| `noponto-backend-shadow-physical-investigation` | a11984f / test/shadow-physical-capture-order |

Git recusou inicialmente M/D por ownership; consultas repetidas com `-c safe.directory=<raiz exata>` somente no processo, sem gravar configuração. A enumeração de M/outputs encontrou três subdiretórios `result` com acesso negado; não se tentou contornar permissões.

Documentação lida: D/GUIA_DE_RETOMADA.md (16/10); D/09-validacao/10-protocolo-comparativo-eta-offline.md (versão 13/10); D/03-funcionalidades/19-eta-limitacoes-testes-e-roadmap.md (consolidação 16/10 e seção histórica preservada); D/09-validacao/06-fechamento-eta-ml-mvp-20261011.md (11/10). Também B/AGENTS.md, B/NoPonto/HISTORICO.md e B/docs/00-auditoria/23-cursor-adiantado-4c9-20261014.md. Não surgiram AGENTS adicionais na busca em M/D. Documentação histórica não equivale a nova execução local.

## 2. Matriz evidenciada — 11 checks

VALIDADO = observado nesta leitura; IMPLEMENTADO = caminho existe, sem execução nova; NÃO VERIFICADO = falta artefato/prova; BLOQUEADO = dependência concreta impede o objetivo.

| # | Check / estado | Prova e limite |
|---|---|---|
| 1 | Revisões locais — VALIDADO | inventário Git acima; guia D/GUIA_DE_RETOMADA.md:15 deve ser interpretado com o estado local atual. |
| 2 | GPS → atualização causal → captura — IMPLEMENTADO | B/NoPonto/2-Application/Services/GPS/GpsPollingService.cs:439 atualiza histórico de velocidade; :486 aplica PhysicalEta; :1144 faz atualização de cache; :1167 atualiza viagem; :1180 captura Shadow. B/.../HistoricalEtaShadow.cs:60 exige identidade, versão, volta, estado e alvo consistentes. Não comprova execução nem quantidade real. |
| 3 | Viagem/passagem e fronteiras — BLOQUEADO | B/NoPonto/2-Application/Services/GPS/ViagemOperacional.cs:92 preserva cursor LINEAR; :164 gera passagem; :266 interpola timestamp. B/.../EtaDataset.cs:13 exige evidência externa, :60 rejeita qualidade não auditada. B/docs/00-auditoria/23-cursor-adiantado-4c9-20261014.md:34 exige fronteira/primeira regressão específica. Não baixar cursor. |
| 4 | Exportação canônica na main — BLOQUEADO para ML atual | B/tools/EtaMl.Export/Program.cs:15–28 fixa cutoff/proveniência antiga; :44 emite `eta-export-3b-v1`. V/tools/EtaMl.Export/Program.cs:15 carrega catálogo; :55 emite `eta-export-3g2-v2`; V/NoPonto/2-Application/Services/GPS/EtaDataset.cs:41 define `eta-route-versioned-3g2-v2`. Não presumir que exporter antigo da main satisfaz M. |
| 5 | Perfil pós-GTFS-RT — BLOQUEADO para coorte desse regime | M/tools/collection_profiles.json:2 e :10 e V/tools/collection_profiles.json:2 e :10 só contêm `historical-pre-fix`/`official-post-fix`; D/09-validacao/06-fechamento-eta-ml-mvp-20261011.md:49 em diante separa esses dados da release posterior. Perfil novo depende de prova do corte, não de renomear dataset. Coorte antiga comprovada pode usar seu perfil verdadeiro. |
| 6 | Features/splits — IMPLEMENTADO | M/tools/eta_ml/pipeline.py:54 valida manifesto/hash/allowlist; :80–88 purga viagem que cruza corte e fixa split inteiro; :94 valida GPS < passagem e label; :104–108 deriva hora/dia do GPS; :197–204 ajusta preprocessing/modelo/baselines só em TRAIN. B/.../EtaDataset.cs:67 verifica journal/label, :89 purga cortes. Ausência de vazamento semântico da origem ainda requer auditoria dos insumos. |
| 7 | Qualificação e ciclo real — IMPLEMENTADO | M/tools/eta_ml/qualify_real.py:112 rejeita fixtures/exploratório; :119 exige política atual; :128 valida conteúdo; :136 exige evidência integral. M/tools/eta_ml/offline_cycle.py:12 executa ciclo sem promoção. M/tools/eta_ml/pipeline.py:244–259 exige 10.000 observações, 14 dias, 200/50/50 viagens e cobertura por linha. |
| 8 | ExtraTrees e serviço — IMPLEMENTADO | M/tools/eta_ml/pipeline.py:191–211: 64 árvores, profundidade 18, 4.096 folhas, mínimo 3 por folha, seed e n_jobs=1, métricas e pickle. M/eta_history_service.py:38–78 verifica contrato, qualificação/hashes e artefato antes de carregar; :114 readiness, :119 batch. Existência do código não demonstra modelo real. |
| 9 | Operacional/Radar versus hook — IMPLEMENTADO | B/NoPonto/2-Application/Services/EventosParada/EventosParadaService.cs:87–155 e :210–216; B/NoPonto/1-API/Controllers/EventosParadaController.cs:7–15; B/.../HistoricalEtaShadow.cs:86–91. Fórmula detalhada abaixo. |
| 10 | Pareamento/métricas — IMPLEMENTADO parcialmente | B/tools/eta_ml/evaluate_shadow.py:107 valida labels; :212 calcula métricas; :294 avalia; :375–380 distingue erro de chegada de diferença entre previsões. M/tools/eta_ml/pipeline.py:169 avalia baselines/ML. Não foi localizada saída integrada `comparison.csv` com operacional do endpoint e quatro métodos na mesma chave TEST. B/tools/eta_ml/test_evaluate_shadow.py:122–144 cobre identidade/perdas em fixtures, sem execução nova. |
| 11 | Artefatos reais e prova local — NÃO VERIFICADO | D/09-validacao/06-fechamento-eta-ml-mvp-20261011.md:24 relata 115 testes C# e :26 relata 34 Python históricos; :44–46 relata bundle e candidatas. Nenhum teste foi executado agora; nenhum conteúdo privado/modelo real foi aberto ou hash revalidado. |

## 3. Dados reais: contagem e disponibilidade

**Hoje, GPS reais disponíveis: desconhecido; viagens integralmente auditadas: desconhecido; chegadas efetivamente auditadas: desconhecido. Zero foi demonstrado nesta auditoria**, o que não afirma inexistência física fora do escopo acessível.

Histórico, exclusivamente: D/09-validacao/06-fechamento-eta-ml-mvp-20261011.md:44–46 descreve `snapshot-3b2b-v2-real-01`, 24/24 hashes então conferidos, 775 GPS, 188 históricos, 220 eventos, 16 viagens com início/fim, um dia, seis linhas; `comparison-3g2-real-01/02` contém 758 candidatas preliminares, 17 sem label. As 16 eram `NaoVerificada`; zero viagens/observações certificadas naquele fechamento. Histórico/evento não é sinônimo de chegada auditada; 758 não é denominador TEST e versões não se somam.

Localizações verificadas por metadados: M/outputs contém `evidence-verifier-3g3b1-6c56333227db45f2966786f439e9b791`, `mvp-preflight-979edf88398c4dd28fa9b6fdd4884f34`, `readiness-3g1-current-01/02`. Nomes de diretório não certificam conteúdo. M/lab-output contém relatório de reconciliação. B/tools/eta_ml/examples e M/tools/eta_ml/examples têm manifesto explicitamente sintético; B/tools/eta_ml/examples tem relatório de fixture PostGIS. A enumeração de B/tools/validation, V/tools/eta_ml/outputs e ../tools/output não apresentou diretórios de bundle. **Não se localizou positivamente o bundle histórico nomeado, CSV real canônico ou modelo real qualificado nas buscas delimitadas**; não houve busca indiscriminada no disco nem leitura dos outputs privados. Um `.pkl` legado, se existir, não provaria modelo ETA histórico.

Verificação mínima futura: custodiante fornecer caminho local autorizado, manifesto sanitizado com hashes/contagens e referências de auditoria; conferir hashes sem exibir registros, contar viagens completas/chegadas únicas por chave e exclusão, verificar regime/corte e prova externa durante toda a viagem. Só então declarar volume real. Não copiar PII, abrir `.env`, configurações de conexão ou desserializar pickle desconhecido.

## 4. ETA operacional/fixo: definição exata

Não há constante única chamada “fixo”. Comparador primário proposto: **operacional legado reconstruído, política congelada, `now = TimestampGps`**, com alvo da mesma ocorrência/versão/volta e `y = TimestampPassagem − TimestampGps`. É uma reconstrução retrospectiva, não prova do que o passageiro viu.

B/.../EventosParadaService.cs:95–103 escolhe primeira ocorrência da parada solicitada na mesma versão com fração alvo + 1e-7 >= fração atual; `d = max(0, (fraçãoAlvo − fraçãoGPS) × ComprimentoMetros)`. Com PhysicalEta desabilitado na política simulada, :210–216 usa ETA do DTO se próxima ocorrência coincide e ETA >= 0; senão `v = média > 2 ? média : instantânea > 2 ? instantânea : 15` km/h; `eta = d / (v/3,6)`. :147 faz `EstimatedAt = now + eta`, :152 arredonda segundos com `Math.Round`. Preservar valor contínuo para erros e registrar arredondamento exibido. Não trocar esse fallback pelos limites 1–160/5–80 do baseline físico ML.

O CSV canônico B/.../EtaDataset.cs:46 **não conserva todos os insumos dessa reprodução**: falta comprimento de rota e ETA/confiança originais do DTO. Acrescentar sidecar sanitizado por chave, ou recuperar causalmente de snapshot versionado, é necessário para a precedência exata. Sem isso, declarar “operacional sem override DTO” como outro estimador não satisfaz a comparação exata solicitada.

O endpoint usado pelo Radar é `/paradas/{paradaId}/eventos`, relógio `clock.GetUtcNow()` em B/NoPonto/1-API/Controllers/EventosParadaController.cs:15. A política atual também tem ramo PhysicalEta (:105–145): valida idade/identidade e velocidade capturada; para alvo coincidente usa ETA DTO menos idade; com resolver presente recalcula para a parada selecionada usando distância versionada e velocidade, também menos idade. B/.../EventosParada/RoadEtaDistances.cs:17–18 calcula diferença de comprimentos cumulativos; B/.../GPS/PhysicalEta.cs:6 define MaxAgeSeconds padrão 30. Flags efetivas de produção não foram lidas: não afirmar qual ramo serve hoje ao usuário.

`PublicEta` no hook não é o resultado Radar. B/.../HistoricalEtaShadow.cs:86–91 só retém ETA marcado `PhysicalEta:estimativa_fisica`, com alvo e prova de velocidade compatíveis, no relógio GPS; não chama EventosParadaService e não subtrai idade de consulta posterior. Também não é toda ETA existente no DTO. Para comparação em t0 posterior, usar instante previsto `tGPS + etaGPS` e erro contra passagem; não somar ETA do GPS a t0 sem compensação. Congelar qual ramo/comparador antes de TEST, sem alterar flag real.

## 5. Somente três bloqueios prioritários

1. **Labels e fronteiras ainda sem prova acessível (C0).** Hipótese falsificável: existe coorte local com chegada única rastreável, identidade completa, limites de execução e auditoria integral suficientes para separar viagens cronologicamente. Teste decisivo único: verificar um pacote local delimitado por manifesto, reconciliando todas as viagens candidatas com início/fim, journal/histórico, qualidade e timestamp de referência, emitindo contagens agregadas e motivos de exclusão. Arquivos: B/.../EtaDataset.cs:60–105, B/.../ViagemOperacional.cs:266, B/docs/00-auditoria/23-cursor-adiantado-4c9-20261014.md:34. GO C0: coorte não vazia rastreável, com referência observada ou interpolação explicitamente identificada e análise de sensibilidade; para C1, ao menos uma viagem completa independente em cada split. NO-GO: fronteira inferida apenas do estado atual, proteção desconhecida ou label ambíguo. Não corrigir 4C.9 sem caso decisivo.

2. **Contrato/exportação/regime e volume impedem o caminho oficial (C1).** Hipótese: exporter 3G.2 reutilizável + perfil verdadeiro + pacote integral podem satisfazer qualificação atual; amostra menor apenas falharia por volume. Teste decisivo único: futuro `qualify_real --dry-run` sobre exportação congelada, sem fit, classificando todos os gates. Arquivos: V/tools/EtaMl.Export/Program.cs:55, M/tools/collection_profiles.json:2, M/tools/eta_ml/qualify_real.py:112–143, M/tools/eta_ml/pipeline.py:244. GO oficial: ready=true com gates intactos; NO-GO oficial se qualquer gate falhar. GO exploratório: C0 e contratos/causalidade/regime passam, apenas volume impede qualificação, e protocolo separado é aprovado/congelado. O pipeline atual bloqueia fit real pequeno (:195–196); precisa runner exploratório separado que reutilize preprocessing/ExtraTrees, grave `training_dataset=false` e não gere recibo de qualificação nem artefato aceito pelo serviço. Nunca marcar real como sintético. Hoje C1 permanece NO-GO, inclusive exploratório por falta de C0 demonstrado.

3. **Falta saída pareada com operacional fiel e seus insumos (C2).** Hipótese: com sidecar causal mínimo e modelo congelado é possível reproduzir a política escolhida e associar todas as estimativas à mesma chegada TEST. Teste decisivo único: replay diferencial offline de uma fixture com override DTO, fallback 15, média/instantânea, alvo diferente, envelhecimento e ausência de previsão; confrontar reconstrução com ProjectRoadEvents e validar chaves/métricas do CSV esperado. Arquivos: B/.../EventosParadaService.cs:87–155/:210, B/.../EtaDataset.cs:46, M/tools/eta_ml/pipeline.py:169, B/tools/eta_ml/evaluate_shadow.py:294. GO: igualdade de estimativas/instantes segundo tolerância numérica congelada, sem perdas silenciosas ou denominador condicionado ao sucesso de todos; NO-GO se faltarem insumos ou relógios/targets divergirem. Teste fixture valida implementação; somente C0+C1 e replay real TEST habilitam a tabela empírica.

## 6. Menor caminho até comparison.csv em TEST

Proposta de trabalho posterior; nada abaixo foi executado. Dias são orçamento, não garantia de disponibilidade dos dados.

1. **D1–D2 / C0:** localizar pacote privado autorizado e manifesto sanitizado; auditar limites/chegadas e congelar exclusões. Sem dados suficientes, encerrar com NO-GO documentado em vez de fabricar labels. Não depender de Shadow novo para resgatar a semana.
2. **D2–D3:** reutilizar exporter/política 3G.2 da V numa ferramenta offline isolada, revisando apenas dependências necessárias; não transportar startup antigo ou integrar arquitetura inteira. Se regime for antigo, manter perfil correto; se pós-GTFS-RT, acrescentar perfil coordenado exporter/ML somente após prova de corte. Preservar lado a lado dados originais e hashes. Exportação futura só em ambiente TEST local autorizado, nunca banco real nesta etapa.
3. **D3 / C1:** congelar cortes por viagem completa, purge e protocolo antes de olhar TEST. Dry-run oficial; se só volume falhar e C0 passar, escolher estudo exploratório separado, uma linha/regime quando possível, com validação e TEST temporal independentes. Amostra de um dia limita generalização; não há tamanho menor automaticamente válido. Implementação mínima exploratória é runner de fit/avaliação isolado reutilizando funções existentes, sem tocar gates oficiais, serviço ou flags.
4. **D4–D5:** fit apenas TRAIN, selecionar decisões em VALIDATION, congelar seed/bibliotecas/config/modelo. Acrescentar um comparador operacional offline e sidecar de insumos causais; testar replay diferencial do bloqueio 3. Baselines físico e mediana TRAIN já existem; não reimplementar modelos.
5. **D6–D7 / C2:** inferir uma vez no TEST congelado e produzir `comparison.csv` por observação + viagem + veículo pseudonimizado + linha/sentido + versão + ocorrência + volta + timestamp GPS. Incluir label/tempo de passagem, relógio/política, digest, ETA operacional, físico, histórico, ExtraTrees e status/motivo de cada ausência. Ausências ficam vazias, nunca zero. Preservar denominador elegível e interseção/par a par.

Métricas: erro assinado = instante previsto − passagem; MAE, MedAE, P90, |erro| <= 60/120 s, cobertura, vitórias estritas e empates pareados; contagens de observações/viagens/dias/linhas por recorte. Fixar P90 `numpy.quantile` com método explicitado (pipeline usa quantile; avaliador Shadow usa nearest-rank, B/tools/eta_ml/evaluate_shadow.py:219). Mostrar perdas e erro no conjunto comum; não declarar superioridade geral com amostra pequena. Se estimar incerteza, reamostrar por viagem mantendo pares. Modelo pior/empate é resultado admissível.

Estados D0: **C0 NO-GO por prova ausente; C1 real oficial NO-GO e exploratório condicional; C2 NO-GO por dependências e comparador integrado ausente.** Nenhum desses gates exige deploy. Uma tabela real pequena é admissível como exploratória, se as viagens/labels forem realmente auditados e o runner separado não adulterar qualificação.

### Apêndice: S0/S1 Shadow opcional

S0 (engenharia local): IMPLEMENTADO, não revalidado em execução nesta auditoria. B/.../HistoricalEtaShadow.cs:193 fila limitada; :219 registra resultados; B/NoPonto/2-Application/Services/GPS/HistoricalEtaOfflineExport.cs implementa retenção privada; testes B/NoPonto/5-Testes/HistoricalEtaOfflineExportTests.cs existem. Readiness/modelo real/latência/perdas não foram medidos agora.

S1 (Shadow real): NÃO VERIFICADO/NO-GO para afirmar validação. Depende de modelo oficialmente aceito, digest/readiness, budget e captura retida com labels e perdas conhecidas; ativação produtiva requer outra autorização. Artefato exploratório não deve passar pelo serviço oficial. S0/S1 não entram no caminho crítico até comparison.csv.

Verificação realizada: leitura estática e inventário Git/filesystem. Testes executados agora: nenhum. Não executados: dotnet/Python suites, dry-run qualificador, treino, replay, benchmark, inferência ou testes conectados. Evidências de 115 C#/34 Python e HTTP sem modelo pertencem ao fechamento de 11/10, não à D0. Limitações: acesso negado a três diretórios de resultados, bundles históricos não localizados positivamente e ausência de inspeção de conteúdo privado por desenho da auditoria.
