# NoPonto — Plano ETA/ML histórico: comparação para o TCC, Shadow opcional

**Versão:** 0.2 (prioridade científica corrigida em 17/10) · **Abertura:** 17/10/2026 · **Revisão de uma semana:** 24/10/2026  
**Responsável pela decisão operacional:** mantenedor do NoPonto · **Status:** PLANEJAMENTO; nenhuma alteração/deploy realizado por este documento.

## 1. Objetivo e limite de escopo

**Objetivo principal (TCC, INEGOCIÁVEL):** obter **dados comparativos reais, auditáveis e reprodutíveis** do ETA operacional/fixo do NoPonto versus um modelo **histórico de ML (ExtraTrees)**, idealmente com físico causal e mediana histórica como baselines auxiliares. O resultado final deve conter previsões, chegadas reais associadas, métricas, gráficos, cobertura, procedência, limitações e protocolo que outro pesquisador consiga executar. Melhoria é uma hipótese, não uma conclusão antecipada.

**Objetivo secundário (engenharia):** executar o ML histórico em *Shadow* produtivo, se isso puder ser feito com segurança e um artefato admissível. O Shadow permite guardar previsões prospectivas no instante de emissão e desenhar séries por veículo/linha/horizonte, mas **não é pré-requisito** para a comparação offline do TCC. Não confundir Shadow ON com dados suficientes ou ganho de acurácia.

**Prazo:** sete dias (17–24/10/2026) para resolver os pontos de engenharia que impedem iniciar o experimento e produzir a primeira comparação **se já houver observações/chegadas auditadas suficientes**, sem contar o tempo de coleta adicional. Não existe garantia de resultado real válido se o acervo não possui labels nem viagens independentes. Ao descobrir falta de dados, quantificar precisamente a lacuna e iniciar coleta que permita continuar, sem passar a semana refazendo o GPS inteiro.

**Não fazer nesta semana:** reescrever Shadow, refazer matching ou o ML, prometer melhora, promover modelo experimental para ETA público, treinar no homeserver, alterar massivamente schema/Redis, relaxar gates oficiais sem criar categoria exploratória separada, usar passageiros/horário programado/ETA estimado como verdade de chegada, nem comparar resultados de relógios/alvos distintos.

**Dois desenhos científicos, sem misturar denominadores:**
- **Offline retrospectivo (prioritário):** reconstruir no mesmo `tGPS` as features causais e o ETA operacional conforme versão de código congelada; treinar e testar ExtraTrees em **viagens distintas com corte cronológico**, contra `TimestampPassagem` auditado. O ETA operacional reconstruído não deve ser chamado de previsão realmente exibida pelo aplicativo.
- **Shadow prospectivo (bônus):** coletar predições efetivamente emitidas e seu instante (`t0`), vincular chegada posterior por identidade exata e comparar os métodos sobre as mesmas observações. Quando houver acesso ao ETA exibido pelo endpoint de Radar, documentar também seu relógio e contrato; o `PublicEta` do hook GPS não é necessariamente esse valor.

## 2. Definições de pronto e ordem de prioridade

| Gate | Entrega verificável | Prioridade | O que NÃO comprova |
|---|---|---|---|
| **C0 — Labels utilizáveis** | Inventário de coortes reais, viagens/fronteiras auditadas, observações com identidade causal, chegada única confiável e regra de exclusão reproduzível. | **Crítica** | Dados GPS brutos ou passagens preliminares não equivalem a dataset certificado. |
| **C1 — ML histórico real offline** | Dataset congelado; split temporal por viagem; ExtraTrees treinado **somente em TRAIN** com dados reais auditados, ou relatório técnico de gate bloqueado; manifestos, hashes e inferência local verificáveis. | **Crítica** | Um modelo exploratório menor não é modelo qualificado para produção. |
| **C2 — Comparação TCC** | ETA operacional reconstruído com política/relógio explícitos e ExtraTrees no **mesmo teste**, mesmo alvo, MAE/MedAE/P90/viés/cobertura/±60/±120, intervalos quando viáveis, tabela por observação e figuras geradas apenas de dados reais. | **Objetivo final** | Ganho estatístico ou generalização não são garantidos. |
| **S0 — Captura produtiva apenas** | Shadow com ML OFF, exportação privada JSONL v2, métricas de perdas e rollback, sem mudança pública. | **Opcional, útil** | Não há ML em produção nem prova de acurácia. |
| **S1 — ML em Shadow produtivo** | Modelo **real qualificado/admissível** e serviço pronto, inferência correlacionada, isolamento/recursos seguros, controle de risco e autorização; resultados prospectivos exportados. | **Bônus condicionado** | Shadow isolado não resolve ausência de ground truth. |

**MVP científico aceitável:** uma comparação offline reexecutável entre o ETA operacional reconstruído e um ExtraTrees **efetivamente treinado com dados reais auditados**, em viagens de TEST não usadas no fit, validação nem na seleção, com cobertura e limitações transparentes. Se os dados só suportarem um estudo pequeno, rotular **EXPLORATÓRIO** e não usar linguagem de validação robusta ou promoção produtiva. Sem viagens independentes e labels fidedignos, não declarar MVP científico; o resultado da semana será o pipeline pronto + bloqueio quantitativo e coleta iniciada.

**Meta da semana:** priorizar **C0 → C1 → C2**; executar **S0/S1 em paralelo somente se não atrasar C0–C2**. O gate atual de qualificação produtiva permanece intacto. Um modelo exploratório pode rodar *offline*, mas nunca contornar validações de produção para satisfazer o calendário.

### Contrato mínimo do resultado do TCC

- `comparison.csv`: **uma linha por observação GPS + viagem + ocorrência + volta**; incluir `t_gps`, `t_chegada`, identidade de linha/modal/padrão, split, status de auditoria, `eta_operacional_reconstruido_s`, `eta_extratrees_s`, baselines opcionais, versão do código/modelo, motivo quando faltante e os erros por origem. Sem dados pessoais no material compartilhável.
- `metrics.json`: `n_elegiveis`, `n_predicoes`, `n_viagens`, `n_dias`, `n_linhas`, cobertura/MAE/MedAE/P90/viés/±60s/±120s para cada estimador, e métricas **pareadas apenas na interseção** da mesma coorte. Registrar a convenção P90 e eventual intervalo de incerteza por viagem.
- `figures/`: comparação MAE/P90 e cobertura; gráficos de erro por horizonte/distância **se a amostra permitir**; metadados para reproduzir cada figura. Com Shadow produtivo, séries por linha/viagem podem ser acrescentadas **sem substituir** a comparação contra a chegada real.
- `experiment_manifest.json` e `README.md`: seleção/exclusões, política dos relógios, hashes de dataset/split/código/modelo, comandos, data de corte, limites e declaração `QUALIFICADO` ou `EXPLORATÓRIO` ou `SEM DADOS SUFICIENTES`.

A primeira comparação ilustrativa com fixtures serve **só para testar software**. Nenhum número ou gráfico sintético entra como resultado empírico do TCC.


## 3. Estado herdado do acervo de 16/10/2026

| Componente | Estado documental | Prova/pendência a verificar agora |
|---|---|---|
| Backend .NET9 Shadow histórico | **Implementado** no `main` examinado (SHA `90f7be72`), integrado pós-CAS, fila não bloqueante, worker separado e resolver PostGIS `READ ONLY`. OFF por default. | HEAD/worktrees locais, override efetivo de produção, teste E2E vigente. |
| Export JSONL v2 e avaliador `evaluate_shadow.py` | **Implementados/testados com fixtures**, quota e retenção limitada; métricas de perda e correlação por observação/viagem/alvo. | Arquivos gravados de verdade, permissões, espaço, taxas de perda e recuperação antes da retenção. |
| FastAPI `eta_history_service.py` | **Implementado** na branch ML `feature/eta-history-mvp` (`6c0f7b5`), `/health`, `/ready`, `/eta/batch`; sem artefato real, 503 esperado. | Imagem atualizada, modelo real, handshake digest/contrato, teste de RSS/p95 com o artefato concreto. |
| ExtraTrees + medianas/baselines + `qualify_real/offline_cycle` | **Implementado e testado com sintéticos/fixtures**; artefato real qualificado não demonstrado. | Dados, splits temporais, manifesto de procedência pós-GTFS-RT, qualificação, fit/benchmark. |
| Ground truth | Journal e passagens existem, mas a última coorte conhecida tem **zero viagens/observações integralmente certificadas**. | Prova de fronteiras e ausência de proteções por viagem, data/versão, labels únicos. |
| Dados locais históricos | Bundle conhecido: 775 GPS, 188 históricos, 220 eventos, 16 viagens, 758 candidatas **preliminares**, um dia UTC. | Procurar **outras coortes reais** com proveniência e não duplicar snapshots da mesma coorte. |
| Gate oficial de treino | `>=10.000` observações elegíveis, `>=14` dias UTC, viagens inteiras `>=200/50/50` TRAIN/VAL/TEST; condição de linha `30/10/10` e 8 horas distintas TRAIN. | Incompatível com começar do zero e fechar em uma semana; não afrouxar em silêncio. |
| GPS 4C.9 | Quatro cursores adiantados; distinção nova viagem × regressão/matching **não provada**. Patch indiscriminado arriscaria duplicar passagens. | Buscar UM par correlacionado da primeira regressão ou prova de fim/partida; não reabrir investigação ampla. |
| PhysicalEta no Shadow | Novo `physical_apply_reason` diagnostica causas em futuras capturas; cinco registros antigos sem causa provada. | Captura causal futura; não reclassificar os cinco registros retroativamente. |
| Produção | Servidor Debian com RAM limitada (~3,7 GiB), swap, carga PostGIS; ML já apresentou `exit 137` histórico compatível com pressão de memória. | Estado real **não verificado**: CPU/RSS/swap, containers, override e disponibilidade. |

**Notas de arquitetura que evitam retrabalho:** o adapter Shadow não deve ser trocado pelo `GpsEtaClient` legado; o campo de distância direta ao alvo não substitui distância por geometria versionada; a inconsistência de 401 distâncias foi investigada e a política de rota 3G.2 foi implementada. O `PublicEta` capturado no polling **não representa automaticamente** a consulta posterior do endpoint `/paradas/{paradaId}/eventos`.

## 4. Caminho crítico e bifurcação de dados

```text
                      [AUDITORIA: código + evidências + dados reais]
                                          |
                        [C0: chegada e viagem auditadas?]
                           /                         \
                     SIM                                 NÃO
                      |                                   |
 [congelar corpus / corte / split]           [medir motivos, selecionar 1 defeito
                      |                      com prova e iniciar captura válida]
         [treinar ExtraTrees offline]                      |
                      |                            [não alegar ganho ML]
   [reconstruir ETA fixo no MESMO teste]
                      |
       [C2: pares, erros, gráficos TCC]

        Trilha independente de engenharia (não bloqueia comparação offline):
          [S0 Shadow captura] → [modelo REAL qualificado + benchmark] → [S1 ML Shadow]
```

**Decisão até 19/10 (D2):** verificar existência de *dados reais auditados*, não apenas arquivos CSV. Inventariar coortes por origem, data, commit, cobertura, observações elegíveis, viagens únicas, razão de rejeição e hash. Classificar:

- **A — suficiente para gate oficial:** `qualify_real.py --dry-run`, treinamentos/avaliações e futura homologação S1 segundo política oficial.
- **B — insuficiente para gate oficial, mas há viagens reais auditadas independentes:** estudar execução **exploratória exclusivamente offline**, com protocolo explícito, split temporal por viagem, nenhum vazamento e amostra/limitações reportadas. Os critérios oficiais de 10k/14 dias/200–50–50 não mudam, e qualquer adaptação de CLI deve ser um caminho de pesquisa *separado* que não falsifique manifesto/recibo produtivo.
- **C — sem labels/viagens confiáveis:** prioridade vira produzir o **menor conjunto de evidências de chegada auditada** e consertar apenas defeito causal provado. Não treinar em labels inventados, não produzir MAE fictício. Preservar protocolo e começar coleta seletiva imediatamente.

**GPS 4C.9:** não baixar cursor nem refazer matching por suposição. Isolar casos não certificados e tentar obter uma prova delimitada de fronteira de execução/primeira regressão. A dificuldade de quatro versões **não impede analisar coortes verdadeiramente auditadas de outras linhas**, se elas existirem.

## 5. Cronograma de sete dias centrado no TCC

| Data | Entrega principal | Critério de saída | Se não houver evidência |
|---|---|---|---|
| **17/10 — D0** | Auditoria dirigida do **caminho offline**: coortes/labels, gerador, política do ETA fixo e treino ML; inventário do estado do Shadow como adicional. | `AUDITORIA_D0_ETA_ML_TCC.md` com código/HEAD, dados localizados, três bloqueios testáveis, decisão A/B/C. | Registrar como NÃO VERIFICADO; não inventar dados ausentes. |
| **18/10 — D1** | Provar o fluxo **GPS real → chegada real auditada → amostra elegível**; fechar relógio/alvo/identidade e uma amostra E2E mínima. | Pelo menos uma linha de amostra verificável **se disponível** + contagem de aceitos/rejeitados e provas; caso contrário diagnóstico causal focal. | Corrigir apenas o defeito demonstrado por teste; abrir coleta limitada. |
| **19/10 — D2** | Decidir A/B/C: inventário de coortes, cortes pós-GTFS-RT, fronteiras, split por viagem; dry-run da qualificação oficial quando cabível. | Matriz com N observações / viagens / dias / linhas e restrições exatas para experimento. | Não travar na investigação de cursor; caminho C com coleta e prova mínima. |
| **20/10 — D3** | Executar **pipeline offline mínimo** (export → manifesto → QA → features) em artefatos privados; implementar apenas adaptação de estudo exploratório se justificada e isolada. | Corpus versionado e congelado ou erro reproduzível, com zero vazamento. | Abrir um único ticket de bloqueio com teste decisivo. |
| **21/10 — D4** | Gerar ExtraTrees real a partir de TRAIN (A: qualificado; B: exploratório offline). Reconstruir ETA fixo com a versão/relógio corretos. | Inferência real para mesmo TEST/observação/alvo; manifestos e hashes. | Sem dados aptos, registrar limitação e priorizar fonte/label. |
| **22/10 — D5** | Produzir tabela pareada e métricas da comparação offline (MAE, MedAE, P90, viés, cobertura, ±60/±120, pares). | `comparison.csv`, `metrics.json` e testes de regressão/verificação, **se houver TEST real**. | Publicar somente metodologia, faltantes e critérios; nunca curvas falsas. |
| **23/10 — D6** | Montar gráficos e resultados para TCC; se C0–C2 não estiverem em risco, fazer preflight S0/S1 de Shadow, com autorização e avaliação de recursos. | Figuras reprodutíveis ou relatório de insuficiência; decisão GO/NO-GO para Shadow. | Seguir coletando; Shadow não deve atrasar comparação. |
| **24/10 — D7** | Congelar relatório, versão do pipeline e histórico; opcionalmente testar S0/S1 em janela isolada **somente com gates/autorizações atendidos**. | Relatório científico inicial, artefatos e próximos dados necessários; status separado C0/C1/C2/S0/S1. | Encerrar semana com prova do bloqueio e plano de coleta, não com refatoração infinita. |

**Regra de progresso:** nenhuma investigação ganha mais de um dia sem mudar uma decisão de A/B/C, explicar uma perda quantitativa ou produzir uma evidência nova. A comparação offline pode prosseguir sem servir HTTP em produção.

## 6. Testes necessários — ordem e interpretação

### T0. Reconciliação de estado

- Registrar localmente `git status --short`, `git rev-parse HEAD`, branches/worktrees de **backend** e **ML**; comparar com `90f7be72` e `6c0f7b5` sem descartar alterações. Apontar onde está a implementação vigente, sem duplicar feature branches antigas.
- Em produção, **somente mediante autorização**, confirmar quais overrides estão ativos, sem expor valores sensíveis. CI/deploy bem-sucedido não prova containers/flags funcionando.

### T1. Captura e invariantes do GPS

- Casos BUS/BRT individuais e lote, GPS aceito/rejeitado, CAS, identidade/versão/volta/ocorrência, relógios, cursor regressivo, `ItineraryChanged`, mudança de trip e ausência de PhysicalEta. Comparar Shadow ON/OFF: **mesmo ETA/DTO/Redis/SignalR público**.
- Observar contagens **candidatos → elegíveis → fila → resolvidos → exportados → com label** e razões de perda, com denominadores diferentes explícitos; JSONL v2 não é garantia de captura completa.
- Reutilizar `HistoricalEtaShadowTests`, `HistoricalEtaRuntimeTests`, `HistoricalEtaGtfsPollingTests`, `HistoricalEtaOfflineExportTests`, `PhysicalEtaCausalDiagnosticsTests` e fixtures conectadas **apenas descartáveis** quando autorizadas.

### T2. Qualidade do dataset e chegada

- Exigir *mesma* `observation_id/ViagemId/linha/sentido/padrao-versão/ocorrência/volta`, prova de viagem inteira sem proteção, `TimestampGps <= t_predição < TimestampPassagem`, identidade e label único auditado.
- Classificar perdas: sem GPS elegível, sem identidade, sem próxima parada, cursor 4C.9, sem passagem, duplicidade, mudança de regime, sobreposição temporal, canal/sink/TTL, fim de viagem e dados posteriores a `t0`.
- Checar fonte/regime pós-GTFS-RT com cortes de deploy provados; não aplicar perfis `historical-pre-fix` ou `official-post-fix` indiscriminadamente a dados de 09/10 em diante.
- Separar viagem inteira por tempo em TRAIN/VAL/TEST; purgar viagens cruzando cortes. `qualify_real --dry-run` é o gate factual, não o volume GPS bruto.

### T3. Modelo e comunicação

- Testar inferência do **serviço histórico**, não `server.py` legado: `/health` 200 não basta; `/ready` **com digest esperado** deve ser 200 antes do Shadow com ML, e 503 sem modelo deve ser tratado como indisponibilidade.
- Provar request/response completos, ordem, `request_id`, `X-Model-Data-Kind=real`, `X-Model-Version`, política, features causais, distância por rota versionada, horário, 200 itens/256 KiB e limite de payload.
- Falhas 503, timeout, digest divergente, payload sintético, modelo ausente, circuito aberto e worker reiniciado **não** alteram o ETA público.
- Testar hardware real com modelo: RSS máximo, p50/p95/p99, throughput, memória de startup, swap e impacto em PostGIS/API. Não aumentar arbitrariamente timeout ou fila para fazer “passar”.

### T4. Pareamento, baselines offline e TCC (crítico)

- Export JSONL v2 com diretório **novo** e privado; controlar fila128/arquivo1 MiB/total16 MiB/32 arquivos/retenção24h **como defaults de código**, não presumir override; mover cópias auditadas antes de expirar. Não exportar coordenadas/identificadores físicos/segredos.
- Associar previsão e passagem pelo identificador exato, nunca por proximidade de horário/parada. Registrar relógios (`tGPS`, `t0` captura, `t_predição`, `t_chegada`) e idade GPS.
- **Primário:** *replay offline* do ETA operacional **reconstruído** e do ExtraTrees histórico em `tGPS`, mesma observação/alvo, corte e regras de features; previsão convertida em instante de chegada. **Secundário/bônus:** comparação Shadow prospectiva no `t0` de previsão. Não chamar `PublicEta` do hook de ETA de Radar mostrado a usuário sem evidência; `EventosParadaService` exige experimento/relógio separados.
- Métricas: `MAE`, `MedAE`, `P90` com convenção única, viés, `|erro|<=60s`, `|erro|<=120s`, cobertura de cada estimador, interseção comum e vitórias/empates pareados. Sempre mostrar `n observações/n viagens/n dias/n linhas`; recortes mínimos antes de declarar superioridade. Não usar resultado sintético como ganho real.

## 7. Regras de intervenção para não andar em círculos

1. **Primeiro reproduzir, depois corrigir**: não alterar produção nem schema por hipótese; defeito funcional exige par causal/fixture e teste RED quando aplicável.
2. **Um gargalo de cada vez**: todo bloqueio tem ID, hipótese, evidência disponível, teste decisivo, próximo decisor e prazo de revisão de no máximo um dia.
3. **Não reabrir o que já foi resolvido**: review de distância direta × rota (3G.2), identidade do request, isolamento do adapter, importação leve, associação por alvo e regra de cursor 4C.9 sem nova prova.
4. **Proibido concluir “funcionou” só por CI verde ou `/health=200`**: C0/C1/C2/S0/S1 exigem evidências diferentes.
5. **Se o diagnóstico não altera a comparação do TCC ou o fornecimento de labels, estacionar** no backlog. Cursor 4C.9 recebe investigação limitada para obter evidência de fronteira, não redesign amplo.
6. **Segurança operacional**: nunca ativar flags, subir containers, executar migration, alterar overrides ou acessar banco/host de produção sem autorização explícita para aquela ação; preflight e rollback definidos antes.
7. **Paridade temporal**: dado futuro não entra no treino/features; mesmo relógio e mesmo alvo, sem leakage.
8. **Governança do ML**: manifesto/hash são necessários mas não suficientes; só artefato confiável e auditado pode ser desserializado.

## 8. Sinais de parada imediata e rollback

- ETA público/Redis/SignalR divergiu com Shadow ON versus OFF; bug em GPS/hot path; aumento sustentado de latência ou filas afetando ciclo GPS.
- OOM/`exit 137`, swap crescente, degradação de PostGIS/API, timeout/circuito em cascata, uso de disco acima da quota.
- Falha de privacidade/permissões, arquivo exportado fora do diretório dedicado, sem contagem de perdas, modelo com digest/tipo/política divergente.
- Primeira ação: desligar opt-ins `HistoricalEtaShadow` de ML/histórico/export (ou `Enabled` geral, segundo plano revisado), mantendo ETA público/PhysicalEta legado intactos; confirmar API/GPS pós-rollback e preservar prova sanitizada. Não usar banco como mecanismo de rollback para o Shadow.

**Configuração inicial a avaliar**, **não aplicada**: P0 com `Enabled=true`, `OfflineExport.Enabled=true`, `MlEnabled=false`, `HistoricalEnabled=false`; `QueueCapacity=64`, `BatchSize=16`, `SinkCapacity=128`, `TimeoutMs=500`, `MaxAgeSeconds=90`, conforme proposta da integração de 15/10. Esses valores exigem revisão de custo e configuração efetiva antes de ação no host.

## 9. Critérios de decisão por produto de pesquisa

| Pergunta | Prova exigida |
|---|---|
| “Temos ground truth?” (C0) | Chegadas identificadas por viagem/ocorrência/volta, fontes, fronteiras e auditoria de qualidade; n elegíveis/rejeitados; não considerar 758 candidatos como labels válidos. |
| “Temos ML treinado com dados reais?” (C1) | ExtraTrees, manifestos e hashes, TRAIN anterior ao TEST por viagens completas, sem features futuras; qualificado **ou exploratório offline claramente rotulado**. |
| “Comparação serve ao TCC?” (C2) | Comparação pareada, mesmas identidades/alvo/tempo, descrição da reconstrução ETA fixo, cobertura e métricas, incerteza adequada ao n, conjunto TEST intocado, scripts reexecutáveis. |
| “ML realmente melhora?” | Erros MAE/MedAE/P90 e acertos pareados versus baseline; n viagens/dias/linhas e limites; hipótese pode ser rejeitada ou inconclusiva. |
| “Cabe Shadow na produção?” (S0/S1) | Código/artefato versionados, modelo qualificado + digest (S1), readiness, recursos, preflight, métricas de perdas, zero alteração pública, rollback e autorização por janela. |

**Meta de visualização TCC:** gráfico de barras MAE/P90 por método e cobertura; dispersão erro versus distância/horizonte; caso S1 esteja ativo, evolução temporal por observação/viagem. Só plotar pontos reais elegíveis; com poucas viagens, gráficos descritivos sem inferir significância ou generalização.

**Limites produtivos:** RSS/CPU/p95 definidos após medir baseline do servidor e custo do modelo. Sem modelo admitido e sem autorização, testes de Shadow ficam em ambiente isolado; não afrouxar digest, data_kind ou `qualification receipt`.

## 10. Primeira solicitação ao Codex — auditoria de viabilidade do TCC (read-only)

Executar **na raiz do workspace backend e, se disponível, no workspace ML**, com acesso aos documentos `noponto-docs` e eventuais **artefatos locais não sensíveis**, sem acesso remoto/produção, sem editar código e sem fazer treinamento. Se um dado estiver só no servidor, reportar `NÃO VERIFICADO` e a consulta read-only necessária. Salvar somente o relatório `AUDITORIA_D0_ETA_ML_TCC_20261017.md`, preservando WIP.

> Você é o auditor de viabilidade experimental do NoPonto. O objetivo **PRINCIPAL** em 7 dias é produzir uma **comparação offline real, causal e reproduzível** do ETA operacional/fixo versus um modelo histórico **ExtraTrees** para o TCC; um Shadow em produção é **bônus**, não dependência. Leia `noponto-docs/GUIA_DE_RETOMADA.md`, `09-validacao/10-protocolo-comparativo-eta-offline.md`, `03-funcionalidades/19-eta-limitacoes-testes-e-roadmap.md`, `09-validacao/06-fechamento-eta-ml-mvp-20261011.md` quando disponíveis. Depois confira o **código atual** do backend e ML, registrando branch/HEAD/status/worktrees, sem checkout/reset. Identifique por `arquivo:linha` a cadeia concreta: dados GPS reais → viagens/passagens com fronteiras auditadas → exportação canônica e perfis pós-GTFS-RT → features/splits sem vazamento → `train/qualify_real/offline_cycle` e serviço ExtraTrees → reconstrução da fórmula REAL do ETA operacional (`EventosParadaService`, distinguindo `PublicEta` do hook) → pareamento e métricas. Diga se EXISTEM hoje bundles/datasets/modelos reais verificáveis e onde (sem abrir nem copiar PII/segredos), quantas **viagens auditadas** de fato há versus candidatas preliminares, qual é o impedimento específico para produzir **a primeira tabela comparativa em TEST** e se caberia um estudo **exploratório OFFLINE** com amostra menor sem adulterar gates produtivos. Distingua código existente, prova local testada, dados reais auditados e documentação histórica. Em até 12 checks, apresente matriz evidenciada e **somente os 3 maiores bloqueios** com UM teste decisivo por bloqueio; proponha a menor sequência de alterações possível, com GO/NO-GO para C0 labels, C1 modelo real offline, C2 comparação pareada e, em apêndice breve, S0/S1 Shadow. Não reescreva arquitetura, não mexa no cursor 4C.9 sem prova, não faça `dotnet run`, `train`, `docker`, deploy, migrations, banco/Redis reais, não altere flags e não leia segredos. Gere apenas `AUDITORIA_D0_ETA_ML_TCC_20261017.md` e um resumo de no máximo 15 linhas. **Priorize desbloquear a comparação offline, não o deploy.**

**Saída esperada:** uma auditoria sobre o **estado presente**, três bloqueios falsificáveis, primeiro teste reproduzível, evidência se há corpus/labels/modelo real e lista curta de ações que levam ao primeiro par ETA fixo × ML na mesma chegada.

## 11. Diário/gestão

Usar arquivo companheiro `DIARIO_ETA_ML_SHADOW_NOPONTO_2026-10-17.md` (nome legado mantido por continuidade). Cada avanço exige: data, ambiente, commit, hipótese, teste, resultado, evidência/artefato, risco residual, próximo teste e GO/NO-GO. Registrar decisões, inclusive “não corrigir” ou “dados insuficientes”. Revisar a cada dia sem recontar toda a história. Ordem de revisão: **C0/C1/C2**; S0/S1 apenas depois.

## 12. Fontes consultadas na preparação

- `noponto-docs/GUIA_DE_RETOMADA.md` (16/10)
- `noponto-docs/03-funcionalidades/19-eta-limitacoes-testes-e-roadmap.md` (16/10)
- `noponto-docs/03-funcionalidades/17-datasets-treinamento-e-validacao.md`
- `noponto-docs/09-validacao/06-fechamento-eta-ml-mvp-20261011.md`
- `noponto-docs/09-validacao/10-protocolo-comparativo-eta-offline.md`
- `noponto-docs/00-auditoria/20-consolidacao-documental-20261016.md`
- `noponto-docs/00-auditoria/07-infraestrutura-producao.md`
- [Integração Shadow real, relatório 17 (15/10)](https://github.com/UVAEstudantes/noponto-backend/blob/90f7be72e4b55198bc721f4f104a58f37def3f2d/docs/09-validacao/17-integracao-shadow-real-20261015.md)
- [GPS 4C.9, relatório 23 (14/10)](https://github.com/UVAEstudantes/noponto-backend/blob/90f7be72e4b55198bc721f4f104a58f37def3f2d/docs/00-auditoria/23-cursor-adiantado-4c9-20261014.md)
- [Diagnóstico causal PhysicalEta/Shadow, relatório 19 (15/10)](https://github.com/UVAEstudantes/noponto-backend/blob/90f7be72e4b55198bc721f4f104a58f37def3f2d/docs/09-validacao/19-diagnostico-causal-physical-shadow-20261015.md)
- [ML README ETA 3C, branch feature](https://github.com/UVAEstudantes/noponto-ml/blob/6c0f7b5313f1d953105d0211f08df6cdaf098e48/README_ETA_3C.md)

**Confiabilidade:** documentação e leitura estática de arquivos pinados em 16/10; sem acesso a runtime atual, bundles privados, overrides, host, modelos reais ou base produtiva em 17/10. Testes históricos indicados não foram reexecutados nesta redação.
