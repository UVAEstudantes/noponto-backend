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

## 3B.2A — auditoria real pré-snapshot

**Status: preparada e revisada estaticamente; não executada em nenhum banco.** Checkpoint de entrada `749b882` (`feat(eta-ml): implementa pipeline offline e valida adapter PostGIS`), árvore inicialmente limpa. Esta etapa acrescenta `NoPonto/ETA_ML_AUDITORIA_REAL_3B_2.sql`, quatro guardas estáticas em `tools/eta_ml/test_audit_sql.py`, esta seção e entrada no histórico. SQL 3A, adapter, pipeline, runtime, dependências, infraestrutura e sampling preservados. Sem produção/SSH, snapshot, export, treino, commit ou push.

O arquivo é para **psql 16+ / PostgreSQL 16+ com PostGIS**, não para executar diretamente em um driver: usa metacomandos de psql para habilitar o bloco opcional. Envolve as consultas em `BEGIN READ ONLY; ... COMMIT;` e usa `SET TRANSACTION ISOLATION LEVEL REPEATABLE READ` somente para manter uma fotografia MVCC consistente. Não escreve tabelas, não usa temp tables, migrations, locks explícitos, funções de alteração de estado, ANALYZE, EXPLAIN ANALYZE ou cálculo geográfico. A coleta pode continuar; a fotografia reflete o início da transação, não eventos que chegarem depois. Relações são resolvidas no search_path do serviço: revisar A/H para confirmar o schema esperado antes de habilitar detalhes.

### Blocos e custo

| Bloco | Conteúdo | Execução/custo |
|---|---|---|
| A | Banco/schema, UTC da transação, PostgreSQL/PostGIS, contrato/cutoff, read_only/isolamento | Padrão, baixo custo esperado |
| B | Existência, `reltuples`, tamanho físico total das dez fontes, soma e índices reais | Padrão, catálogo; sem COUNT de tabelas grandes |
| H | Colunas/tipos reais e FKs das fontes | Padrão, catálogo; confirma dependências antes do snapshot |
| C | Total GPS pós-cutoff, min/max, dias UTC, ViagemId, versão/ocorrências/volta, identidade estrutural conferida; grupos modal/linha/dia/hora UTC | Opcional, scan global e joins estruturais |
| D | Viagens com início canônico válido >= cutoff; com fim real no journal, sem fim observado, fim inválido, passagens, candidatos; duração min/mediana/P90/max; grupos modal/linha/dia/motivo de fim | Opcional; percentis/agregações |
| E | Eventos por tipo, envelopes inválidos, histórico, viagens com passagem, início+fim, fechadas sem passagem, discrepâncias seguras | Opcional; lê journal/histórico |
| F | Uma linha JSON por viagem: fronteiras, contagens/identidades, pares potenciais e booleano `candidate_inventory` | Opcional; joins GPS/passagem por viagem/ocorrência/volta |
| G | Fim por `PerdaContinuidadeCircular`, divergências de fronteiras e markers correntes PascalCase | Opcional; somente evidências negativas |

Os blocos padrão usam catálogos/funções de tamanho, geralmente baratos; não existe garantia universal de latência. No detalhado, cada fonte é lida uma vez para CTE materializada e reutilizada na mesma consulta. Agregações são por viagem e joins conjuntos, sem rescans por viagem. Materialização, joins e ordenações ainda podem consumir memória e spill interno para disco; esse custo existe mesmo em READ ONLY. O relatório retorna todas as linhas na distribuição C com `rank_por_volume` dentro de cada grupo; para top linhas, filtrar `relatorio=C_telemetria_nao_e_dataset`, `grupo=linha` e `rank_por_volume <= 20` no resultado local (empates compartilham rank). A contagem diária é exata na fotografia, e não uma extrapolação de sampling.

Índices verificados no código: `EventosViagem.TimestampEvento`; histórico `TimestampGps`, `(ViagemId,TimestampPassagem)` e unicidade parcial `(ViagemId,OcorrenciaParadaPadraoId,Volta)`; GPS `(CodigoLinha,TimestampGps)`, `(OrdemVeiculo,TimestampGps)` e parcial `(ViagemId,TimestampGps)`. Não há índice isolado global de TimestampGps no GPS. Um filtro global de cutoff pode ler telemetria antiga inteira; não criar índice nesta etapa. Estado operacional não tem mais índice AtualizadoEmUtc. O bloco B lista o que de fato estiver instalado. O detalhado vem desligado porque pode ser caro no servidor de ~4 GB; executar apenas após revisar tamanhos, em período apropriado, com timeout/memória limitados. Timeout encerra a tentativa; com ON_ERROR_STOP a conexão fecha e a transação é revertida. Não insistir aumentando limites sem revisar custo. Não executar adapter/SQL 3A em produção para completar esta auditoria.

### Interpretação e limites

- `C` mede observações cujo **TimestampGps >= 2026-10-07T14:53:59.225082Z**. Inclui GPS de viagens iniciadas antes do cutoff; não representa dataset elegível. `com_viagem_id` mede não nulo, enquanto `identidade_estrutural_conferida` também rejeita UUID vazio, origem diferente de REAL, modal fora de ONIBUS/BRT, volta inválida e cadeia estrutural incompatível. IDs são auditoria, nunca features adicionadas ao modelo.
- `D/F` exigem início válido/canônico no journal pós-cutoff. Não usam último GPS para inventar fim. `sem_fim_observado` significa ausência de fim no journal nesta fotografia: pode haver viagem em andamento, journal atrasado/retido ou lacuna; não prova estado operacional aberto. Fim sem início pós-cutoff aparece em E/G, podendo corresponder a viagem antiga. Início malformado aparece nas contagens de envelopes/fronteiras inválidas, não é promovido a viagem válida. Durações só são calculadas com fronteiras únicas válidas, identidade concordante e fim >= início.
- F exige fronteiras únicas válidas, fechamento, identidade concordante e pelo menos um par GPS/passagem com identidade e tempos compatíveis dentro das fronteiras. Descarta automaticamente do inventário evidências de perda/marker negativo. Não aplica todos os filtros de `EtaDataset`: geometria/distância real, velocidades, label máximo, posições, todos os campos de passagem e cobertura completa ainda precisam do adapter no snapshot local. Um candidato pode depois exportar zero linhas. Nenhuma viagem parcialmente anterior ao cutoff é candidata.
- `candidate_inventory=true` é descoberta exploratória, **não AuditadaSemProtecao**. `false` e as contagens ajudam a localizar bloqueios; ausência de fim, passagem, identidade ou fronteira pode ser atraso/lacuna e pede investigação. Labels/voltas corretas, volume e representatividade não são certificados por estas contagens.
- Motivo de fim `PerdaContinuidadeCircular` e marker com Continuidade=1 (Ambigua), PerdaEm/Motivo preenchidos são evidências negativas para a viagem identificada. IntegridadeCircular é JSONB corrente, serializado PascalCase, Contrato=1; inclui registros fora da janela e o relatório mostra timestamps/contrato para revisão. O UPSERT substitui markers e não conserva toda a história; ausência de marker negativo jamais aprova execução inteira. Contratos/desvios inesperados exigem revisão. A consulta confere envelope/schema_version=2/canonical IDs e identidade das fronteiras, mas não é auditoria completa de cada payload/estado.
- E/histórico usa TimestampGps >= cutoff para incluir confirmação posterior de passagem interpolada; TimestampPassagem anterior ao cutoff não vira label candidato. Contagens globais E não são todas restritas às viagens candidatas. Comparações de timestamps do envelope são feitas na precisão PostgreSQL; casts de UUID/timestamp do payload são protegidos por `pg_input_is_valid`.
- Mesmo candidatos existentes só sustentam um **possível export exploratório local** após snapshot autorizado e revisão de evidências. Esta etapa não informa se já existe volume real suficiente. Para treino permanecem os critérios já definidos no gate: 10.000 observações, 14 dias UTC, 200/50/50 viagens train/validation/test, linha com 30/10/10 viagens e 8 horas, além de auditoria de qualidade, split temporal inteiro e representatividade. Esses números são aferidos no dataset exportado, não no total GPS ou neste inventário.

### Execução manual pelo operador

O agente não executou estes comandos. A partir da raiz do checkout no servidor, usando um **serviço libpq já configurado pelo operador** com banco/schema corretos e usuário com permissão de leitura (sem credenciais no comando), executar primeiro:

```sh
PGOPTIONS='-c statement_timeout=60000 -c lock_timeout=3000 -c work_mem=8MB -c timezone=UTC' psql -X --dbname='service=noponto_auditoria' -v ON_ERROR_STOP=1 -v detalhado=false -f NoPonto/ETA_ML_AUDITORIA_REAL_3B_2.sql -L eta-auditoria-3b2-catalogo.log
```

Depois da revisão dos tamanhos/schema/índices, o operador pode executar o opcional (mesmo limite de 60 segundos por statement, deliberadamente conservador):

```sh
PGOPTIONS='-c statement_timeout=60000 -c lock_timeout=3000 -c work_mem=8MB -c timezone=UTC' psql -X --dbname='service=noponto_auditoria' -v ON_ERROR_STOP=1 -v detalhado=true -f NoPonto/ETA_ML_AUDITORIA_REAL_3B_2.sql -L eta-auditoria-3b2-detalhado.log
```

Comandos em shell POSIX do servidor; não usar a sintaxe PGOPTIONS acima diretamente no PowerShell. `noponto_auditoria` é nome de serviço **a ser configurado fora do repositório**, não conexão fornecida/testada pelo agente. `-X` ignora psqlrc, pager desligado, `-L` escreve apenas resultado local do cliente. Proteger os logs: contêm IDs operacionais e nomes de banco/schema; não publicar conexão/credenciais nem despejar esses resultados no histórico. A saída A registra o instante efetivo para delimitar futuro snapshot; execuções padrão/detalhada distintas são fotografias diferentes.

### Recorte mínimo futuro (nenhum snapshot criado)

Preservar linhas completas das fontes selecionadas para manter compatibilidade com o `to_jsonb(t/h)` do SQL 3A; não transportar só a lista de features. Colunas e FKs efetivas saem em H. Lista funcional:

| Fonte | Colunas/relacionamentos essenciais | Recorte futuro |
|---|---|---|
| TelemetriasVeiculoMl | Id/ObservacaoId, ViagemId, TimestampGps, Modal/Provedor/OrdemVeiculo/CodigoLinha/OrigemPosicao, LinhaId/SentidoId/PadraoVersaoId, ocorrências/Volta; posições/GPS/distância/comprimento/velocidades e timestamps de proveniência | GPS >= cutoff até limite UTC registrado; preferir IDs de viagens com início >= cutoff, sem recortar viagem fechada ao meio |
| HistoricoPassagens | ViagemId, OcorrenciaParadaPadraoId/PadraoVersaoId/SentidoId/ParadaId/Volta, Ordem/CodigoLinha, TimestampPassagem/TimestampGps/TimestampRegistro, PosicaoNaRota/velocidades | Todas as passagens das viagens selecionadas, incluindo confirmações; não filtrar só TimestampPassagem e perder interpolação |
| EventosViagem | EventId/Tipo/Payload JSONB/TimestampEvento; IDs canônicos inicio/fim/passagem | Todos os eventos das mesmas viagens; preservar fronteiras e payload originais, não somente passagens ou janela arbitrária |
| ViagensOperacionais | OrdemVeiculo/Estado JSONB/Versao/AtualizadoEmUtc/IntegridadeCircular JSONB | Estado corrente de evidência, preferencialmente completo; não filtrar por AtualizadoEmUtc para concluir qualidade histórica |
| OcorrenciasParadasPadroes | Id/PadraoVersaoId/ParadaId/Ordem/PosicaoTracado | Todas as ocorrências das versões referenciadas, não apenas destino observado |
| PadroesVersoes | Id/PadraoOperacionalId/Geometria/Topologia/ComprimentoMetros e metadados/versionamento | Todas as versões referenciadas, inclusive inativas/antigas; sem filtro de cutoff/Ativo |
| PadroesOperacionais | Id/SentidoId/VersaoAtualId e metadados | Padrões referenciados e fechamento das versões atuais para restauração das FKs |
| Sentidos | Id/LinhaId e metadados | Sentidos referenciados, sem filtro temporal |
| Linhas | Id/Codigo/ModalId e metadados | Linhas referenciadas |
| Paradas | Id/Localizacao/ModalId/ParadaPaiId e metadados | Paradas de todas as ocorrências e pais necessários |

O intervalo exploratório é `[cutoff, instante UTC da auditoria]`, sujeito à confirmação do snapshot consistente futuro; para exporter escolher limite final explícito que preserve viagens completas e considerar sua regra `Fim < options.Fim`. Diário/primeiro/último GPS em C descrevem intervalo observado, não início de viagem ou atestado da coleta. Estrutura pode ser anterior ao cutoff e deve ser conservada. Para explorar inicialmente, carregar toda a estrutura pode ser mais simples e seguro que selecionar cadeias incompletas.

As dez fontes são o conjunto funcional do exportador mais evidência corrente. Um restore com **schema completo e FKs** também precisa dos pais que H apontar, especialmente Modais, FontesEstruturais (Sentidos) e pais recursivos de Paradas; relações legadas do histórico podem exigir Itinerarios/ParadasItinerario. Revisar fechamento transitivo de FKs reais no banco antes de transportar. Não incluir todas as tabelas de produção por suposição nem remover constraints nesta etapa. O contexto PostGIS/schema/migrations precisa acompanhar o snapshot futuro em manifesto.

B/H fornecem a estimativa de planejamento disponível sem scans extras: soma dos bytes físicos **completos das dez fontes**, incluindo índices/TOAST. É uma referência conservadora para essas fontes completas, não tamanho garantido de pg_dump comprimido nem do banco restaurado. O recorte de dados pode ser menor; dependências de FKs, índices recriados, geometrias/TOAST e compressão mudam o total. `reltuples` pode estar desatualizado. Não estimar um recorte por proporção global de GPS como se todas as linhas tivessem o mesmo tamanho; ainda falta definir IDs/limite e medir dump local para tamanho final. Nenhum número real foi inventado.

### Validação desta entrega

Executado `python -m unittest tools.eta_ml.test_audit_sql -v`: quatro guardas estáticas (envelope/allowlist de statements, fontes pesadas somente no opcional com uma leitura base por fonte, colunas/chaves JSON conferidas no schema/código, cutoff e SHA256 do SQL 3A preservado). Revisão manual de aliases, agregações, fronteiras, casts defensivos, custo e interpretação. `git diff --check` executado. Estes testes não são parser PostgreSQL nem prova de plano/latência; nenhuma consulta SQL foi executada, nem em fixture. Validação de execução/schema instalado/performance e resultados reais cabem à execução manual do operador. Suites de treino/adapter não repetidas porque não foram alterados; nenhum treino realizado.

### Correção estrutural 3B.2A — ocorrências e validação seletiva

**Atualização de operação:** o operador informou catálogo READ-ONLY executado com sucesso, ~19M GPS/~13 GB e proibição de scans globais. O agente não verificou produção. Os comandos anteriores de detalhado=true ficam como referência histórica e NÃO devem ser executados nesse banco. Usar somente o procedimento seletivo abaixo.

Equivalência confirmada no SQL: `o` é ligado à ProximaOcorrenciaParadaPadraoId, e exigir `t.OcorrenciaParadaPadraoId=o.Id` une os papéis. Não foi confirmado que isso rejeite linhas válidas produzidas pela factory atual. TelemetriaMl.cs/TelemetriaMlIdentidadeTests mostram que, havendo identidade confiável e próxima operacional, **ambos recebem o ID operacional**. OcorrenciaParadaPadraoId não é sempre o matching original. Sem próxima operacional, pode preservar fallback; ViagemId e Volta podem continuar preenchidos se a execução for confiável (ProximaOperacionalNula_CompativelMantemViagemEFallbackObservacional). Sem associação confiável, viagem/volta/próxima operacional ficam nulas e matching pode permanecer.

| Campo | Semântica persistida na factory |
|---|---|
| OcorrenciaParadaPadraoId | Próxima operacional quando disponível; senão fallback posicao.ProximaOcorrenciaParadaPadraoId; pode ser nulo |
| ProximaOcorrenciaParadaPadraoId | Apenas próxima operacional de execução confiável/compatível; sem fallback; pode ser nulo mesmo com viagem confiável |
| PadraoVersaoId | Versão do matching/DTO; associação exige compatibilidade com estado e próxima operacional, quando presente |
| LinhaId / SentidoId | IDs do matching/DTO, preservados mesmo sem associação; associação exige concordância e IDs não vazios |
| Volta | Volta do estado operacional confirmado; não inferida pela ocorrência observacional; auditoria exige >=0 |

Correção restrita à auditoria: `o` continua destino operacional obrigatório; novo LEFT JOIN `obs` confere OcorrenciaParadaPadraoId separadamente. Observacional ausente é permitido; presente exige ID não vazio, existência e mesma versão do destino operacional/matching. A cadeia linha→sentido→padrão→versão/parada continua exigindo IDs não vazios, concordância com GPS, REAL, ONIBUS/BRT, volta válida e topologia LINEAR/CIRCULAR. Não exigir igualdade entre IDs de ocorrências. Mesma versão implica mesmo padrão/sentido/linha. Fallback sem próxima operacional não vira candidato.

Revisados pares/inventario/candidate_inventory: `HistoricoPassagens.OcorrenciaParadaPadraoId = GPS.ProximaOcorrenciaParadaPadraoId` está correto: passagem pelo destino operacional, não campo observacional do GPS. Mantidas viagem/volta iguais; predicado corrigido é reutilizado sem outra equiparação. `com_versao_ocorrencias_volta` continua contador de presença de ambos os campos, não elegibilidade; identidade_estrutural permite observacional nulo, então esses contadores não são subconjuntos obrigatórios um do outro.

**Divergência reportada sem expandir escopo:** EtaDataset.Avaliar exige `g.OcorrenciaParadaPadraoId == destino.Id` além da próxima operacional. Isso é coerente com a factory atual quando há próxima; não demonstrado defeito do exportador. A auditoria solicitada fica mais ampla e pode descobrir candidato com observacional distinto/nulo que o adapter rejeitará por IdentidadeEstruturalIncompativel. A fixture PostGIS3B.1 e seu gerador usam ocorrências iguais nos positivos; não validam suporte a IDs distintos. SQL3A, EtaDataset, factory, fixture, runtime, sampling e dependências não alterados. IDs diferentes pós-cutoff pedem investigar procedência/versão da coleta, não aprovação automática.

Outras limitações: candidate_inventory não é AuditadaSemProtecao; conferência de journal é parcial; ausência de marker não aprova execução inteira. Não houve banco/SQL real/SSH/Docker/snapshot/treino/índice/commit/push. Teste estático acrescentado verifica ausência da igualdade direta/invertida e indireta via alias do destino, observacional opcional e mesma versão, ID não vazio e passagem ligada à próxima operacional.

#### Comando manual seletivo — uma viagem por execução

Na raiz do checkout, shell POSIX, Python abaixo **somente escreve arquivo SQL local** extraindo o predicado corrigido; não conecta. Usar UUID não vazio de viagem já identificado pelo operador; não descobrir IDs varrendo GPS. Fronteiras são dois lookups por PK EventId; início >= cutoff e fim posterior. GPS é filtrado por ViagemId específico e intervalo completo, aproveitando índice parcial (ViagemId,TimestampGps). Sem fronteiras válidas, zero registros; fim nunca inferido do GPS. Isso confere estrutura, não o atestado completo do journal/qualidade.

```sh
python3 - <<'PY'
from pathlib import Path
source = Path('NoPonto/ETA_ML_AUDITORIA_REAL_3B_2.sql').read_text(encoding='utf-8')
gps = source.split('gps AS MATERIALIZED (', 1)[1].split('\n),\njournal AS MATERIALIZED', 1)[0]
cutoff = '''WHERE t."TimestampGps">=TIMESTAMPTZ '2026-10-07T14:53:59.225082Z' '''.strip()
assert gps.count(cutoff) == 1
gps = gps.replace(cutoff, '''WHERE t."ViagemId"=:'viagem_id'::uuid
 AND t."TimestampGps">=(SELECT inicio FROM limites)
 AND t."TimestampGps"<=(SELECT fim FROM limites)''')
query = '''WITH limites AS MATERIALIZED (
 SELECT i."TimestampEvento" AS inicio, f."TimestampEvento" AS fim
 FROM "EventosViagem" i JOIN "EventosViagem" f
 ON f."EventId"='fim:'||(:'viagem_id'::uuid)::text
 WHERE i."EventId"='inicio:'||(:'viagem_id'::uuid)::text
 AND i."Tipo"='ViagemIniciada' AND f."Tipo"='ViagemFinalizada'
 AND i."Payload"->>'viagem_id'=(:'viagem_id'::uuid)::text
 AND f."Payload"->>'viagem_id'=(:'viagem_id'::uuid)::text
 AND i."TimestampEvento">=TIMESTAMPTZ '2026-10-07T14:53:59.225082Z'
 AND f."TimestampEvento">i."TimestampEvento"
), gps AS MATERIALIZED (''' + gps + ''')
SELECT "ViagemId", count(*) AS gps_da_viagem,
 count(*) FILTER (WHERE identidade_estrutural) AS identidade_corrigida,
 count(*) FILTER (WHERE identidade_estrutural AND "OcorrenciaParadaPadraoId"
   IS DISTINCT FROM "ProximaOcorrenciaParadaPadraoId") AS adicionais_vs_igualdade_antiga,
 count(*) FILTER (WHERE identidade_estrutural AND "OcorrenciaParadaPadraoId" IS NULL) AS observacional_nula,
 count(*) FILTER (WHERE identidade_estrutural AND "OcorrenciaParadaPadraoId" IS NOT NULL
   AND "OcorrenciaParadaPadraoId"<>"ProximaOcorrenciaParadaPadraoId") AS ocorrencias_distintas
FROM gps GROUP BY "ViagemId";'''
Path('eta-auditoria-seletiva.sql').write_text(
 'BEGIN READ ONLY;\nSET TRANSACTION ISOLATION LEVEL REPEATABLE READ;\n'
 + '\\if :executar\n' + query + '\n\\else\nEXPLAIN (COSTS ON) ' + query
 + '\n\\endif\nCOMMIT;\n', encoding='utf-8')
PY
```

Primeiro **somente plano, sem ANALYZE**; substituir UUID_DA_VIAGEM por UUID real conhecido, serviço libpq configurado pelo operador sem credenciais no repositório:

```sh
PGOPTIONS='-c statement_timeout=30000 -c lock_timeout=3000 -c work_mem=8MB -c timezone=UTC' psql -X --dbname='service=noponto_auditoria' -v ON_ERROR_STOP=1 -v viagem_id='UUID_DA_VIAGEM' -v executar=false -f eta-auditoria-seletiva.sql
```

Exigir Index Scan/Bitmap Index Scan de GPS com ViagemId/limites de TimestampGps; **não executar** se houver Seq Scan global de TelemetriasVeiculoMl. Scans de estruturas pequenas são distintos. Só após revisar plano, executar o mesmo comando com `-v executar=true`, mantendo UUID/timeout/arquivo. Não aumentar limites/insistir em plano global. Repetir para poucos IDs conhecidos pós-cutoff. O agente não aferiu plano; filtro seletivo não garante sozinho a escolha de índice.

`adicionais_vs_igualdade_antiga` mede efeito potencial da mudança. Zero é esperado na factory atual com próxima operacional; positivo exige investigação de procedência, sem provar erro/qualidade. Contagens dessa viagem não são labels/exportadas/volume de produção.

### Conclusão A — contrato de observação elegível confirmado

A ressalva anterior fica delimitada: inventário mais permissivo pode aceitar ocorrências distintas/nulas, mas **isso não demonstra defeito no exportador**. Contrato3A documenta igualdade dos dois campos GPS com o destino. Factory, quando existe alvo operacional, grava o mesmo ID nos dois campos e substitui matching/fallback; próxima operacional válida com campo geral diferente não é saída possível desse código. OcorrenciaParadaPadraoId não significa parada atual. Viagem confiável com próxima nula mantém contexto, mas esse GPS não tem label elegível.

Fluxo: factory/registro GPS → SQL3A destino por ProximaOcorrenciaParadaPadraoId → histórico da mesma viagem/volta e ocorrência do alvo → journal conferido → EtaDataset exige ambos campos GPS e ocorrência da passagem iguais ao destino, estrutura/tempos/distância válidos e alvo à frente → label TimestampPassagem−TimestampGps. SQL3A não possui a igualdade entre os dois campos GPS; essa checagem ocorre no EtaDataset. Rastreio detalhado/cobertura registrado ao final de docs/ETA_ML_DATASET_3A.md.

A auditoria recém-separada continua consistente somente como descoberta mais ampla, sem certificação de elegibilidade3A. Obs distinto/nulo com próxima válida é anomalia de procedência/contrato da factory, não automaticamente exemplo aproveitável. Nenhuma mudança no SQL da auditoria, SQL3A, EtaDataset ou runtime nesta análise. Acrescentadas apenas regressões em EtaDatasetTests e documentação/histórico. Próximo passo é operador revisar plano e contagem seletiva de poucos IDs conhecidos, sem scan global; se houver diferença de IDs, investigar escritor/build/dados antes de qualquer relaxamento. Sem testes PostgreSQL/Docker/SSH ou acesso real.

### Anomalia inicial N → primeiro journal N+1/N+2 — investigação local

**Conclusão delimitada:** C é um mecanismo compatível reproduzido localmente (alvo publicado antes de uma adoção/cancelamento sem retroativos); não está provado que explique as oito viagens reais. Não há evidência suficiente para classificar definitivamente cada caso como A/B/C. Contagens21.111/21.089/22,21GPS em8viagens e1fora1h foram fornecidas pelo operador, não verificadas pelo agente. Bootstrap A isolado não explica a perda de um alvo que estava à frente numa observação operacional válida antes da primeira passagem. Não demonstrado bug B de emissão somente da última parada.

Fluxo conferido: GPS aceito pelo cache/mapa → matching/projeção e ViagemObservadaService → ViagemOperacionalRepository calcula baseline/transição → Decidir cria/adota/avança estado e lista eventos → estado/outbox na mesma transação → projeção Redis pós-commit → resultado com transition.Proxima → factoryML após aguardar AtualizarAsync. ViagemOutboxWorker materializa EventosViagem e HistoricoPassagens/ACK na transação; somente PassagemParada gera histórico. Ausência em ambos é compatível com evento não emitido, mas não prova que chegou ao outbox nem exclui retenção/pendência. O retry não republica ML e pode alterar a sequência operacional entre observações amostradas; matching ausente/falhas/backlog não autorizam associação ML.

**Criação:** anterior null usa baseline na posição atual (PosicaoInicialTransicao). SQL de OcorrenciaParadaRepository incorpora todas as posições <= início, cursor=max(ordem), próxima=primeira ordem acima do cursor, Ultrapassadas vazio. Decidir grava cursor e emite somente ViagemIniciada. Nascer entre4/5 começa com cursor4 e próxima5; não emite4 retroativamente nem publica4 como próxima nesse bootstrap normal. Antes da primeira:cursor0/próxima1; após várias:cursor da última incorporada. Ordens podem ter lacunas e posições coincidentes, pois validação não exige ordens contíguas nem posições estritamente crescentes; comparar diferença numérica de ordens não prova quantas ocorrências existiam entre elas.

**Avanço normal:** incorporadas exige ordem>cursor e posição>posição anterior, <=posição atual. Decidir percorre TODAS Ultrapassadas em ordem (ou ordem dirigida no wrap), emite um evento canônico por ocorrência/volta com interpolação quando admissível. Saltos de1/2 ou mais não descartam as primeiras no caminho normal. Cursor avança para última, próxima é calculada DEPOIS do avanço. Primeira atualização normal após criação segue essa mesma regra. Um GPS anterior ao alvo, seguido de cruzamento normal, deve produzir passagem daquele alvo.

**Caminho C reproduzido:** t0 nova viagem confiável antes de N, ML publica N; t1 matching aponta para outra operação e abre candidato (não há passagem, timestamp avança sem substituir posição confirmada); t2 retorna à operação original e cancela candidato; com flag MudancaOperacionalHabilitada a consulta é baseline na posição adotada e Decidir recebe adocaoLegado=true, sem eventos retroativos; mesmo ViagemId/Volta, cursor incorpora N ou N e a seguinte; t3 cruza a próxima ainda futura, primeiro journal N+1 ou N+2. Testes usam ordens1→2 e1→3 como equivalentes de N genérico. Sem flag/contexto/candidato correspondentes, esse caminho não está demonstrado. Mudança confirmada cria nova viagem/baseline; circular protegida tem prova/ancora/volta e possível nova viagem com motivo PerdaContinuidadeCircular. Não confundir isso com cancelamento linear na mesma execução.

**Sem janela de próxima antiga comprovada na factory:** polling aguarda decisão e recebe transition.Proxima da transição atual, não lê a próxima do matching anterior como operacional. No teste C, ML ANTES do candidato contém N; ML DEPOIS da adoção já contém próximo alvo novo, nunca N antigo. Identidade transitória é contexto válido no instante t0 que perde futura label após rebaseline, não prova de corrida nem observação já atrás do cursor naquele mesmo instante. Nenhuma garantia de passagem futura é feita pela factory. Anular todo alvo inicial não é justificado: retiraria observações úteis de bootstrap normal, sem resolver adoções posteriores; não alterado. O relato “imediatamente anterior” pode excluir GPS intermediários por sampling/falha/retry; é preciso distinguir último GPS coletado de última atualização operacional.

**Cobertura e limites:** testes existentes Baseline_CriaAtiva_EmiteSomenteInicio/Finalizada_*SemRetroativos, interpolação múltipla, Cancelamento_ConsultaBaselineNaPosicaoAdotada (.25/.39/.55/.65), flag false, circularbaseline/volta, mudança e integridade cobrem partes do desenho. Acrescentados5casos em ViagemOperacionalRegraTests: bootstrap antes/entre/depois de várias paradas e primeira atualização normal cruzando1/2, emitindo todas. Acrescentados2casos em CancelamentoCandidatoCursorTests ligam factory antes/depois, candidato/cancelamento, posição baseline/cursor e primeiro journal+1/+2. Transições baseline são modelo lógico do retorno SQL, não PostgreSQL executado; testes não provam como a produção calculou a transição real nem materializam journal. Integrações OcorrenciaParadaTests/MudancaOperacionalPontaAPontaTests/fixture circular/PostGIS foram inspecionadas, não executadas.

**Impacto:** dataset deve descartar observações sem passagem correspondente e horizonte>1h, sem fabricar label/relaxar3A. Estado do app/ETA também usa contexto operacional e pode ter histórico de chegadas incompleto em intervalos adotados; não afirmar efeito exclusivamenteML. Mapa continua aceito, cursor pós-adoção se alinha à posição atual e alvo futuro pode ser válido; ausência de erro no app/ETA público não foi verificada. Números restritos ao inventário fornecido não são coverage geral nem desempenho. Sem mudança operacional/ML/sampling/SQL3A/EtaDataset/auditoriaSQL/infra.

**O que falta para concluir as8:** UUIDs sanitizados/volta/versão/topologia, payload do início/cursor, posições físicas antes/depois do alvo e timestamps dos GPS, flags efetivas e evidência de candidato/cancelamento/proteção/retry/outbox no intervalo. Estado corrente substitui anteriores, e journal não registra cada candidato/cancelamento; dados amostrados não reconstituem o caminho completo. Se continuidade linear sem candidato/adoção/proteção for demonstrada e houver cruzamento físico válido de N, a hipótese B merece teste seletivo adicional. Não presumir que perdas iniciais são bootstrap seguro nem condenar materializador sem evidência.

#### Consulta adicional manual mínima (não executada)

Executar apenas para um UUID/ocorrência/volta JÁ conhecidos entre os8, com serviço libpq configurado pelo operador, timeout30s. O início canônico deve ser >= cutoff. Primeiro revisar EXPLAIN sem ANALYZE da consulta GPS e exigir índice parcial (ViagemId,TimestampGps), não Seq Scan global; nas demais usar PK EventId/Id e índice histórico (ViagemId,TimestampPassagem). Não executar se plano for global. Exemplo em shell POSIX, trocar os três placeholders; retorno é janela inicial de10min/até50GPS, não reconstrução completa de todos os GPS:

```sh
PGOPTIONS='-c statement_timeout=30000 -c lock_timeout=3000 -c work_mem=8MB -c timezone=UTC' psql -X --dbname='service=noponto_auditoria' -v ON_ERROR_STOP=1 -v viagem='UUID_DA_VIAGEM' -v alvo='UUID_DA_OCORRENCIA' -v volta='0' <<'SQL'
BEGIN READ ONLY;
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
SELECT "EventId","Tipo","TimestampEvento","Payload" FROM "EventosViagem"
WHERE "EventId" IN ('inicio:'||(:'viagem'::uuid)::text,
 'passagem:'||(:'viagem'::uuid)::text||':'||(:'alvo'::uuid)::text||':'||(:'volta'::int)::text);
SELECT "Id","Ordem","PosicaoTracado","PadraoVersaoId" FROM "OcorrenciasParadasPadroes"
WHERE "Id"=:'alvo'::uuid;
-- Primeiro somente plano, sem ANALYZE; retirar EXPLAIN apenas apos revisar indice.
EXPLAIN (COSTS ON)
SELECT t."TimestampGps",t."PosicaoNaRota",t."PadraoVersaoId",t."Volta",
 t."OcorrenciaParadaPadraoId",t."ProximaOcorrenciaParadaPadraoId"
FROM "TelemetriasVeiculoMl" t
WHERE t."ViagemId"=:'viagem'::uuid
 AND t."TimestampGps">=(SELECT "TimestampEvento" FROM "EventosViagem"
  WHERE "EventId"='inicio:'||(:'viagem'::uuid)::text AND "Tipo"='ViagemIniciada'
  AND "Payload"->>'viagem_id'=(:'viagem'::uuid)::text
  AND "TimestampEvento">=TIMESTAMPTZ '2026-10-07T14:53:59.225082Z')
 AND t."TimestampGps"<=(SELECT "TimestampEvento"+interval '10 minutes' FROM "EventosViagem"
  WHERE "EventId"='inicio:'||(:'viagem'::uuid)::text)
ORDER BY t."TimestampGps" LIMIT 50;
SELECT "TimestampPassagem","TimestampGps","Volta","OcorrenciaParadaPadraoId","PadraoVersaoId"
FROM "HistoricoPassagens" WHERE "ViagemId"=:'viagem'::uuid
 AND "Volta"=:'volta'::int ORDER BY "TimestampPassagem" LIMIT 5;
COMMIT;
SQL
```

Não colocar IDs/resultados reais/credenciais no histórico. Essa consulta ajuda a comparar cursor inicial/posição alvo/GPS e primeiras passagens; não prova ausência histórica de candidato. Complementar apenas com logs já existentes, seletivos por veículo/intervalo, fornecidos pelo operador e sanitizados. Não conectar ou criar nova instrumentação automaticamente. Próximo passo3B.2A: separar essas21observações como sem label; preservar restante inventário sem certificação positiva; investigar poucos exemplos N+1/N+2 antes de declarar causa das8 ou aprovar snapshot/treino.

### Novas posições reais — baseline de renascimento distinto do bootstrap null

Novos casos físicos fornecidos pelo operador: início15:51:41/posição.2132447277/nextordem4(.1288164736), já apósordem5(.1787908964); primeiro evento6(.2561770242). Outro caso posição.0663955227/next2(.0639872683), próximo2 mantido até.1308317048; primeiro evento3(.1320921575) interpolado18:14:56.442 com GPSconfirmador18:15:27. Agente não verificou banco/produção.

**Retificação da abrangência anterior:** bootstrap com anterior=null realmente incorpora <= posição atual, sem margem, e não produz esses alvos atrás. Porém ViagemIniciada NÃO implica anterior=null. DecidirAposFinalizada confirma uma nova execução a partir de candidato com movimento; o repositório pode retornar Updated (durável já existia), apesar de novo ViagemId/início. No helper PosicaoInicialTransicao, baseline=true por previous.Estado=Finalizada NÃO basta para usar atual: se versão igual e flagMudancaOperacionalHabilitada=false, retorna previous.Observada.PosicaoNaRotaConfirmada. SQL baseline usa PosicaoTracado<=@anterior, não @atual. DecidirAposFinalizada grava posição/timestamp ATUAIS no novo estado mas copia cursor da transição anterior; retorno Proxima também vem dessa transição. Factory grava posição ATUAL e próxima recebida, sem verificar que alvo esteja à frente.

**Demonstrado localmente, sem prova de origem dos8:** snapshots finalizados sintéticos com mesma versão e posição anterior.10/.02, candidato de reinício válido, flagfalse e GPSatual.2132447277/.0663955227. Baseline incorpora atéordem3/1; retorna próxima4/2; novoViagemId e TimestampEvento=TimestampGps atual, ML publica posição atual com alvo atrás. Atualização normal seguinte usa posição atual recém-gravada como limite inferior:4/5 ou2 já estão atrás e nunca cruzam novamente; como próxima SQL é por ordem>cursor, o alvo antigo permanece até cruzar6/3, que vira primeiro evento, exatamente o padrão numérico fornecido. Com flagtrue+candidato, helper usa atual e elimina esse desalinhamento específico. Não inferir flagtrue/false da configuração de sampling.

Nova teoria RenascimentoMesmaVersao_BaselineAnteriorPodePublicarAlvoJaAtras (2casos) chama helper real, decisão real e factory real, modelando retornoSQL sem banco. Snapshot finalizado foi construído como entrada sintética; teste NÃO comprova que uma sequência íntegra de fim terminal criou essa posição anterior baixa, nem valida alcance desses snapshots em produção. Uma viagem linear finalizada normalmente deve ter alcançado o terminal; por isso explicar a origem/posição do estado anterior também é necessário. A reprodução estabelece inconsistência na fronteira helper→novaexecução quando recebe tal entrada, não certifica causalidade real.

**Posições operacional/ML:** no mesmo ciclo, projeção operacional pode substituir posição em gpsOperacional quando matching diverge de versão da execução anterior ativa/PossivelFim. Factory continua recebendo DTO observacional original, mas recusa identidade quando versão operacional difere; logo esse caminho não explica automaticamente próximos operacionais preenchidos na mesma versão. No renascimento acima, não precisa haver diferença DTOoperacional/DTOobservacional: ambos têm atual; a diferença é @anterior usado como corte baseline. Sem normalização dirigida/margem no baseline; circular só altera wrap e retorno da primeira ocorrência após última, não explica alvo4 quando ainda existem ordens maiores. Monotonicidade estrutural permite posições iguais/lacunas de ordem, mas não corrige posições concretas atrás apresentadas.

Polling aguarda AtualizarAsync antes da factory e persiste ambos campos diretamente. TimestampEvento início vem do timestampGPS, não relógio da execução. Igualdade desses instantes é coerente com a mesma transição de renascimento (testado) mas sozinha não prova um único ciclo/escritor: retry conserva timestamp físico, pode reenriquecer, não republica ML; reprocessamento igual/antigo é rejeitado pela autoridade e factory não associa status rejeitado. ObservacaoId identifica modal/provedor/veículo/timestamp, e não contém ViagemId; outro escritor/build/dado fora do contrato exige evidência, não deve ser presumido. Nenhum replay antigo legítimo que misture novo ViagemId/estado com positionposterior foi demonstrado.

**Hipóteses restantes:** (1) renascimento com baseline anterior, condicionado à flag/estado anterior/versão; (2) estrutura/escritor/build/associação diferente do código e sequência considerados. Criação realmente null na estrutura fornecida viola a invariante alvoadiante (caso1 próxima6; caso2 próxima3). CancelamentoC demonstrado anteriormente não explica sozinho alvoatrás já no GPS de início; não mantê-lo como atribuição desses casos. Veredito: defeito local demonstrado no corte baseline de renascimento (B condicional), causa real ainda não fechada. Não há justificativa para classificar o alvoatrás como estado transitório confiável aceitável.

**Consulta mínima seguinte (manual, não executada):** obter payload bruto de início pela PK e comparar ocorrência/cursor/volta/versão com estrutura; recuperar próximos fins de mesmo veículo apenas em janela temporal indexada. Sem scanGPS global. Serviço libpq previamente configurado, UUID conhecido, shellPOSIX:

```sh
PGOPTIONS='-c statement_timeout=30000 -c lock_timeout=3000 -c timezone=UTC' psql -X --dbname='service=noponto_auditoria' -v ON_ERROR_STOP=1 -v viagem='UUID_DA_VIAGEM' <<'SQL'
BEGIN READ ONLY;
SELECT "EventId","TimestampEvento","Tipo","Payload"
FROM "EventosViagem" WHERE "EventId"='inicio:'||(:'viagem'::uuid)::text;
WITH inicio AS MATERIALIZED (
 SELECT "TimestampEvento" AS ts,"Payload"->>'ordem_veiculo' AS veiculo
 FROM "EventosViagem" WHERE "EventId"='inicio:'||(:'viagem'::uuid)::text
 AND "Tipo"='ViagemIniciada'
 AND "TimestampEvento">=TIMESTAMPTZ '2026-10-07T14:53:59.225082Z'
)
SELECT e."EventId",e."TimestampEvento",e."Payload"
FROM "EventosViagem" e JOIN inicio i ON e."TimestampEvento">=i.ts-interval '5 minutes'
 AND e."TimestampEvento"<=i.ts
WHERE e."Tipo"='ViagemFinalizada' AND e."Payload"->>'ordem_veiculo'=i.veiculo
ORDER BY e."TimestampEvento" DESC LIMIT 5;
COMMIT;
SQL
```

A segunda consulta usa índice TimestampEvento e filtra JSON só dentro5min; operador deve revisar EXPLAIN sem ANALYZE, não executar se plano global. Ausência de fim nessa janela não elimina renascimento (fim pode ser antigo); não ampliar janela global por hipótese. Se cursor início for3/1, reforça baseline atrás; se5/2 mas próximo4/2 persistido, precisa investigar associação/estrutura/escritor. Conferir flags/build efetivos e, se disponíveis, logs/estado anterior sanitizados; journal início não persiste posição e registro corrente sobrescreve predecessores. Consultas anteriores já fornecem GPS/alvo; não repetir scanGPS.

**Ação:** não alterar runtime agora nem encaixar dados no contrato. Menor correção candidata, após revisão de causalidade/invariantes, é usar posição atual quando baseline=true e anterior.Estado=Finalizada, preservando política de cancelamento/recovery/wrap. Não implementada nem implantada. Labels ausentes são descartadas, e EtaDataset também rejeita destino atrás; isso protege esses exemplos de treino mas não prova cobertura/ETAoperacional corretos. Alvo inicial inválido pode afetar consumidores, exigindo revisão funcional antes de declarar problema sóML. Pode continuar contagens seletivas3B.2A mantendo esses casos excluídos; não encerrar causa comoC/bootstrapping esperado, nem liberar3B.2B/snapshot/treino por esse diagnóstico sozinho.

### Correção confirmada — renascimento após Finalizada usa corte atual

**Veredito B: bug de renascimento confirmado pela cadeia do código e pelas evidências seletivas fornecidas pelo operador.** Agente não acessou produção. A confirmação é do caso N+2 documentado; não significa reconstituição independente dos8casos nem prova adicional da origem do N+1. Mudanças/análises anteriores preservadas como histórico.

A regra primeiro constrói `atual` com PosicaoNaRotaConfirmada=p, conservando cursor conforme transição. Em PossivelFim, `permaneceTerminal` verifica cursor terminal/próxima nula/nenhuma ultrapassada, não que p esteja fisicamente no terminal. Ao segundo confirmador, muda Estado para Finalizada e emite fim a partir DESSE atual. Assim GPS.10942379055055879 no ciclo15:48:41 é gravado junto de cursor terminal26. SQL linear conserva cursor nas regressões, sem inventar wrap; essa combinação é alcançável pela máquina de estados e foi exercitada no teste. Não alterada política de finalização nesta correção focalizada.

Persistência segue GravarEstadoAsync/codec→Estado JSONB e outbox na mesma transação; projeção Redis depois do commit. O estado finalizado lido conserva.10942379. DecidirAposFinalizada constrói semInicio alterando somente TimestampUltimaAtualizacao; uma observação em.20535911939693594 pode criar/atualizar candidato ou mantê-lo, SEM substituir PosicaoNaRotaConfirmada. No nascimento.2132447277027137, candidato fornece evidência de movimento (janela180s), não o corte baseline. Versão igual/flagfalse fazia helper retornar.10942379: incorpora3(.07672496), ainda antes4(.12881647), próxima4. Novo estado iniciava fisicamente após5, mas cursor3/próxima4; atualizações normais não voltam a cruzar4/5 e primeira passagem futura é6. Isso fecha a lacuna do snapshot anteriormente apenas sintético.

A igualdade de timestamp início/GPS é esperada: ambos usam instante físico da observação confirmadora; início novo pode retornar Updated por haver estado durável anterior. Factory recusava associação na finalização (faseFinalizada) e enquanto não havia novo estadoAtiva, preservando fallback, compatível com GPS semViagemId/Volta/próxima em15:48:41 e15:51:11. No renascimento, ambos campos de ocorrência recebem próxima calculada; o erro era no corte espacial, não no mapping SQL3A/factory/materializador.

**Menor alteração efetiva:** PosicaoInicialTransicao acrescenta somente `baseline && anterior.Estado == EstadoViagem.Finalizada` às condições que retornam posição atual. Não muda fluxo não-baseline nem adoção/cancelamento de estado ativo. Null e versão diferente já usam atual; flagtrue+candidato já usa atual; flagfalse passa a corrigir renascimento. Mudança confirmada calcula baseline p,p antes de Decidir(null); recovery/proteção circular de execução ativa mantém política própria. Circular naturalmente não encerra por terminal; caso finalizado reiniciado deve ter baseline da nova execução, com volta resetada pelo código já existente. Nenhum wrap artificial/passagem retroativa adicionado. Retry chama mesmo repository/helper, sem contrato novo. Não mudar sampling, cutoff, SQL3A, EtaDataset ou telemetria histórica.

Regressão anterior ajustada para esperar comportamento corrigido, agora4casos (N+2/N+1 × flagfalse/true). Constrói execução ativa, passagem terminal, primeiro confirmador e finalização com GPS na posição inicial; exige EstadoFinalizada/posição nova/cursor26. Depois candidato não altera posição finalizada; baseline usa GPSatual; cursor5/2 e próxima6/3; novoViagemId/volta0/únicoViagemIniciada, MLalvoàfrente, nenhuma passagem retroativa e primeira passagem futura normal. SQL/transição de ocorrências permanecem modelados localmente; PostgreSQL não executado. RED antes da correção:2false falharam,2true passaram. Sem flags de teste/coleta relaxadas.

Os21GPS previamente sem label continuam sem label: não reconstruir4/5 nem mudar histórico para completardataset. Bug operacional pode causar alvoatrás para consumidores; filtros de labels ausentes/destinoatrás protegem exportação desses casos, não provam funcionamento do ETApúblico. Correção é prospectiva e não foi implantada. Falhas/gaps/sampling e evidência de execução inteira continuam critérios independentes.

**Riscos/validação pendente:** teste puro cobre encadeamento/regra/helper/factory, não matching real/SQL/PostGIS/cache/outbox conectado. Política de finalização com cursor terminal e posição regredida permanece, pois é fora da correção mínima; merece observação específica futura, sem modificar automaticamente. Configuração/build de produção não verificados pelo agente. Não atribuir automaticamente mesma causa às8viagens. Revisar diff/testes antes de qualquer deploy autorizado; depois validar coleta seletiva pós-deploy para ausência de alvoatrás em novos renascimentos. Pode encerrar esta subinvestigação causal do N+2 e voltar à3B.2A/preparação3B.2B, mantendo exclusões/auditoria/volume e autorização de snapshot separadas.

Integração futura, SOMENTE ambiente local descartável configurado pelo operador via POSTGIS_TEST_CONNECTION/REDIS_TEST_CONNECTION (fixtures escrevem/removem dados próprios), não executada nesta rodada:

```powershell
dotnet test NoPonto/NoPonto.csproj --no-restore --filter '(FullyQualifiedName~ViagemOperacionalIntegracaoTests|FullyQualifiedName~MudancaOperacionalPontaAPontaTests|FullyQualifiedName~IntegridadeCircularPostgresTests)' --verbosity normal
```

Não executar esse comando contra banco real/produção ou serviços preexistentes sem isolamento confirmado. Nenhum teste longo/integração externa foi iniciado pelo agente.

Validação desta correção: RED2falhas(flagfalse)/2aprovados(flagtrue) antes da alteração; GREEN184/184 testes locais relacionados após correção,0falhas/ignorados,6s; Pythonguardas auditoria6/6,1,296s; git diff --check aprovado. Cinco avisos preexistentes na recompilação, sem novos avisos. Nenhum teste conectado executado. Correção pronta para revisão local, sem deploy/commit/push, com mudanças anteriores preservadas.

### Fechamento — integração PostgreSQL/Redis validada pelo operador

**Atualização posterior à auditoria local:** o operador informou execução MANUAL em ambiente descartável e isolado, sem banco de produção: PostgreSQL 16/PostGIS 3.4 em loopback porta 55439 e Redis 7 em loopback porta 6397, configurados via POSTGIS_TEST_CONNECTION/REDIS_TEST_CONNECTION. O agente não acessou esses serviços nem repetiu os testes. O comando de integração documentado acima foi executado com o mesmo filtro de ViagemOperacionalIntegracaoTests, MudancaOperacionalPontaAPontaTests e IntegridadeCircularPostgresTests, usando --no-restore e --verbosity normal.

Build com sucesso; **75 testes executados, 75 aprovados, 0 falhas e 0 ignorados**. Duração dos testes ~129,5s; execução total ~139,4s. A tentativa anterior falhou somente porque os containers descartáveis estavam desligados e nenhuma conexão era aceita na porta 55439; após o operador reiniciá-los, a mesma suíte passou integralmente, sem mudança de código.

Isso fecha a principal ressalva da auditoria anterior: a integração PostgreSQL/Redis está validada no escopo dessa suíte e desse ambiente, conforme resultado fornecido pelo operador. As afirmações anteriores de integração pendente referem-se às rodadas anteriores. A correção permanece prospectiva e não implantada; **validação em produção ainda depende de observação seletiva pós-deploy**, após implantação separadamente autorizada. A política de finalização com cursor terminal/posição regredida permanece fora desta correção; não há reconstrução dos 21 GPS sem passagem, atribuição automática da causa às oito viagens ou certificação do dataset real.

Este fechamento altera somente documentação/histórico, sem credenciais, connection strings completas ou UUIDs reais adicionais. Runtime, testes, SQL, migrations, sampling, infraestrutura e dataset preservados; nenhum commit, push ou deploy realizado.

### 3B.2B — preparação do snapshot seletivo pós-fix

Operador informou encerramento da auditoria 3B.2A e validação do fix em produção. Novo marco oficial: cutoff2026-10-07T21:00:57.997530Z, backend70d7bcb8041e167df063237b4fdcafd4971b01d6, imagesha256:42d8b78b7b683234211f0d10edf8a79ddcf5c7f6557df9fd065f2409299f5094, migration20261006180000_IntegridadeCircularDuravel, contrato noponto-eta-gps-v1 e sampling10%/60min/NOPONTO_ML_V1 habilitado. Agente não verificou produção. Os registros anteriores permanecem históricos.

Procedimento, arquitetura, comandos manuais e limites em [SNAPSHOT_3B_2B.md](../../tools/eta_ml/SNAPSHOT_3B_2B.md). Nova ferramenta stdlib tools/eta_ml/snapshot.py prepara bundle offline, exige viagens fechadas explicitamente auditadas pós-cutoff, gera exportação READ ONLY seletiva e manifesto/hashes, verifica arquivos e restaura somente em loopback/banco dedicado vazio. Estrutura completa inclui Modais/FontesEstruturais e preserva ciclos por pre-data/dados/post-data, com validação das FKs originais. Não executada exportação/restore/produção/Docker/SSH/treino.

Bloqueio explícito: EtaMl.Export/Program.cs e pipeline.py ainda fixam o perfil/cutoff anterior e recusam metadados pós-fix corretos. Preservados nesta tarefa; não relaxar proteção local/READ ONLY nem informar release antigo para contornar. Atualização compatível dos perfis oficiais e regressões é necessária antes do primeiro CSV/treino real pós-fix. Snapshot seletivo pode ser preparado/restaurado independentemente, sujeito à revisão de planos/tamanhos e validação conectada pelo operador.

### Perfis de coleta oficiais compartilhados — bloqueio pós-fix resolvido

Catálogo único em tools/collection_profiles.json: historical-pre-fix (cutoff14:53:59/commit3e40d923/image2bbb6011) e official-post-fix (cutoff21:00:57/commit70d7bcb/image42d8b78b), cada um com migration, contrato e sampling completos. EtaMl.Export incorpora esse JSON como recurso no build, sem caminho configurável pelo usuário; Python o lê a partir do módulo, independentemente do diretório corrente. Snapshot3B.2B usa o perfil atual do mesmo catálogo. Qualquer combinação misturada ou sampling/contract divergente é recusada.

Os exemplos audit.example.json/options.example.json/config.real.example.json agora usam o perfil REAL pós-fix. Cópias audit.historical.example.json/options.historical.example.json/config.historical.example.json conservam os valores anteriores. Dataset manifest novo grava collection_profile e cutoff selecionados; pipeline/check_volume/train validam a tupla completa de collection, perfil/config e cutoff antes de consumir dados. Model manifest transporta o cutoff/perfil do dataset, e evaluate também confere manifesto original/hash/perfil/coleta antes de carregar pickle. Features/labels/baselines/modelo/split/gate de volume não alterados.

Compatibilidade: audit/manifest históricos sem ID ainda são reconhecidos pela tupla completa oficial; ID nulo no audit legado é equivalente a ausente. Config sem ID permanece exclusivamente no contexto histórico. Para dados reais pós-fix é obrigatório collection_profile=official-post-fix na config, com início>=cutoff atual. Fixture gerada sem collection continua identificada separadamente como synthetic-fixture-3b-v1, somente data_kind=synthetic/cutoff antigo/seed conhecido; não é perfil de produção e não pode ser reclassificada como real sem procedência. Fixture PostGIS3B.1 usa explicitamente audit histórico e mantém DataKind=synthetic; construção da fixture testada com banco fake, sem Docker.

Exporter preserva localhost/127.0.0.1/::1, REPEATABLE READ/READ ONLY, fronteiras reais e AuditadaSemProtecao. Loader real também exige referências/qualidade auditadas e viagens/fronteiras concordantes com collection.Trips; não certifica autenticidade da evidência. Guias de snapshot foram atualizados com o bloqueio resolvido e comando manual de exportação local. Alterações anteriores de snapshot/documentação preservadas. Sem acesso a produção/SSH/Docker/banco/treino real/commit/push.

Pendências3B.2B/3B.2C: operador ainda precisa executar snapshot seletivo, transferência, restore descartável e conferência conectada de schema/geometria/FKs/contagens; preparar audit e limites completos, exportar CSV local e medir volume/representatividade. Reconhecer release não aprova automaticamente execução inteira, labels ou treino. Referência de snapshot/evidência e gate de qualidade/volume continuam obrigatórios.

### 3B.2B — dependências de triggers no snapshot v2

Operador informou primeiro snapshot real pós-fix de 16 viagens/11 tabelas, 775 telemetrias, 188 passagens e 220 eventos: prepare/export/seal/verify passaram, assim como pre-data e onze contagens de importação em PostgreSQL 16/PostGIS 3.4 local. Post-data falhou: pg_dump seletivo transportou os triggers de imutabilidade sem incluir suas funções. Banco parcial eta_snapshot_3b2b_real01 não deve ser reutilizado; snapshot selado anterior permanece intocado. Evidência fornecida pelo operador, sem conexão ou inspeção do bundle real pelo agente.

Ferramenta passa a gerar contrato noponto-snapshot-3b2b-v2, descobrindo todos os triggers não internos das onze tabelas, suas funções via pg_get_functiondef e dependências externas dos objetos pertencentes às tabelas via pg_depend. Política conservadora exige as duas funções reais de imutabilidade, com corpos revisados contra a migration existente, assinatura/schema/linguagem/SECURITY INVOKER/propriedades preservados; nenhuma função é fabricada a partir da política. Dependências/corpos/triggers adicionais não revisados falham explicitamente. Referências PL/pgSQL do corpo não são completamente descritas por pg_depend; revisão estrita dos corpos cobre essa lacuna para estas funções, ambas referenciando PadroesOperacionais já transportada.

CSV de funções/triggers/dependências, functions.sql e marcador de versão entram nas evidências/SHA256 do manifesto. Restore: pre-data → dados → funções originais → post-data → conferência de constraints/colunas/FKs/funções/triggers/dependências. Sem desabilitar triggers/FKs, sem enfraquecer imutabilidade ou alterar o contrato das onze tabelas. V1 continua verificável sem escrita; restore v1 é recusado antes de conectar, exigindo novo bundle v2 e banco descartável novo. Guia/comandos/limites em [SNAPSHOT_3B_2B.md](../../tools/eta_ml/SNAPSHOT_3B_2B.md).

Validação do agente exclusivamente offline, com subprocessos mockados e regressões positivas/negativas; restore automático conectado v2 e primeiro CSV real ainda dependem do operador. Nenhum acesso a produção/SSH/Docker/banco, nenhuma mudança de API/migrations/sampling/coleta/ETA/infraestrutura/dataset, commit ou push.


### 3B.2B — comparação de metadados e restore v2 conectado

Operador informou validação MANUAL em PostgreSQL16/PostGIS3.4 local descartável, banco eta_snapshot_3b2b_real02: onze tabelas/count_guard=1, duas funções, post-data concluído,38constraints/nenhuma não validada e dois triggers habilitados vinculados corretamente. Catalog(11)/functions(2) idênticos; triggers(2)/dependencies(4) idênticos com search_path=pg_catalog. Essa evidência não foi coletada pelo agente.

A diferença de qualificação public. é representação dependente de search_path, sem indicar alteração funcional. Comparação centralizada no restore/check-restored fixa contexto em cada sessão READ ONLY: catálogo com public visível, demais evidências com pg_catalog, preservando exatamente representações do bundlev2 já selado. Nenhuma remoção de qualificadores ou redução da comparação; vínculo/eventos/tabela/habilitação/propriedades/definições continuam exigidos integralmente. Novo comando check-restored permite validar SOMENTE metadados do banco existente sem reimportar/restaurar/escrever. Guia e comandos em [SNAPSHOT_3B_2B.md](../../tools/eta_ml/SNAPSHOT_3B_2B.md).

27 testes offline aprovados (21snapshot+6auditoria), subprocessos mockados. Bundle existente intocado, contrato v2 preservado, sem reexportar/re-selar. Etapa3B.2B NÃO concluída: geometrias e conteúdo restaurado ainda precisam de validação final, assim como primeiroCSV/volume nas etapas seguintes. Agente não acessou produção/SSH/Docker/banco, nem alterou API/migrations/coleta/ETA; sem commit/push.


### 3B.2C — diagnóstico exploratório real e transição para 3C

Operador confirmou snapshotv2 selado/verificado, banco local descartável eta_snapshot_3b2b_real02 PostgreSQL16/PostGIS3.4,11tabelas/conteúdo integral comparado com origem (incluindo geometrias),38constraints válidas,2funções/2triggers preservados. Bundle tools/eta_ml/outputs/snapshot-3b2b-v2-real-01,16viagens,775telemetrias,188passagens,220eventos. Perfil official-post-fix/cutoff2026-10-07T21:00:57.997530Z/snapshot_end2026-10-07T22:09:24.149621Z. A pendência de conteúdo/geometrias do restore3B.2B foi encerrada pelo operador; agente não conectou em banco. Distância geography específica dos candidatos ainda requer consulta3A local.

Ferramenta mínima stdlib tools/eta_ml/diagnose.py: verify do bundle existente → diagnóstico OFFLINE por viagem/global/linha/modal/faixa → SQL3A original parametrizado para execução MANUAL local → reanálise dos candidatos com distância conferida. Sem conector, subprocessos, dependências novas, CSVdataset, treinamento ou mudança de backend. Hashes normalizados de SQL3A/EtaDataset em diagnose.sources.json bloqueiam evolução silenciosa dos critérios; snapshotmanifest/script/SQL/candidatefile têm hashes no relatório. Nenhum bundle selado é modificado; relatório exige novo diretório externo.

SQL gera os mesmos LEFT JOINs/distância geography/paginação do SQL3A, sem filtrar tempos/labels inválidos antes de contar. Cursores por janela/linha calculados do bundle verificado, páginas<=1000 GPS antes dos joins; viagem selecionada explicitamente. Guard de banco dedicado, READ ONLY/REPEATABLE READ/timeout60s/lock2s, execução manual exclusivamente loopback. Resultado SQL é confrontado com GPS/passagem/journal/estrutura das fontes seladas antes da reanálise; incompletude ou adulteração falham. Report por viagem contém UUIDs privados somente em outputs ignorado, qualidade=NaoVerificada/certificação não atribuída.

Elegibilidade reportada é PRELIMINAR e COMPONENTE, nunca certificação/exportação EtaDataset. Diagnóstico usa critérios técnicos de identidade/journal/fronteiras/tempo/posição/distância do contrato atual, sem chamar ExportarCsvAsync: esse método exige AuditadaSemProtecao, não concedida aqui. Não inventa esse enum nem referência auditada para contornar o gate. Predicados diagnósticos têm sourcepins/regressões, mas não substituem o validador canônico: exportação final ainda exige auditoria externa, splits temporais por viagem completa e deduplicação de observações conflitantes. Horizonte diagnóstico3600s; primeiro motivo de rejeição por GPS, separado das métricas independentes identidade/associação/journal. Não avaliar modelos ou erroETA nesta amostra.

Resultado REAL obtido OFFLINE dos arquivos selados:775GPS;758identidades operacionais válidas e758GPS associados a passagem/journal conferido (97,8%);17identidades inválidas e17labels ausentes (mesmos GPS, não somar os dois como34);0associações ambíguas. Entre758com label/journal,0rejeições preliminares de identidade estrutural/tempo/topologia/fronteiras/posição/comprimento;758PendenteGeographySQL3A. Não declarar758 elegíveis definitivos; contadorTecnicamenteElegivelPreliminar=0 enquanto falta a consulta geography. Zero de descartes de distância ainda não é validação dessa distância.

Distribuição GPS: linha28=103;50=121;60=16;73=457;764=71;80=7. ModalBRT=704/ONIBUS=71. Distância INFORMADA (ainda não conferida):<200m=205;200-999m=297;>=1000m=256;ausente/inválida=17. Por viagem/motivo, consultar report.json privado. Report produzido em tools/eta_ml/outputs/diagnostic-3b2c-real-02/report.json e SQL no mesmo diretório; a geração é determinística para mesmos fontes/arquivos/parâmetros/versão do script.

Comandos FUTUROS PowerShell, raiz do checkout, identidade via PGUSER/passfile externo, sem senha em linha, somente localreal02. SQL já foi gerado offline no diretório abaixo. Não executar automaticamente contra serviços não isolados:

```powershell
# Repetir offline em diretório NOVO se necessário:
python tools/eta_ml/diagnose.py tools/eta_ml/outputs/snapshot-3b2b-v2-real-01 tools/eta_ml/outputs/diagnostic-3b2c-novo
# Para o relatório já preparado:
Push-Location tools/eta_ml/outputs/diagnostic-3b2c-real-02
try {
    psql -X --host=127.0.0.1 --port=55439 --dbname=eta_snapshot_3b2b_real02 --no-password -v ON_ERROR_STOP=1 -f diagnostic.sql
    if ($LASTEXITCODE -ne 0) { throw 'Diagnóstico SQL incompleto; não analisar como concluído.' }
} finally { Pop-Location }
python tools/eta_ml/diagnose.py tools/eta_ml/outputs/snapshot-3b2b-v2-real-01 tools/eta_ml/outputs/diagnostic-3b2c-completo --candidates tools/eta_ml/outputs/diagnostic-3b2c-real-02/diagnostic-candidates.csv
```

Para futuros snapshots: verify/restauração/isolamento atestados, novo bundle e diretório de relatório; informar --database eta_snapshot_NOME_DEDICADO se diferente de real02, usar esse mesmo banco no comando psql local. Se SQL3A/EtaDataset mudar, revisão explícita dos critérios/pins antes de continuar. Não re-selar snapshot, não usar relatório como audit do exporter, não reutilizar nome de saída existente. Faixas finais usam distância geography conferida quando candidatos foram fornecidos. O SQL é SELECT/metadata/clienteCOPY, não modifica dados.

Validação agente:33testes offline aprovados (6diagnóstico+21snapshot+6auditoria),0falhas,3,382s; fixtures/mocks sem banco, positivos/negativos de identidade/journal/label/tempo/posição/distância, pins de fontes, contexto readonly/paginação, outputforaBundle, guardbanco e resultadoSQL divergente. git diff --check aprovado; sem produção/SSH/homeserver/Docker/banco/treino real/commit/push.

Fechamento rápido3B.2C: falta apenas operador executar SQL3A local e reanalisar as distâncias, registrar contagens finais/limitações e transferir código/contratos/relatório privado ao repositórioML na3C. Amostra16viagens/6linhas predominantementeBRT é exploratória; não libera treino real nem certifica representatividade. 3C tratará repositórioML próprio, serviçoinferência e Shadow com autorização/validação próprias, sem implantação nesta etapa.


### Hotfix 3B.2C — comando psql copy em uma única linha

Operador reproduziu parse error at end of line no diagnostic.sql: o metacomando psql copy terminava na primeira quebra de linha da consulta3A. Corrigido somente diagnose.py e regressões: copy_query percorre SQL por estados léxicos, remove comentários apenas fora de strings/identificadores, inclusive comentários de bloco aninhados, transforma whitespace externo em espaços e substitui parâmetros apenas fora de quotes. Preserva conteúdo/doubledquotes de literais/identificadores, filtros/cursores/LEFT JOINs/geography. Dollarquotes, literais prefixados/escapados/multiline, concatenação de strings dependente de newline e construções incompletas falham explicitamente; não são reescritos por hipótese. SQL3A e sourcepins não alterados.

Novo arquivo gerado offline em outputs/diagnostic-3b2c-copy-hotfix01:11linhas,1copy completo,16consultas3A/15UNIONALL,16cálculosgeography. Mesmas contagens offline775GPS/758pendentesGeography/17labelausente. Bundles e relatórios anteriores preservados; nenhuma execuçãoSQL/Docker/banco pelo agente.

Repetição manual em diretório NOVO (hotfix02 abaixo), cliente psql no PC acessando a porta55439 do Docker LOCAL descartável já isolado. Não requer nome do container nem docker exec; usuário/passfile externos, sem senha em linha:

```powershell
python tools/eta_ml/diagnose.py tools/eta_ml/outputs/snapshot-3b2b-v2-real-01 tools/eta_ml/outputs/diagnostic-3b2c-copy-hotfix02
if ($LASTEXITCODE -ne 0) { throw 'Geração incompleta' }
Push-Location tools/eta_ml/outputs/diagnostic-3b2c-copy-hotfix02
try {
    psql -X --host=127.0.0.1 --port=55439 --dbname=eta_snapshot_3b2b_real02 --no-password -v ON_ERROR_STOP=1 -f diagnostic.sql
    if ($LASTEXITCODE -ne 0) { throw 'SQL incompleto; não continuar' }
} finally { Pop-Location }
python tools/eta_ml/diagnose.py tools/eta_ml/outputs/snapshot-3b2b-v2-real-01 tools/eta_ml/outputs/diagnostic-3b2c-copy-hotfix02-completo --candidates tools/eta_ml/outputs/diagnostic-3b2c-copy-hotfix02/diagnostic-candidates.csv
```

Validado offline:36testes aprovados (9diagnóstico+21snapshot+6auditoria),0falhas,5,497s; comparação de tokens da query3A parametrizada antes/depois e arquivo gerado com copy completo numa linha. git diff --check aprovado. Psql conectado/fixture descartável não executados pelo agente: não iniciado serviço/container e não acessado banco existente. A conferência sintática offline não certifica execução PostgreSQL; confirmação conectada fica com operador. GuardsREADONLY/REPEATABLEREAD/banco/timeout/paginação/qualidade e ausência de dataset de treino mantidos. 3B.2C continua aguardando execução/relatório final para transição3C.


### 3C — ML separado em checkout acessível, inferência isolada preparada

Resultado final3B.2C confirmado pelo operador e reconstituído dos arquivos locais:775GPS/758com identidade-passagem-journal/357tecnicamente elegíveis preliminares/401distâncias divergentes/17sem label. Diagnóstico exploratório encerrado; não há datasetcertificado/AuditadaSemProtecao nem autorização para treino real. Pendência401 passa a investigação separada de semântica da distância, não bloqueia separação dos componentes.

Repo ML encontrado em D:/repositorio_github/NoPonto/ml (develop), não presumido. Quinze arquivos de treino/avaliação/fixtures/configs/perfis/contratos transferidos byte a byte; mapa/SHA256 em tools/eta_ml/migration-3c.manifest.json e cópia no ML. Arquivos backend mantidos para compatibilidade com CLIs/testes/documentação atuais, retirada coordenada futura; nenhuma mudança runtime/ETApúblico. Coleta/journal/EtaDataset/exporter/auditoria/SQL/snapshot/diagnóstico continuam backend. LegadoML existente preservado. Mapa exato/contrato/limites/Shadow/retreino/comandos em [MIGRACAO_3C.md](../../tools/eta_ml/MIGRACAO_3C.md), também README_ETA_3C.md no ML.

Novo serviçoML separado eta_history_service.py com /eta/batch,/health,/ready, Dockerfile.eta-history e compose próprio preparados, nenhum build/deploy. PipelineExtraTrees/baselines/métricas/gates unchanged. O clienteHTTP atual usa chunks200/timeout3s/respostaarrayordem, mas não fornece modal/padrão/parada/topologia/posiçãodestino; distância é direta e hora usa relógioatual local. Serviço aceita featurescompletas, recusa payload incompleto503 e não inventa valores. Shadow exige adaptercausal separado e não poderá trocar ETApúblico. Container proposto512MiB/.5CPU/1worker/2concorrência, modelo readonly/64MiBmax, sem retreinoendpoint/cron. Realmodel ausente→ready503; syntheticopt-in somentelocal. Memória/latência não medidas.

Investigação401: repository calcula DistanciaProximaParadaMetros por ST_Distance direto ao alvo matching; campo DistanciaRestanteRotaMetros separado não é transportado no ML. Factory copia distância direta mas usa IDsalvooperacional; SQL3A calcula trechoGeography da rota ao operacional. Semânticas incompatíveis confirmadas no código; amostraoffline401,400distâncias informadas próximas(até10m) de aproximação direta ao operacional,283menores que trecho. Um outlier linha764 requer alvooriginal/matching, não preservado separadamente; não declarar causaindividual completa. Amostra privada outputs/distance-evidence-3c-02. Nenhuma tolerância/label/SQL3A/factory/snapshot alterada.

57testes offline aprovados:38backend/13pipelineML/5serviçoML/1migraçãoML;15hashes conferidos, git diff --check em ambos repos aprovado. Sem produção/SSH/homeserver/Docker/banco/treino real/commit/push. Próximos gates: coordenar retirada de cópias, revisar distância/contrato, datasetcertificado/volume, adapterShadow, modelo real e benchmark antes de implantação separadamente autorizada.

### 3E — Shadow histórico técnico preparado

Adapter causal, produtor/fila bounded, consulta geográfica por lote, cliente HTTP exclusivo, circuito e sink limitado implementados no backend, default desligado. Contrato noponto-eta-shadow-history-v1 preserva features existentes e calcula distância pela rota versionada, não direta. Hora/dia usam TimestampGps UTC−03:00; alvo deve estar à frente na mesma execução/volta. Serviço ML ecoa correlação e identifica artefato/data_kind; backend recusa modelo sintético e lotes sem metadados/ordem válidos. ETA público e Shadow legado permanecem independentes.

Guia e comandos locais em [SHADOW_HISTORICO_3E.md](../../tools/eta_ml/SHADOW_HISTORICO_3E.md). 64 testes C#, seis testes de serviço ML e teste dos hashes de migração offline aprovados; consulta PostGIS real, Docker, benchmark e homologação operacional não executados. Sem dataset certificado ou modelo real, não há comparação real; 503/model_unavailable é o estado esperado. Não houve alteração dos gates/SQL3A/labels/sampling, ativação em produção, deploy ou treino real.

### 3E.1 — componentes Shadow aprovados para habilitação local técnica

Homologação exclusivamente em fixture descartável PostgreSQL 16.4/PostGIS 3.4.3, HTTP controlado em loopback e container ML novo sem rede/modelo. 65 testes C# offline, um teste conectado composto, seis testes ML e um de hashes de migração aprovados. Build/Docker build e smoke health/ready/batch passaram; nenhuma API ou Redis operacional foi iniciado/usado. Captura tardia após StopAsync agora registra stopped e Dispose libera cliente próprio. SQL3A, treino/gates/modelo/sampling/default OFF e ETA público preservados.

Lotes 1/10/50/200: medianas 6,322/5,155/5,203/8,637ms; maior amostra 155,417ms. Pool ocupado e HTTP lento cancelaram perto de 1000ms com entrega/cleanup adicionais medidos; não declarar deadline wall-clock rígido. Memória amostrada PostgreSQL135,5MiB/ML92,24MiB; modelo ausente. Cold-start ML levou ~101s até primeiro health200; tentativa fria de consulta excedeu orçamento anteriormente. Fixture de poucos vértices não estima homeserver nem rotas reais. Relatório, rastros/limites e comandos em [HOMOLOGACAO_SHADOW_3E_1.md](../../tools/eta_ml/HOMOLOGACAO_SHADOW_3E_1.md). Aprovação técnica local, não certificação/modelo real ou autorização de produção.
# Observabilidade histórica — 3F

3F acrescenta agregações e logs limitados do worker histórico, mantendo Shadow OFF e ETA público independente. Relatório, custos efetivamente medidos, distinção entre diferença de previsões e erro de chegada e reprodução local: [OBSERVABILIDADE_SHADOW_3F.md](../../tools/eta_ml/OBSERVABILIDADE_SHADOW_3F.md). Não há modelo real certificado nem avaliação contra labels confiáveis. Imports científicos no ML foram adiados até necessidade de artefato/inferência, sem alteração dos contratos/gates/pipeline de treino; readiness continua 503 sem modelo.

## Qualificação 3G.1

Pré-validação e orquestração condicionada disponíveis no ML, reutilizando gates existentes. Prontidão real reprovada: ausência de evidência de proteção por viagem inteira, CSV certificado e volume suficiente. Revisão futura isolada da política de distância aprovada pelo usuário; nesta etapa comparação3A/tolerâncias/labels e401descartes preservados. Relatório/evidências/proposta prospectiva/comandos: [QUALIFICACAO_TREINO_REAL_3G_1.md](../../tools/eta_ml/QUALIFICACAO_TREINO_REAL_3G_1.md). Nenhum treino real, certificação ou ativaçãoShadow.

## Revisão isolada 3G.2

Política técnica local aprovada, exporter/manifest/config/modelo versionados; antiga comparação direta versus rota removida, demais gates preservados. Real offline:357→758preliminares/401→0descartessemânticos/17sem label; zero certificados. Outlier continua sem evidência individual. Relatório/comandos/limites: [REVISAO_DISTANCIA_3G_2.md](../../tools/eta_ml/REVISAO_DISTANCIA_3G_2.md). Não autoriza treino ou Shadow.
