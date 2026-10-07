# Etapa 3B — primeiro pipeline histórico ETA

07/10/2026. Pipeline offline implementado, com treino/avaliação reproduzíveis e artefato **sintético**. Nenhuma métrica abaixo representa produção. **Etapa 3B.1: adapter conectado APROVADO em PostgreSQL 16.4/PostGIS 3.4.3 Docker local descartável**, com SQL 3A original, journal, CSV/manifest e check_volume. Detalhes e limites do ensaio estão na seção 3B.1 abaixo. Dataset/treino real e certificação automática de procedência continuam pendentes.

## Contexto oficial fornecido

| Campo | Valor |
|---|---|
| collection_started_at_utc | `2026-10-07T14:53:59.225082Z` |
| backend commit | `3e40d92327c517a4f2e5342cfe6f84a5be9568ee` |
| image | `sha256:2bbb601108bfe9c7d0f7723da3bc3b2f1c696868e7dfe88f087b345d733eb0eb` |
| dataset contract | `noponto-eta-gps-v1` |
| sampling | enabled=true; line_percentage=10; block_minutes=60; seed=NOPONTO_ML_V1 |
| migration | `20261006180000_IntegridadeCircularDuravel` |

Esses valores foram informados pelo operador e são preservados no template/manifesto; não houve inspeção de produção. Sampling é seleção determinística de linhas por bloco, não 10% independente dos GPS. O pipeline não altera sampling, polling, ETA público, migrations ou telemetria existente.

## Arquitetura mínima e arquivos

Snapshot PostgreSQL local + auditoria de viagens completas → adapter Npgsql READ ONLY → validador/CSV 3A + dataset manifest → treino Python → dois baselines + ExtraTrees → métricas + model manifest + artefato. Avaliação separada recarrega o artefato e reproduz a mesma divisão, sem novo fit.

| Arquivo (relativo à raiz do repositório) | Responsabilidade |
|---|---|
| `tools/EtaMl.Export/Program.cs` | Adapter offline, fronteiras do journal, paginação, CSV e manifesto |
| `tools/EtaMl.Export/EtaMl.Export.csproj` | Console net9; reutiliza código/dependências da API sem executar seu entry point |
| `tools/EtaMl.Export/audit.example.json` | Template do marco oficial, snapshot e lista auditada de viagens |
| `tools/EtaMl.Export/options.example.json` | Fronteiras e limites de exportação propostos |
| `tools/eta_ml/pipeline.py` | Contrato, validação, features, baselines, modelo, métricas e volume |
| `tools/eta_ml/train.py` / `evaluate.py` | CLIs de treino e reprodução da avaliação |
| `tools/eta_ml/check_volume.py` | Relatório objetivo de suficiência; exit 2 quando insuficiente/sintético |
| `tools/eta_ml/synthetic.py` | Fixture controlada de 240 viagens, sem banco |
| `tools/eta_ml/config.synthetic.json` / `config.real.example.json` | Cortes UTC, seed e mínimo para breakdown |
| `tools/eta_ml/requirements.txt` | Versões do ambiente Python inspecionado, fixadas |
| `tools/eta_ml/test_pipeline.py` / `test_exporter.py` | Testes ML e guards do exportador compilado |
| `tools/eta_ml/examples/synthetic.model.manifest.json` | Exemplo persistido do manifesto/métricas efetivamente gerados |
| `.gitignore` em cada ferramenta | Exclui bin/obj, caches, ambiente, dados/artefatos locais |
| `NoPonto/2-Application/Services/GPS/EtaDataset.cs` | Pequena correção: rejeitar execução inteira fora da janela |
| `NoPonto/5-Testes/EtaDatasetTests.cs` | Regressão do cutoff e limite final por execução |
| Este documento e `NoPonto/HISTORICO.md` | Entrega, limites e validações |

O serviço externo citado nas docs antigas não está neste checkout; não se pressupõe existência de `treinar.py` antigo. Inspecionados csproj, fontes ML/ETA, SQL/contrato 3A e ambiente: Python 3.13.14, scikit-learn 1.6.1, NumPy 2.2.6, SciPy 1.16.1, joblib 1.4.2, threadpoolctl 3.6.0. CatBoost ausente. Nenhuma biblioteca Python instalada nesta etapa. O console alinha EFCore.Relational 9.0.10 à versão já usada por Design/API para evitar resolver transitivamente 9.0.0; nenhum pacote/configuração da API foi alterado.

## Dataset, cutoff e ausência de vazamento

Mantido exatamente o cabeçalho/features do contrato 3A. `posicao_gps` e `posicao_destino` são frações no traçado versionado; esta versão não acrescenta latitude/longitude nem features novas.

Allowlist explícita usada pelo modelo:

```text
modal, linha_id, codigo_linha, sentido_id, padrao_id, versao_id,
ocorrencia_id, parada_id, topologia, posicao_gps, posicao_destino,
distancia_metros, velocidade_kmh, velocidade_media_causal_kmh,
hora_dia, dia_semana
```

IDs estruturais acima são categorias autorizadas pelo contrato. `observacao_id`, `viagem_id`, `volta`, `veiculo`, `provedor`, `timestamp_gps`, `split`, referência de auditoria, `timestamp_passagem` e `label_segundos` nunca entram na matriz de features. Hora/dia vêm do GPS em UTC−03:00; dia da semana 0=domingo. Label mantém passagem−GPS, positivo e até 3600s. Velocidades inválidas viram ausentes; não há imputação de label.

Uma viagem com início anterior ao cutoff é integralmente excluída, mesmo com GPS posterior. Início exatamente no cutoff é elegível. Execução com fim no limite final ou depois também é excluída. Nenhuma fronteira é inferida do primeiro/último GPS amostrado. Para os cortes de TRAIN/VALIDATION/TEST, a viagem que começa antes e termina no corte ou depois é purgada inteira. Começar exatamente no corte pertence ao split seguinte. Os mesmos cortes são exigidos pelo CSV e treino; discrepância de split é erro. Viagens/observações repetidas são rejeitadas; não há random split por linha.

O adapter reutiliza o SQL 3A sem modificá-lo, confere eventos `inicio:{ViagemId}` / `fim:{ViagemId}` no journal, versão/evento/código/timestamps, e entrega candidatos ao validador 3A existente (mesma execução/volta/estrutura, passagem com journal e distância geography). Leitura em `REPEATABLE READ` + `READ ONLY`; conexão somente localhost/127.0.0.1/::1, destinada a cópia local. Não inicializa API/hosted services/migrations. **Loopback por si só não prova que o banco é descartável: o operador deve identificar o snapshot local, sem túnel de produção.**

Auditoria continua obrigatória. Cutoff/build corretos, fronteiras no journal e ausência de marker atual não certificam ausência de proteção ao longo da execução. `AuditadaSemProtecao` só pode ser atribuída com evidência externa da execução completa, incluindo intervalos protegidos/ambíguos. O exportador verifica consistência, não autenticidade de atestados. Snapshot local e manifesto auditado devem conservar versões estruturais/journal/histórico/telemetria. Mudança de release durante a janela exige reauditoria e ajuste explícito do contrato de coleta, não trocar silenciosamente os campos oficiais.

Saída: `dataset.csv`, `dataset.manifest.json` com versão, cutoff, contexto oficial, snapshot/auditoria, fronteiras, viagens, features, contadores de candidatos/descarte e hashes do CSV/auditoria/SQL/opções. Apenas manifestos concluídos podem ser usados para treino; falha deixa diretório incompleto, que não é dataset publicável. Diretórios existentes não são sobrescritos. Candidatos sem passagem/journal/estrutura são contabilizados antes do validador; quality/identity/time/distance/dedup são contabilizados por ele. Isso não mede GPS que sampling/backpressure nunca persistiram.

Limites herdados/configuráveis: 10.000 candidatos/viagem, 1.000 páginas externas, 1.000 páginas SQL por viagem de 1.000 GPS. A consulta percorre GPS da mesma linha/intervalo antes de filtrar viagem; execuções concorrentes podem aumentar custo. Não foi medido plano/custo do adapter novo; não proposto índice por hipótese. O treino MVP aceita até 200.000 linhas em memória e roda em uma thread; usar janela menor ou evoluir processamento offline quando necessário. Nenhum treino no servidor de 4 GB.

## Baselines e modelo

**Físico:** `distancia_metros * 3.6 / velocidade_kmh`. Preferência pela média causal válida, depois velocidade atual válida (1..160 km/h). Ausente, zero ou muito baixa usa mediana de `distância/label` do TRAIN, limitada a 5..80 km/h. Nunca usa velocidades/labels da avaliação para calibrar fallback. Essa hipótese de velocidade positiva é aproximação para veículo parado; não modela duração de parada.

**Histórico:** mediana de label por modal, código de linha, sentido, versão, ocorrência, faixa de distância e bloco de 4 horas, com pelo menos 5 linhas no grupo de TRAIN. Grupo raro/desconhecido usa mediana global do TRAIN. É baseline deliberadamente simples; as cinco linhas não equivalem a cinco viagens independentes.

**ExtraTreesRegressor:** 64 árvores, profundidade máxima 18, no máximo 4096 folhas/árvore, mínimo 3 linhas/folha, seed 20261007, n_jobs=1. Árvores capturam relações não lineares; categorias recebem one-hot sparse, frequência mínima 5 e categorias desconhecidas são aceitas. Imputação mediana/indicadores de ausência e encoder são fitados só no TRAIN. Artefato contém preprocessing, modelo e baselines. Não há tuning nesta versão nem refit com VALIDATION antes de medir TEST.

Escolha aproveita scikit-learn já disponível, CPU e árvores fáceis de descrever no TCC. Evita instalar CatBoost só para a primeira versão. Limites: não extrapola bem além do domínio de treino; IDs estruturais podem mudar; categorias novas e ausência de velocidade precisam ser acompanhadas nas métricas reais. Documentação primária: [ExtraTreesRegressor](https://scikit-learn.org/1.6/modules/generated/sklearn.ensemble.ExtraTreesRegressor.html), [OneHotEncoder](https://scikit-learn.org/1.6/modules/generated/sklearn.preprocessing.OneHotEncoder.html).

## Avaliação e artefatos

MAE, RMSE, mediana e P90 do erro absoluto em segundos; coverage = previsões finitas e não negativas / linhas elegíveis do split. Não confundir coverage de previsão com cobertura do sampling/coleta/labels. Erros são ponderados por observação; viagens com mais GPS pesam mais. Intervalos de confiança por viagem e métricas ponderadas por viagem ficam para avaliação real.

Breakdowns por modal, código da linha, distância (<250m, 250..<1000m, >=1000m) e hora local (0..23), quando grupo tem >=30 linhas e >=3 viagens. Nenhuma linha difícil é removida por categoria desconhecida. Relatório inclui TRAIN, VALIDATION e TEST; TRAIN é diagnóstico de ajuste, não generalização. Pequenos grupos omitidos não autorizam conclusão sobre suas linhas/horários.

`model.pkl`, `metrics.json`, `model.manifest.json`: features, contrato, tipo real/sintético, cutoff, cortes UTC configurados, início/fim efetivos das viagens por split, viagens/linhas/observações, métricas/breakdowns, seed, versões de Python/scikit-learn/NumPy/SciPy, hiperparâmetros e SHA256 do artefato/dataset/manifesto/config/fonte. `evaluate.py` verifica hash de artefato/config, exige o dataset/divisão originais e não faz fit. Pickle deve ser carregado somente de fonte local confiável; o hash confirma integridade, não torna fonte desconhecida confiável.

## Reprodução sintética executada

240 viagens, 4 linhas, 1920 observações em cinco dias. TRAIN: 144 viagens/1152 linhas; VALIDATION: 48/384; TEST: 48/384. Cada split tem quatro códigos de linha. Velocidades ausentes, atrasos por hora e relações distância/velocidade são controlados pela fixture. Não simula sampling/coleta nem certificação PostgreSQL.

**TEST exclusivamente sintético — 384 observações:**

| Método | MAE (s) | RMSE (s) | Mediana AE (s) | P90 AE (s) | Coverage |
|---|---:|---:|---:|---:|---:|
| Físico | 9,85 | 16,24 | 5,91 | 21,02 | 100% |
| Histórico | 38,71 | 50,44 | 32,07 | 80,13 | 100% |
| ExtraTrees | 8,04 | 12,30 | 4,73 | 16,85 | 100% |

Esses resultados apenas comprovam execução/coerência do pipeline. A fixture é favorável à relação física e não demonstra desempenho, melhoria ou capacidade de generalizar para operação real. Breakdowns modal/linha/distância gerados; hora no TEST ficou sem grupos suficientes (24 linhas/hora <30). Dois treinos idênticos nos testes geraram métricas e hash binário idênticos no mesmo ambiente. Reprodução por `evaluate.py` confirmou relatório idêntico. Não prometida identidade binária entre versões/plataformas diferentes.

Artefato local gerado em `tools/eta_ml/outputs/model-synthetic-3b/model.pkl`, ignorado pelo Git. SHA256:

```text
9c0c374df867c448ca569715df572a8b7dab769bb7f0adcb973081f20eeecf6d
```

Manifesto completo com métricas preservado em `tools/eta_ml/examples/synthetic.model.manifest.json`; `data_kind=synthetic`, `real_performance_claim=false`. Dados/artefatos locais são recriados pelos comandos abaixo. Usar nomes novos se os diretórios já existirem.

```powershell
python tools/eta_ml/synthetic.py --output tools/eta_ml/outputs/synthetic-3b
python tools/eta_ml/train.py --dataset tools/eta_ml/outputs/synthetic-3b/dataset.csv --manifest tools/eta_ml/outputs/synthetic-3b/dataset.manifest.json --config tools/eta_ml/config.synthetic.json --output tools/eta_ml/outputs/model-synthetic-3b
python tools/eta_ml/evaluate.py --dataset tools/eta_ml/outputs/synthetic-3b/dataset.csv --manifest tools/eta_ml/outputs/synthetic-3b/dataset.manifest.json --config tools/eta_ml/config.synthetic.json --model tools/eta_ml/outputs/model-synthetic-3b/model.pkl --model-manifest tools/eta_ml/outputs/model-synthetic-3b/model.manifest.json --output tools/eta_ml/outputs/model-synthetic-3b/reevaluation.json
```

Ambiente Python novo, se necessário (não instalado nesta execução): criar venv e instalar `tools/eta_ml/requirements.txt`. Usar Python 3.13 para reproduzir o ambiente validado. Manter treino fora do processo/container da API.

Este documento novo é ignorado pela regra existente de Markdown em NoPonto. Ao preparar um checkpoint, incluí-lo explicitamente com `git add -f NoPonto/docs/ETA_ML_TREINO_3B.md`. Não foi alterada a regra global nem realizado staging nesta etapa.

## Exportação futura de dados reais

Não executada. Primeiro disponibilizar snapshot local já autorizado, com as fontes completas, e comprovar a auditoria. Copiar `audit.example.json` para arquivo local ignorado, preencher referência de snapshot/evidência e `Trips` reais. Não criar fronteiras a partir de GPS amostrado. Formato de cada viagem:

```json
{
  "ViagemId": "UUID-real-da-execucao",
  "Inicio": "timestamp-real-do-evento-ViagemIniciada-com-timezone",
  "Fim": "timestamp-real-do-evento-ViagemFinalizada-com-timezone",
  "CodigoLinha": "codigo-real",
  "Qualidade": "AuditadaSemProtecao",
  "ReferenciaAuditoria": "evidencia-real-da-execucao-completa-sem-protecao"
}
```

Qualidade sem evidência mantém viagem fora do manifesto elegível. Manter template/contexto oficial; arquivo vazio, cutoff divergente, viagem duplicada ou qualidade não auditada falham. Confirmar conexão de snapshot descartável em `ETA_ML_LOCAL_CONNECTION` no terminal; não gravar credenciais em JSON/Git/histórico. Não configurar túnel para produção. Os exemplos de cortes 18/21/24 de outubro são propostas futuras, não evidência de dados nessas datas; escolher limites antes de avaliar TEST e manter opções/treino alinhados.

```powershell
dotnet build tools/EtaMl.Export/EtaMl.Export.csproj --verbosity quiet
dotnet run --project tools/EtaMl.Export/EtaMl.Export.csproj --no-build -- tools/EtaMl.Export/outputs/audit.real.json tools/EtaMl.Export/options.example.json NoPonto/ETA_ML_CANDIDATOS_3A.sql tools/EtaMl.Export/outputs/dataset-real-v1
```

O diretório final deve ser novo. Falha/limite/paginação incompleta não é exportação aprovada. O ensaio conectado do adapter foi executado na 3B.1, com fronteiras/precisão de timestamps/joins/cutoff/paginação/descarte e comparação independente com SQL direto. Reproduzi-lo quando adapter/schema/contrato mudar e conferir o schema completo do snapshot real antes da extração. A fixture usa o slice relacional necessário ao exportador; não cobre o polling/materializador operacional completo nem a migração integral da API.

## Critérios iniciais objetivos de volume real

Gate de engenharia implementado por `check_volume.py` e aplicado automaticamente a treino `data_kind=real`:

- >=10.000 observações elegíveis depois dos descartes.
- >=14 dias UTC distintos com dados elegíveis, para não contar só muitas observações de poucos dias.
- >=200 viagens completas em TRAIN, >=50 em VALIDATION e >=50 em TEST, sem sobreposição.
- Pelo menos uma linha com >=30/10/10 viagens nos três splits e >=8 horas locais distintas em TRAIN. O relatório indica quais linhas atendem; linhas abaixo disso não sustentam conclusão individual.

Esses mínimos são critérios prospectivos do MVP, não estimativa estatística de suficiência universal. Procedência/fechamento/estrutura/causalidade e comparação temporal são pré-condições. Sampling de linhas por bloco pode exigir mais dias; contagem bruta de telemetria não equivale a volume elegível. Não aumentar sampling para cumprir o gate. Representatividade por modal/sentido/distância/horário, categorias desconhecidas, descartes e distribuição dos labels precisam de revisão do primeiro dataset real. Coverage de coleta exige denominadores externos não fornecidos pelo CSV.

```powershell
python tools/eta_ml/check_volume.py --dataset tools/EtaMl.Export/outputs/dataset-real-v1/dataset.csv --manifest tools/EtaMl.Export/outputs/dataset-real-v1/dataset.manifest.json --config tools/eta_ml/config.real.example.json --output tools/EtaMl.Export/outputs/dataset-real-v1/volume.json
python tools/eta_ml/train.py --dataset tools/EtaMl.Export/outputs/dataset-real-v1/dataset.csv --manifest tools/EtaMl.Export/outputs/dataset-real-v1/dataset.manifest.json --config tools/eta_ml/config.real.example.json --output tools/eta_ml/outputs/model-real-v1
```

`check_volume.py` retorna exit 2 enquanto insuficiente; nenhuma espera por volume bloqueia a validação sintética. Quando suficiente, treinar localmente e comparar baselines/modelo no mesmo TEST. Melhor desempenho não é garantido. A integração com serving/ETA público continua fora da 3B.

## Testes e pendências

Executados:

```powershell
dotnet build tools/EtaMl.Export/EtaMl.Export.csproj --no-restore --verbosity quiet
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~EtaDatasetTests' --verbosity quiet
dotnet test NoPonto/NoPonto.csproj --no-restore --filter '(FullyQualifiedName~EtaDatasetTests|FullyQualifiedName~TelemetriaMlIdentidadeTests|FullyQualifiedName~TelemetriaMlTests|FullyQualifiedName~EtaV2FoundationTests|FullyQualifiedName~IntegridadeCircularRegraTests)' --verbosity quiet
python -m unittest discover -s tools/eta_ml -p 'test_*.py' -v
```

Build final do console: zero erros/avisos. Dataset: 30/30; regressões ampliadas: 133/133, zero falhas/ignorados. Python: 9 testes pipeline + 6 guards do console sem banco; cutoff/purga integral, split errado, duplicação, allowlist sem label/proveniência, fallback/categorias novas, encoder só TRAIN, métricas conhecidas, hashes/reload/reprodutibilidade e gate insuficiente. Gerador/treino/evaluação CLI executados em fixture; avaliação recarregada idêntica.

Ocorrências intermediárias corrigidas: build concorrente da API/console disputou o mesmo DLL (reexecutado sequencialmente); comparação de matriz object com NaNs no teste precisou separar categorias/números; stderr UTF-8 do dotnet exigiu encoding explícito no harness Windows. Dependências transitivas EF alinhadas no console para eliminar conflitos 9.0.0/9.0.10. Não foram contornados testes de causalidade/split.

Na execução original 3B, o adapter conectado não havia sido executado. Essa pendência foi encerrada pelo ensaio 3B.1 abaixo. Permanecem sem execução: extração/treino/avaliação de dados reais, carga, migrations da API e produção/SSH. Para o primeiro modelo real faltam snapshot local auditável, manifesto elegível com viagens fechadas, conferência do schema real, volume mínimo e revisão da representatividade/descarte.

## Etapa 3B.1 — adapter conectado aprovado, 07/10/2026

### Ambiente e preservação

Preservadas todas as mudanças 3B existentes, inclusive os diffs de EtaDataset.cs/EtaDatasetTests.cs, scripts/modelo/baselines/features/config/artefato sintético. Sem operações Git destrutivas, staging, commit ou push. Lidos AGENTS/histórico/documento 3B, inspecionados status/diff e contratos/persistência/fixtures existentes antes das alterações.

Containers já existentes foram somente listados; nomes não demonstravam descartabilidade, portanto não usados. O runner criou exclusivamente containers `noponto-eta-3b1-<UUID>` com label `noponto.fixture=eta-3b1`, imagem local `postgis/postgis:16-3.4`, banco exclusivo `eta_fixture`, sem volume nomeado e publicação de porta dinâmica somente em `127.0.0.1`. Todos os containers/volumes anônimos criados pelo runner foram removidos em finally; os containers preexistentes permaneceram ativos.

Versões consultadas no banco do ensaio: PostgreSQL `16.4 (Debian 16.4-1.pgdg110+2)`, PostGIS `3.4.3`. O exportador abriu conexão Npgsql em loopback como role própria com apenas SELECT e executou `REPEATABLE READ`/`READ ONLY`. Preparação/mutações negativas usam somente o postgres interno desse container efêmero. Nenhuma conexão a banco preexistente, remoto, produção, SSH ou túnel.

Fixture mínima em `tools/eta_ml/postgis_fixture.sql`: tabelas/chaves/tipos/índices/constraints necessários ao SQL 3A, incluindo geometria LineString SRID4326, estrutura linha→sentido→padrão→versão→ocorrência→parada, telemetria, journal e histórico. Não é banco completo da API e não aplica migrations. Journal/histórico são semeados com payload schema2 compatível para testar o adapter; este ensaio não valida factory/polling/outbox/materializador operacional. Velocidades/matching/qualidade física dos registros não representam viagens reais; a procedência da fixture é conhecida por construção.

### Caminho e resultados

Executado o comando real `dotnet run --project tools/EtaMl.Export/EtaMl.Export.csproj --no-build -- <audit fixture> <options fixture> NoPonto/ETA_ML_CANDIDATOS_3A.sql <output novo>`, duas vezes na mesma fixture, seguido de validação independente e CLI `check_volume.py`. Não alterado SQL 3A nem reduzido seu tamanho de página para facilitar o teste.

| Item | Esperado | Produzido |
|---|---:|---:|
| Viagens no audit de entrada | 8 | 8 |
| Viagens distintas no CSV | 4 | 4 |
| Observações exportadas | 1004 | 1004 |
| TRAIN | 1002 observações / 2 viagens | 1002 / 2 |
| VALIDATION | 1 observação / 1 viagem | 1 / 1 |
| TEST | 1 observação / 1 viagem | 1 / 1 |
| Candidatos GPS descobertos nas viagens elegíveis | 1007 | 1007 |
| Candidatos entregues ao validador 3A | 1006 | 1006 |
| Arquivos concluídos | CSV + dataset manifest | Ambos gerados |

O campo `trips` do dataset manifest conserva o escopo auditado de entrada (8), inclusive viagens excluídas, como proveniência. Não interpretá-lo como quantidade exportada. As quatro viagens exportadas foram contadas independentemente do CSV e constam no relatório do ensaio. Nenhum ID das viagens excluídas apareceu no CSV. O loader calcula splits/viagens efetivamente presentes para treino/volume.

Descartes do cenário principal:

- 2 viagens fora da janela: uma começa exatamente 1 microssegundo antes do cutoff e possui GPS posterior; outra termina exatamente no limite final.
- 2 viagens cruzam cortes: TRAIN→VALIDATION e VALIDATION→TEST; ambas inteiramente purgadas.
- 1 candidato sem passagem/journal, contabilizado em discovery antes do validador.
- 1 candidato com identidade estrutural incompatível.
- 1 candidato circular cujo destino exigiria wrap (.95→.4), rejeitado sem inventar distância/label. Controle circular na mesma volta (.1→.4) exportado.

Uma viagem possui 1001 observações, com chave/timestamp que exigem mais de uma página real de 1000 GPS; uma segunda viagem concorrente tem GPS no mesmo timestamp da fronteira, com UUID posterior. Todos os IDs esperados foram recuperados uma vez, demonstrando keyset/paginação completa e filtro correto da execução. A alta frequência de GPS é proposital para testar paginação, sem simular cadência/sampling de produção.

Validados: labels positivos <=3600, fronteiras de execução, cortes temporais, cabeçalho/allowlist idênticos à 3B, ausência de label/proveniência na matriz mesmo após envenenar esses campos, hashes do CSV/audit/opções/SQL e unicidade de ObservacaoId no PostgreSQL. Constraint de duplicata foi exercitada com UUID novo e ObservacaoId existente; inserção rejeitada, sem criar linha extra.

Amostra comparada com SELECT independente no PostgreSQL (joins e `ST_LineSubstring::geography`, sem reutilizar o SQL de candidatos):

| Tipo | Split | Distância SQL e CSV | Label SQL e CSV |
|---|---|---:|---:|
| Linear, modal ONIBUS, posição .1→.4 | TRAIN | 615,5874145445704 m | 60 s |
| Circular fechada, modal BRT, mesma volta .1→.4 | TEST | 1280,0428469260182 m | 60 s |

São dados sintéticos armazenados em um PostgreSQL real descartável; não medidas de performance ETA. CSV e dataset manifest foram byte a byte idênticos nas duas exportações. SHA256 do CSV: `1ca69769d5d5f0eb790d96aacb59c8db85cbca040169408655bdc913c7691526`. SQL 3A preservado, SHA256 `b2c5f0909d817fc57ba3942c9b2985e77dcfbf9bbaf9a1644dd3228aa77898be`.

Negativos adicionais aprovados: viagens duplicadas, qualidade NaoVerificada, qualidade ProtegidaOuAmbigua, referência de auditoria vazia, build/cutoff divergentes, fronteira declarada diferente do journal e evento de fim realmente ausente. Todos impediram publicação de manifesto concluído; fim ausente também não deixou CSV parcial. Qualidade não auditada não é reclassificada: o manifesto de entrada inválido é recusado inteiro. Payload de passagem adulterado, divergente do histórico, descartou só o candidato correspondente (`PassagemSemJournalConferido`), produzindo 1003 observações no cenário negativo separado.

`check_volume`: exit2 esperado, `data_kind=synthetic`, `ready=false`, 1004 observações/3 dias UTC/2-1-1 viagens. Motivos: menos de 10000 observações, menos de 14 dias, menos de 200/50/50 viagens nos splits e nenhuma linha com mínimos30/10/10+8horas. Nenhum mínimo alterado para aprovar a fixture. Isso é resultado correto do gate, não reprovação do adapter. Não executado treino real nem geradas métricas de performance real nesta etapa; os testes Python existentes continuam exercitando apenas treinos sintéticos temporários.

### Falhas encontradas e correções

Primeira tentativa: conexão externa Npgsql terminou durante abertura do stream; não alcançou leitura de dataset. Harness passou a verificar resposta do protocolo PostgreSQL na porta publicada, além de pg_isready interno, e definir SSL desabilitado somente para esse container de teste sem TLS. Novas tentativas conectaram. Causa exata do encerramento transitório não foi demonstrada; não atribuída a defeito do SQL/adapter. Os containers próprios foram removidos inclusive nas falhas.

Segundo ensaio reproduziu defeito de metadados: o console sempre escrevia `data_kind=real`, embora o conteúdo da fixture fosse sintético. Correção localizada em `Program.cs`: `DataKind` opcional no audit, valores aceitos real/synthetic, valor declarado propagado ao dataset manifest; ausência mantém default real para compatibilidade dos manifests 3B. Template explicita real. O ensaio conectado exige synthetic e aprovou após a correção; guards novos rejeitam tipo inválido e comprovam compatibilidade do audit anterior. Tipo declarado não substitui auditoria/autenticidade. Ajustado encoding UTF-8 no harness ao ler JSON com motivos do gate.

Arquivos criados nesta etapa: `tools/eta_ml/integration_postgis.py`, `postgis_fixture.sql`, `examples/postgis-3b1.integration.report.json`. Alterados nesta etapa: `tools/EtaMl.Export/Program.cs`, `audit.example.json`, `tools/eta_ml/test_exporter.py`, este documento e HISTORICO.md. Saídas/logs/seed/audits/CSVs ficam em `tools/eta_ml/outputs/postgis-3b1-final`, ignoradas; relatório resumido sem credenciais preservado em examples. Modelo/baselines/contrato/features/SQL3A/sampling/ETA público intactos.

### Testes e reprodução

Build EtaMl.Export aprovado, zero erros/avisos; EtaDatasetTests30/30, zero falhas/ignorados,779ms; Python17/17 (9 pipeline + 8 guards),7,044s. Ensaio conectado final APROVADO, com duas exportações determinísticas e nove cenários negativos de auditoria/journal, mais unicidade no banco. `git diff --check` aprovado, somente normalização LF/CRLF. Não repetida a bateria133 nem integrações irrelevantes, pois não houve mudança correspondente na API/modelo.

```powershell
dotnet build tools/EtaMl.Export/EtaMl.Export.csproj --no-restore --verbosity quiet
python tools/eta_ml/integration_postgis.py --output tools/eta_ml/outputs/postgis-3b1-nova-execucao
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~EtaDatasetTests' --verbosity quiet
python -m unittest discover -s tools/eta_ml -p 'test_*.py' -v
git diff --check
```

Usar diretório novo. O runner exige Docker local e imagem `postgis/postgis:16-3.4` já disponível (`--pull never`); cria/remove apenas seu próprio container/volume anônimo em finally. Não aceita conexão externa fornecida pelo usuário e não utiliza ETA_ML_LOCAL_CONNECTION herdada: define a conexão exclusiva da fixture ao chamar o console. O guard loopback do exportador permanece intacto.

### AuditadaSemProtecao e snapshot real exploratório

**A — Existe no banco/journal:** telemetria com identidade operacional quando a factory a considera confiável; próxima ocorrência/versão/volta/GPS; passagens e journal schema2 compatíveis; início/fim com identidade/timestamps; motivo_fim PerdaContinuidadeCircular em recuperação que encerra execução ambígua; snapshot corrente de ViagensOperacionais com extensão IntegridadeCircular. Essas fontes permitem enumerar viagens fechadas, conferir associação/tempo/estrutura, rejeitar motivos/identidades incompatíveis e descobrir candidatos depois do cutoff.

**B — Não existe historicamente de forma completa:** trilha versionada de todas as entradas/saídas de proteção/ambiguidade por execução, todos os snapshots anteriores, prova de qualidade por observação/execução, distinção persistida completa da qualidade de interpolação/fallback e denominador integral de GPS antes de sampling/backpressure. IntegridadeCircular é substituída no UPSERT do estado corrente; entrada em proteção pode persistir sem evento. Journal de passagens não preenche esse intervalo. Build/cutoff declarados no audit não são certificação gravada individualmente nos GPS.

**C — Automatização segura:** é seguro automatizar inventário de fronteiras/candidatos, hashes, filtros negativos e contagens; isso não autoriza atribuir automaticamente AuditadaSemProtecao a todas as viagens. Ausência de marker atual ou de motivo de fim, presença de IDs e início após o cutoff, isoladamente, não provam o requisito de execução inteira sem proteção. Não implementado gerador que invente esse atestado nem nova arquitetura.

**D — Evidência/revisão necessária:** permanece necessária evidência externa confiável da execução completa e revisão dos critérios/versões da coleta, ou futura decisão explícita sobre evidência mínima auditável. Logs incompletos, flag ligada ou ausência de erro observado não bastam. Fixture tem lifecycle conhecido por construção; essa evidência não se transfere a viagens reais.

Estamos tecnicamente prontos para analisar **snapshot real exploratório local**, com SQL de descoberta/contagens e revisão de fronteiras/labels/schema. A exploração não certifica automaticamente dataset de treino. Para publicar exportação real pelo console, ainda é necessário manifesto com viagens fechadas e auditoria fundamentada; para treinar, também volume/representatividade suficientes e schema/geometrias/versões reais preservados. Custo/plano do adapter em snapshot grande, cobertura de coleta e validação física dos labels continuam pendentes. Não houve acesso a produção, treino real, deploy ou mudança operacional.
