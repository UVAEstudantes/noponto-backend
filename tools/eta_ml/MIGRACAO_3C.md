# NoPonto 3C — separação do ML e inferência isolada

Repositório ML localizado por inspeção local: D:/repositorio_github/NoPonto/ml, checkout Git develop. Não presumido, clonado ou conectado remotamente. Nenhum AGENTS adicional no destino/ancestrais; AGENTS do backend respeitado. Estado anterior ML: apenas lab-output/ untracked, preservado; .env não lido. Legacy server.py/treinar.py/modelos.joblib/Dockerfile/docker-compose.yml e seus testes permanecem intactos. Código 3A/3B não foi substituído pelo dataset_v1.py, que modela segmentos/passagens com outro contrato.

## Mapa exato e estado de transferência

migration-3c.manifest.json lista 15 arquivos com origem, destino e SHA256. Cópia local realizada sem reescrever bytes; hashes origem/destino conferidos. Layout tools/eta_ml preservado para imports/CLIs/testes/configs; tools/collection_profiles.json mantém caminho relativo esperado. Arquivos novos adicionais de serviço/deploy não fazem parte dos hashes de transferência, que registram somente o código original transportado.

| Origem backend | Destino ML | Responsabilidade |
|---|---|---|
| tools/eta_ml/pipeline.py | tools/eta_ml/pipeline.py | ExtraTreesRegressor CPU, preprocessing, baselines, métricas, artefato, gate de volume |
| train.py/evaluate.py/check_volume.py em tools/eta_ml | mesmos caminhos | CLIs offline com procedência/hash/qualidade |
| synthetic.py/test_pipeline.py em tools/eta_ml | mesmos caminhos | fixture sintética e 13 regressões, nenhuma coleta real |
| collection_profiles.py/requirements.txt em tools/eta_ml | mesmos caminhos | catálogo permitido e numpy2.2.6/scipy1.16.1/sklearn1.6.1/joblib1.4.2/threadpoolctl3.6.0 |
| config.synthetic.json/config.real.example.json/config.historical.example.json em tools/eta_ml | mesmos caminhos | splits/configs/perfis preservados |
| tools/eta_ml/examples/synthetic.model.manifest.json | mesmo caminho | exemplo SINTÉTICO, não modelo real |
| tools/collection_profiles.json | mesmo caminho | contrato noponto-eta-gps-v1 e perfis oficiais |
| NoPonto/docs/ETA_ML_DATASET_3A.md | contracts/ETA_ML_DATASET_3A.md | cópia contratual versionada por hash |
| NoPonto/ETA_ML_CANDIDATOS_3A.sql | contracts/ETA_ML_CANDIDATOS_3A.sql | referência contratual; execução pertence ao backend |

Permanecem no backend: coleta/factory/repository/sampling/retention/outbox/journal/migrations/GPS/ETA público; EtaDataset e seus testes canônicos; EtaMl.Export/test_exporter.py; SQL3A/auditoria3B.2A/test_audit_sql.py; snapshot.py/snapshot_schema.py/snapshot_functions.reviewed.json/request/guia/test_snapshot.py; diagnose.py/sourcepins/test_diagnose.py/distance_sample.py/test_distance_sample.py e seus relatórios privados. integration_postgis.py/postgis_fixture.sql ficam com adapter/exporter e fixture conectada (não precisam ser dependência do treino). Docs/HISTORICO permanecem backend; ML recebe somente cópias de contrato e este guia.

As cópias originais de treino no backend não foram apagadas: referências/CLI/tests/documentação existentes ainda dependem delas. ML passa a ser destino preparado para manutenção do treino; retirada coordenada/ajuste de referências será follow-up, evitando duas linhas de evolução. Comparar manifestos/hashes antes de qualquer remoção. Perfis em dois repos devem ser atualizados de forma coordenada e conferidos por hash; nenhuma dependência dinâmica no checkout do backend. Dados/UUIDs/credenciais/bundles/artefatos reais não foram transferidos ou certificados.

Inventário do pipeline: modelo ExtraTreesRegressor64árvores/profundidade18/max_leaf_nodes4096/min_samples_leaf3/n_jobs1/seed20261007; OneHotEncoder categórico unknownignore/minfrequency5, imputação numérica fitada TRAIN. Baseline físico distância/velocidade causal com fallback TRAIN, baseline histórico mediana por grupo TRAIN. Métricas MAE/RMSE/mediana/P90/coverage e breakdowns existentes. Artefatos model.pkl/model.manifest.json/metrics.json com hashes/dataset/perfil/cutoff/splits/versões/seed. Features em pipeline.FEATURES, labels/IDs de auditoria excluídos; nenhuma feature/label alterada na migração.

## Contrato HTTP real e bloqueio de features

GpsEtaClient.cs envia POST /eta/batch como array, chunks200, timeout3s (Program.cs), cooldown30s após falha. Itens: linha,hora_dia,dia_semana,distancia_metros,velocidade_media,posicao_na_rota,padrao_versao_id,ocorrencia_parada_padrao_id,sentido_id,linha_id. IDs podem ser nulos: elegibilidade do cliente não exige identidade estrutural completa. Resposta array na MESMA ordem/quantidade: eta_segundos,eta_minutos,confianca,linha_conhecida; associação ao veículo é feita por índice, não por ID. Cliente calcula horário usando agora.ToLocalTime(), não timestampGPS UTC-3: avaliar esse desvio no futuro adapterShadow.

Modelo histórico exige também modal,padrao_id,parada_id,topologia,posicao_destino; velocidade_kmh pode ser nula causalmente. Payload legado não permite reconstruir esses campos sem estrutura/lookup/alteração de adapter. Distância do cliente é direta ao alvo de matching, não geography do trecho operacional do treino. Portanto NÃO conectar serviço histórico ao ETA público, NÃO completar estrutura com valores fictícios nem tratar serviço como drop-in. Compatibilidade de wire/ordem/array/aliases existe; compatibilidade semântica do payload atual NÃO existe. Serviço retorna503 incomplete_historical_features para esse caso. Completar adapterShadow causalmente e validar significado de distância/horário/velocidade é requisito antes de inferência com dados atuais.

eta_history_service.py é app isolada; server.py legado continua igual. Aceita features canônicas completas e aliases verificados linha→codigo_linha/padrao_versao_id→versao_id/ocorrencia_parada_padrao_id→ocorrencia_id/posicao_na_rota→posicao_gps/velocidade_media→velocidade_media_causal_kmh. Mesmas features do artefato, sem label ou ID auditoria. Valida bounds/modal/topologia/speeds finitas; teto200itens/body256KiB. Modelo trustedpickle local<=64MiB, hash/contrato/versão sklearn/procedência real conferidos antes de unpickle. Synthetic só com ETA_ALLOW_SYNTHETIC=1 para teste local explícito; confiança sempre baixa, sem alegar calibração. Emptybatch→[]; modelo ausente/incompatível503, nunca treinamento/fallback fictício; /retreinar não existe. /health é liveness; /ready valida/carrega modelo e informa digest/data_kind, falhando503 quando indisponível. Hash não autentica um pickle: montar apenas artefatos locais confiáveis.

Sem modelo real certificado disponível: readiness real deve falhar. Serviço e API in-process testados com predictorFAKE; não executar essas fixtures em produção. Inferência real/performance/calibração não validadas.

## Docker e limites para 4GB — preparado, não executado

Dockerfile.eta-history separado do Dockerfile legado. Python3.11-slim, dependências existentes de treino + FastAPI0.115.12/uvicorn0.34.2 já presentes no ML legado, sem biblioteca nova. Um worker/threadsBLAS1/concurrency2, user10001/read-only/cap_dropALL/no-new-privileges, modelo volume:/models:ro,512MiB/0.5CPU/128PIDs/tmpfs16MiB. Sem porta publicada no compose; serviço rede privada futura separada do backend atual. Healthcheck /health30s/timeout3s; implantação deve exigir também /ready. Limites propostos, não benchmark de memória/latência: medir RSS/p95/error/queue/rejeições antes de uso, meta p95<500ms para respeitar cliente3s; tamanho do artefato64MiB não garante RSS<=512MiB.

Build/deploy compose não executados. Registrar digest da base/image final e versões no primeiro build; tag python3.11-slim não fixa digest. Dockerfile específico .dockerignore exclui .env/.git/outputs/pickles/joblibs; artefatos entram só por volume. Nunca apontar volume a modelo legado não atestado como noponto-eta-gps-v1. Nenhum treinamento por endpoint/cron dentro do container de inferência.

## Shadow e retreinamento periódico futuros

Não alterar ML:BASE_URL/serviço atual/sampling/polling/ETApúblico nesta rodada. Shadow novo deve ser consumidor separado de origem causal: capturar mesmo timestampGPS/identidade/alvo/distância operacional/ETA atual, enviar em lotes<=200 por fila limitada independente, timeout<=1s com backpressure/drop/circuit-breaker. Respostas ficam em sinkShadow separado com observacao_id/modeldigest/versãocontrato/perfil/latência/erro/tempo; nunca substituir retorno público. EtaV2Shadow existente é LONGITUDINAL_SPEED_V0 e não equivale ao novo Shadow histórico; não reaproveitar flags por nome sem revisar semântica. Validar origem/consumer/idempotência/volume antes de implantação autorizada; nenhuma fila/sink/config criada aqui.

Retreino: job OFFLINE no PC/runner com RAM adequada, periodicidade semanal proposta depois de existir dataset certificado/volume. Snapshot→exporter canonical auditado→check_volume→train→evaluate na MESMA divisão temporal→comparar baselines/modelo anterior→manifest/hash/smoke→promoção humana/atômica de diretório versionado readonly. Nunca train no homeserver4GB ou /retreinar. Gates existentes:>=10000linhas,>=14dias,>=200/50/50viagens por split, linha>=30/10/10viagens e8horasTRAIN; amostra atual357preliminares não chega perto nem possui qualidade certificada.

## Investigação focalizada dos401 casos

Estado real confirmado no report pósPostGIS:775GPS/758com identidade+passagem+journal/357tecnicamente elegíveis PRELIMINARES/401DistanciasDivergentes/17sem label. Nenhuma AuditadaSemProtecao/datasetcertificado.

Rastreamento código: GpsItinerarRepository.cs126/207/529 calcula distancia_parada_metros=ST_Distance(v.ponto,p.Localizacao::geography), distância DIRETA do GPS recebido à parada do matching. Mapeamento421/628 atribui esse campo a DTO.DistanciaProximaParadaMetros; calcula separadamente distancia_restante_rota_metros e DTO.DistanciaRestanteRotaMetros. TelemetriaMl.cs copia DistanciaProximaParadaMetros diretamente; substitui IDs das ocorrências pelo alvoOPERACIONAL quando confiável, sem recalcular distância. SQL3A mede ST_Length(ST_LineSubstring(versão.Geometria,posicaoGPS,posicaoDestino)::geography) do trecho de rota até ocorrênciaOPERACIONAL. EtaDataset compara campo informado com esse valor, tolerância max(10m,10%). Comparação envolve duas semânticas distintas; aumentar tolerância não resolve causa.

distance_sample.py verifica hashes report/candidatefile/bundle e gera amostra privada determinística por linha + maior outlierdireto, sem escrever no bundle. Saída outputs/distance-evidence-3c-02/distance-sample.json:401divergências,283informadas menores que rota;400/401distâncias informadas a<=10m de estimativa esférica direta ao alvooperacional. Forte evidência de mismatch direto/rota como causa dominante, não confirmação esferoidal individual. Um outlier linha764:informada45.04m/rota184.22m/diretaaproximada ao operacional191.16m; alvo original do matching não é preservado separadamente na telemetria quando sobrescrito, então não atribuir esse caso a erro geométrico sem rastrear o alvooriginal. Não mudar SQL3A/tolerâncias/labels/factory nesta etapa. Próxima correção de contrato/telemetria requer tarefa separada com evidência e versionamento; snapshot atual permanece intacto e casos recusados.

## Testes e próximos comandos manuais

No ML:13testes do pipeline migrado aprovados (treinos só sintéticos,~19.9s);5testes de serviço aprovados (~0.09s), incluindo shape/ordem, payload real incompleto503, limites/health/ready e hash/syntheticgate antes do pickle. Backend:38testes offline diagnóstico/snapshot/auditoria/amostra aprovados (~6.8s). Quinze hashes de transferência iguais. git diff --check em ambos aprovado; whitespace dos novos arquivos conferido. Sem teste Docker/buildHTTPconectado/memóriabenchmark, dataset/treino real, deploy/SSH/produção/commit/push.

PowerShell no repo ML:
```powershell
Set-Location D:/repositorio_github/NoPonto/ml
$env:PYTHONPATH='tools/eta_ml'
python -m unittest discover -s tools/eta_ml -p test_pipeline.py -q
python -m unittest discover -s tests -p test_eta_history_service.py -q
# Só teste sintético em diretório NOVO:
python tools/eta_ml/synthetic.py --output tools/eta_ml/outputs/synthetic-3c
python tools/eta_ml/train.py --dataset tools/eta_ml/outputs/synthetic-3c/dataset.csv --manifest tools/eta_ml/outputs/synthetic-3c/dataset.manifest.json --config tools/eta_ml/config.synthetic.json --output tools/eta_ml/outputs/model-synthetic-3c
# Build futuro, sem iniciar/deployar serviço:
docker build -f Dockerfile.eta-history -t noponto-eta-history:3c-local .
```

Checar argumentos de synthetic.py antes de executar (CLI preservada); modelos/outputs são privados, adicionar política de ignore antes de versionar artefatos. Executável de treino não deve consumir diagnostic-candidates.csv nem report.json como datasetmanifest.

Bloqueios restantes: retirada coordenada das cópias backend; certificação dataset/volume; resolver semântica distância; adapterShadow com features completas/horárioGPS causal; artefato real; benchmark/isolamento/rede privada/autorizaçãodeploy. Separação física preparada e tests/contratos preservados; não declarar Shadow em operação nem ML substituindo ETA público.


### Comandos adicionais e dependências de testes
As versões FastAPI0.115.12/uvicorn0.34.2/starlette0.46.2/httpx0.28.1 foram inspecionadas no ambiente local. requirements.eta-history-test.txt acrescenta apenas httpx0.28.1 para TestClient; produção usa requirements.eta-history.txt. Lock completo/digest do build ainda pendentes. Carregamento de modelo é serializado para evitar duas cópias simultâneas; artefato real ainda indisponível.

```powershell
# Dependências futuras, no ambiente ML isolado (não executado pelo agente):
python -m pip install -r requirements.eta-history-test.txt
# Reproduzir avaliação SINTÉTICA depois do comando de treino acima:
python tools/eta_ml/evaluate.py --dataset tools/eta_ml/outputs/synthetic-3c/dataset.csv --manifest tools/eta_ml/outputs/synthetic-3c/dataset.manifest.json --config tools/eta_ml/config.synthetic.json --model tools/eta_ml/outputs/model-synthetic-3c/model.pkl --model-manifest tools/eta_ml/outputs/model-synthetic-3c/model.manifest.json --output tools/eta_ml/outputs/model-synthetic-3c/evaluation.json
python -m unittest discover -s tests -p test_migration_3c.py -q
```

Novo teste de15hashes passou (1teste,~0.005s); serviço final5testes passaram (~0.110s). Total desta etapa:57testes offline (38backend+13pipelineML+5serviçoML+1migraçãoML). Gitdiff/whitespace finais conferidos nos dois repos. Não houve pipinstall/Dockerbuild/deploy/rede pelo agente.

## Revisão versionada 3G.2

Quatro arquivos originalmente transferidos foram revisados apenas para metadata/guards de política: collection_profiles, pipeline, evaluate, config.real.example. Manifest histórico3C preservado; ML migration-3g2.revision.json registra ligação dos hashes originais/atuais. Demais11hashes inalterados. Novo validation_policies.py tem cópias idênticas backend/ML; features/labels/modelo/gates inalterados. Ver REVISAO_DISTANCIA_3G_2.md.
