# Isolamento Git ETA/ML — checkpoint experimental e plano MVP

2026-10-08. Checkpoint local EXPERIMENTAL — NÃO IMPLANTAR. Não autoriza deploy, migrations, Shadow, certificação, permissões ou treino real. Repositório ML não alterado.

## Preservação

Base conferida: main/HEAD `70d7bcb8041e167df063237b4fdcafd4971b01d6`; índice inicialmente vazio, 21 arquivos rastreados modificados e 59 novos. Branch experimental/eta-evidence-3g criada a partir dessa base preservando o working tree. Seleção explícita dos 80 arquivos originais abaixo; nenhum output, dump, CSV real, modelo, cache ou credencial selecionado. Este plano é o único arquivo novo adicional; HISTORICO recebe somente acréscimo documental. Staging deve preservar bytes originais para não invalidar hashes existentes por conversão CRLF/LF.

JSONs dos candidatos conferidos; sem whitespace excedente ou marcadores fortes de segredo encontrados. Scripts de fixture usam recursos exclusivos/loopback; não foram executados. Inspeção de segredos não é prova contra toda forma possível de falsificação/credencial. NoPonto/.env, bin, obj, lab-output e outputs dos utilitários permanecem locais, ignorados e intactos. Há caminhos de build excessivamente longos em lab-output; não limpar nem copiar esses diretórios.

## Worktree após checkpoint válido

Destino: `D:/repositorio_github/NoPonto/noponto-backend-eta-ml-mvp`.
Branch: feature/eta-ml-mvp, criada diretamente da main, sem cherry-pick ou aplicação de patches. Deve permanecer limpa em `70d7bcb8041e167df063237b4fdcafd4971b01d6` até aprovação deste plano. main não deve receber commits nem ter sua referência alterada. O SHA do checkpoint será informado no encerramento; pode ser consultado por git rev-parse experimental/eta-evidence-3g.

## Transferência mínima proposta — NÃO aplicada

### 1. Dataset/exportação e compatibilidade de perfil/política

Transportar conteúdo completo, após revisão, dos seguintes arquivos do checkpoint:

- NoPonto/2-Application/Services/GPS/EtaDataset.cs
- NoPonto/5-Testes/EtaDatasetTests.cs
- tools/EtaMl.Export/Program.cs e EtaMl.Export.csproj
- tools/EtaMl.Export/audit.example.json, options.example.json, audit.historical.example.json, options.historical.example.json
- tools/collection_profiles.json
- tools/eta_ml/test_exporter.py
- NoPonto/docs/ETA_ML_DATASET_3A.md (revisão 3G.2)

O SQL3A canônico, factory/telemetria, sampling, gates, labels e migration operacional já estão na base e não devem ser substituídos. O exporter precisa do catálogo como EmbeddedResource e do marker de política no validator; não transportar apenas o marker ou apenas o exporter. A cópia local dos testes Python do exporter importa helpers de perfil/política e test_pipeline: revisar suas dependências antes de decidir o conjunto auxiliar de testes. Para manter os testes existentes, candidatos auxiliares são collection_profiles.py, validation_policies.py, pipeline.py, evaluate.py, configs real/histórica e test_pipeline.py em tools/eta_ml; são compatibilidade legada, não nova autoridade de treino no backend. integration_postgis.py com política atual é auxiliar de homologação, não runtime da API.

### 2. Shadow histórico OFF e observabilidade

Transportar por arquivo completo:

- NoPonto/2-Application/Services/GPS/HistoricalEtaShadow.cs
- NoPonto/2-Application/Services/GPS/HistoricalEtaOperationalStatistics.cs
- NoPonto/5-Testes/HistoricalEtaShadowTests.cs
- NoPonto/5-Testes/HistoricalEtaShadowLocalTests.cs
- tools/eta_ml/homologate_shadow.ps1, profile_shadow_startup.ps1, smoke_shadow_ml.ps1
- tools/eta_ml/SHADOW_HISTORICO_3E.md, HOMOLOGACAO_SHADOW_3E_1.md, OBSERVABILIDADE_SHADOW_3F.md

Recriar somente estes hunks sobre a base, nunca copiar os dois arquivos misturados completos:

- Program.cs: bloco de registro de HistoricalEtaShadowOptions, cliente HTTP eta-history-shadow, IHistoricalEtaGeometry/HistoricalEtaGeometryQuery, singleton e hosted service HistoricalEtaShadow. Não incluir bloco de EtaDecisionCoverageOptions/Coordinator/EtaGpsIngressCoverage/EtaEvidenceBoundaryWriter/IEtaDecisionCoverageSource. Não alterar ML:BASE_URL, cliente público ou flags operacionais; Enabled permanece false.
- GpsPollingService.cs: somente campo _historicalEtaShadow; parâmetro opcional HistoricalEtaShadow historicalEtaShadow=null adaptado ao construtor da base; atribuição ao campo; chamada _historicalEtaShadow?.TryCapture(posicao, viagem) no ponto causal após confirmação, junto ao Shadow legado. Excluir coverage/ingress, Tick/InvalidateAll, admission/LeavePending/Resolve e CompleteEvidenceLocalAsync. Campos e construtor misturam hunks: reconstrução manual mínima, com revisão do patch, será necessária.

As classes Shadow não têm dependência de tipos de certificação. Testes devem ser executados com dependências locais/stubs; a suíte conectada só mediante fixture exclusiva autorizada. Não iniciar API completa.

### 3. Documentação pertinente

Transportar FECHAMENTO_MVP_ETA_ML.md e MIGRACAO_3C.md como documentação histórica, preservando suas ressalvas sobre o working tree experimental. Acrescentar na futura worktree somente registro novo da separação/validação; não copiar HISTORICO completo experimental para fingir que 3G foi implementado no MVP. ETA_ML_TREINO_3B.md contém várias etapas: selecionar conteúdo pertinente ou adicionar referência, sem trazer afirmações de implementação de certificação para a versão MVP.

### 4. Ferramentas opcionais, fora do patch inicial

snapshot.py/snapshot_schema.py/request/inventário de funções/testes, diagnose/amostra/comparação/pins podem ser transportados em commits próprios se necessários ao operador. Não são requisito para ligar Shadow OFF nem para manter serviço ML instalado. Não transportar snapshots, relatórios de dados reais ou diretórios outputs. Treino, qualificação, ciclo offline e serviço de inferência permanecem no ML; nenhum arquivo será escrito nesse repositório nesta operação.

## Exclusões obrigatórias

Não transportar:

- migration 20261008090000_EtaTripEvidenceFoundation;
- EtaTripEvidence.cs/v2, EtaDecisionCoverage, EtaGpsIngressCoverage, EtaEvidenceBoundaryWriter, EtaEvidenceRecoveryRepository, EtaTripEvidenceRepository;
- mudanças de ViagemOutboxWorker, RetryOperacionalGps, ViagemObservadaService e ViagemOperacionalRepository destinadas à evidência/cobertura;
- testes/fixtures/runners/sourcepins de certificação 3G, snapshot-v3.contract, propostas de fencing, roles/ACL/credenciais ou ativação de epochs.

O domínio operacional estabilizado e a correção de renascimento já estão em main. Não há correção nova desses repositories a transportar para o MVP.

## Riscos e validação após aprovação

Program.cs já chama db.Database.Migrate na base: não modificar bootstrap nesta tarefa e não executar API para testar. A futura MVP precisa demonstrar que a lista de migrations não ganhou EtaTripEvidenceFoundation; uma única migration experimental transportada acidentalmente seria risco de produção mesmo com flags OFF. O csproj principal inclui arquivos .cs automaticamente, inclusive testes: arquivos parciais com references 3G causariam falha de compilação; auditar diff completo contra main e pesquisar referências antes do build.

Após aprovação: aplicar arquivos/hunks revisados somente na worktree, conferir catálogo/política/hashes e guards READ ONLY/local, build backend/exporter sem iniciar API, regressões dataset/telemetria/Shadow/fontes/cadência e exporter Python. Não reconstruir a imagem ML instalada por conveniência. Homologação conectada/Docker exige autorização própria e recursos exclusivos.

Decisão: checkpoint seguro para preservação local; plano de transferência REQUER APROVAÇÃO. Worktree limpa não significa MVP implementado nem release de backend pronta para deploy.

## Inventário original preservado (80 arquivos)

- `NoPonto/2-Application/Services/BackgroundServices/ViagemOutboxWorker.cs`
- `NoPonto/2-Application/Services/GPS/EtaDataset.cs`
- `NoPonto/2-Application/Services/GPS/EtaDecisionCoverage.cs`
- `NoPonto/2-Application/Services/GPS/EtaGpsIngressCoverage.cs`
- `NoPonto/2-Application/Services/GPS/EtaTripEvidence.cs`
- `NoPonto/2-Application/Services/GPS/EtaTripEvidenceV2.cs`
- `NoPonto/2-Application/Services/GPS/GpsPollingService.cs`
- `NoPonto/2-Application/Services/GPS/HistoricalEtaOperationalStatistics.cs`
- `NoPonto/2-Application/Services/GPS/HistoricalEtaShadow.cs`
- `NoPonto/2-Application/Services/GPS/RetryOperacionalGps.cs`
- `NoPonto/2-Application/Services/GPS/ViagemObservadaService.cs`
- `NoPonto/4-Data/Repositories/EtaEvidenceBoundaryWriter.cs`
- `NoPonto/4-Data/Repositories/EtaEvidenceRecoveryRepository.cs`
- `NoPonto/4-Data/Repositories/EtaTripEvidenceRepository.cs`
- `NoPonto/4-Data/Repositories/ViagemOperacionalRepository.cs`
- `NoPonto/5-Testes/EtaDatasetTests.cs`
- `NoPonto/5-Testes/EtaDecisionCoverageTests.cs`
- `NoPonto/5-Testes/EtaIngressCoverageTests.cs`
- `NoPonto/5-Testes/EtaTripEvidenceLocalTests.cs`
- `NoPonto/5-Testes/EtaTripEvidenceTests.cs`
- `NoPonto/5-Testes/HistoricalEtaShadowLocalTests.cs`
- `NoPonto/5-Testes/HistoricalEtaShadowTests.cs`
- `NoPonto/HISTORICO.md`
- `NoPonto/Migrations/20261008090000_EtaTripEvidenceFoundation.cs`
- `NoPonto/Program.cs`
- `NoPonto/docs/ETA_ML_DATASET_3A.md`
- `NoPonto/docs/ETA_ML_TREINO_3B.md`
- `tools/EtaMl.Export/EtaMl.Export.csproj`
- `tools/EtaMl.Export/Program.cs`
- `tools/EtaMl.Export/audit.example.json`
- `tools/EtaMl.Export/audit.historical.example.json`
- `tools/EtaMl.Export/options.example.json`
- `tools/EtaMl.Export/options.historical.example.json`
- `tools/collection_profiles.json`
- `tools/eta_ml/COBERTURA_EVIDENCIA_3G_3B_2A.md`
- `tools/eta_ml/FECHAMENTO_COBERTURA_3G_3B_2B_1.md`
- `tools/eta_ml/FECHAMENTO_MVP_ETA_ML.md`
- `tools/eta_ml/FUNDACAO_EVIDENCIA_3G_3B_1.md`
- `tools/eta_ml/HOMOLOGACAO_SHADOW_3E_1.md`
- `tools/eta_ml/MIGRACAO_3C.md`
- `tools/eta_ml/OBSERVABILIDADE_SHADOW_3F.md`
- `tools/eta_ml/PROPOSTA_CERTIFICACAO_3G_3A.md`
- `tools/eta_ml/PROPOSTA_FENCING_3G_3B_2B_2.md`
- `tools/eta_ml/QUALIFICACAO_TREINO_REAL_3G_1.md`
- `tools/eta_ml/REVISAO_DISTANCIA_3G_2.md`
- `tools/eta_ml/SHADOW_HISTORICO_3E.md`
- `tools/eta_ml/SNAPSHOT_3B_2B.md`
- `tools/eta_ml/collection_profiles.py`
- `tools/eta_ml/compare_distance_policy.py`
- `tools/eta_ml/config.historical.example.json`
- `tools/eta_ml/config.real.example.json`
- `tools/eta_ml/coverage.sources.json`
- `tools/eta_ml/diagnose.py`
- `tools/eta_ml/diagnose.sources.json`
- `tools/eta_ml/distance_sample.py`
- `tools/eta_ml/evaluate.py`
- `tools/eta_ml/fixtures/trip-evidence-v1.json`
- `tools/eta_ml/fixtures/trip-evidence-v2.json`
- `tools/eta_ml/homologate_coverage.ps1`
- `tools/eta_ml/homologate_evidence.ps1`
- `tools/eta_ml/homologate_shadow.ps1`
- `tools/eta_ml/integration_postgis.py`
- `tools/eta_ml/migration-3c.manifest.json`
- `tools/eta_ml/pipeline.py`
- `tools/eta_ml/profile_shadow_startup.ps1`
- `tools/eta_ml/smoke_shadow_ml.ps1`
- `tools/eta_ml/snapshot-v3.contract.json`
- `tools/eta_ml/snapshot.py`
- `tools/eta_ml/snapshot.request.example.json`
- `tools/eta_ml/snapshot_functions.reviewed.json`
- `tools/eta_ml/snapshot_schema.py`
- `tools/eta_ml/test_compare_distance_policy.py`
- `tools/eta_ml/test_coverage.py`
- `tools/eta_ml/test_diagnose.py`
- `tools/eta_ml/test_distance_sample.py`
- `tools/eta_ml/test_evidence_foundation.py`
- `tools/eta_ml/test_exporter.py`
- `tools/eta_ml/test_pipeline.py`
- `tools/eta_ml/test_snapshot.py`
- `tools/eta_ml/validation_policies.py`
