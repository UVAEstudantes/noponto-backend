# 3G.2 — revisão isolada da distância operacional

**APROVADO para a nova política técnica em fixtures locais. Treinamento real/certificação: REPROVADOS, continuam bloqueados.** Não se executaram produção, SSH, banco operacional, API, alterações de coleta/migrations, treinamento real, commit/push/deploy. Snapshot/bundle/manifests/evidências anteriores preservados.

## Diferença exata

Antiga política `eta-direct-route-comparison-3a-v1`: após validar distância de rota, exigia distância persistida finita/positiva e igualdade aproximada com o trecho, max(10m,10%). Nova `eta-route-versioned-3g2-v2`: distância de treino continua exclusivamente ST_Length(ST_LineSubstring(Geometria da versão validada, posição GPS, posição do alvo operacional)::geography); a distância persistida direta ao matching é apenas diagnóstico. Não aumentar a tolerância: a comparação entre semânticas diferentes foi removida. O limite **comprimento+10m** da distância de rota permanece.

`EtaDataset.Avaliar` mantém procedência AuditadaSemProtecao/referência, journal, identidades, alvo explícito/ambos IDs coerentes, versão/linha/sentido/padrão, parada, volta, GPS REAL/modal, posições finitas [0,1], alvo adiante, comprimento positivo, distância geography finita/positiva/limitada, tempos/label, horizonte3600s e splits por viagem inteira. Nenhum cálculo do label ou feature mudou. SQL3A não foi alterado: o destino vem da próxima ocorrência explícita, a versão da ocorrência e a estrutura desse padrão; o validador confronta com versão/identidade GPS/journal. A factory/coleta não foi alterada e continua sem preservar o matching original separado quando há alvo operacional.

O objeto offline CandidatoDatasetEta confia na fonte para entregar distância realmente conferida; não pode autenticar geometria a partir de um double arbitrário. A fonte SQL/exporter é o caminho autorizado e testado. Geometria de outra versão, alvo incompatível, wrap, label/journal/procedência inválidos não ganham exceção.

## Versionamento e compatibilidade

- Features/labels/CSV/contrato de coleta continuam `noponto-eta-gps-v1`; perfil pós-fix/cutoff/sampling inalterados.
- Exporter novo `eta-export-3g2-v2` inclui `validation_policy=eta-route-versioned-3g2-v2` no manifest. Host loopback, READ ONLY/REPEATABLE READ mantidos.
- Novo `validation_policies.py` compartilhado por cópias idênticas backend/ML exige igualdade dataset/config e coerência exporter/política. Artefatos antigos sem campo são identificados como política **legada**, e só são lidos com configuração legada; não são recertificados. Fixtures sintéticas antigas continuam legadas. Config real atual declara explicitamente a nova política.
- Pipeline/model.manifest registra política; evaluate compara modelo/dataset/config antes de pickle. Qualificação real exige política corrente e exporter correspondente, conservando todos os gates3G.1/volume/evidências. Recibo e modelo preservam a política; loader HTTP verifica-a internamente, sem mudar contrato HTTP, Shadow ou ETA público.
- `diagnose.sources.json` atualizado apenas no hash do validador; SQL3A conserva hash normalizado `b2c5f0909d817fc57ba3942c9b2985e77dcfbf9bbaf9a1644dd3228aa77898be`. Relatórios antigos não foram sobrescritos nem pretendem validar código novo.
- Manifest histórico dos15arquivos3C permanece intacto. Novo `ml/migration-3g2.revision.json` registra hashes originais/atuais das quatro revisões (collection_profiles, config.real.example, pipeline, evaluate); teste exige hashes vigentes e ligação ao original. Não há atualização silenciosa das provas da migração inicial.

## Antes/depois observado

Comparação offline dos mesmos resultados PostGIS reais previamente conferidos, com bundle verificado e correspondência de GPS/passagem/journal/estrutura, sem conexão ao banco real restaurado. Novas saídas: `outputs/comparison-3g2-real-02/report.json`, `comparison.json` e SQL gerado. Report antigo/candidates/snapshot são ligados por hashes; sourcepins do validador antigo explicitamente reconhecidos na comparação.

| Motivo/componente | Antes | Depois |
|---|---:|---:|
| GPS |775|775|
| Associados e journal conferido |758|758|
| DistanciasDivergentes como descarte |401|0|
| TecnicamenteElegivelPreliminar |357|758|
| LabelAusente |17|17|
| Identidade operacional inválida |17|17|
| Demais descartes técnicos componentes |0|0|
| Certificados para treino |0|0|

Os17contadores de identidade/label referem-se aos mesmos GPS, não somar34. Os401deixaram de falhar **apenas pela comparação semântica**; não foram certificados. Ausência de proteção continua não comprovada para todas as viagens, sem splits certificados; essa falta de evidência é transversal aos775GPS. Componentes técnicos não substituem exportação canônica/auditoria externa. Critérios de volume continuam insuficientes.

Outlier linha764: ~45,04m persistidos,184,22m de rota,191,16m de direta esférica ao alvo. A nova verificação componente não usa a distância direta para veto, mas isso **não prova matching/validade física do outlier**. Matching original separado está ausente; causa individual permanece sem evidência. Não houve regra especial por UUID/linha nem certificação retrospectiva. A investigação individual e a certificação devem resolver essa lacuna antes de incluir dados em treino real.

## Testes/fixture e limites

Regressão antes da correção: três testes de distância direta com trecho válido, dois falharam exclusivamente DistanciasDivergentes, um linear/coincidente passou. Depois: **70C#** aprovados/zero ignorados (616ms), incluindo rota inválida nula/zero/negativa/NaN/infinita/acima do limite, matching incompatível, proteção, versão/alvo/tempo/journal e circular sem wrap. Teste com45m é fixture controlada, não prova do outlier real.

Python backend **26** aprovados (18,317s): diagnóstico/sourcepins, exporter/host/READ ONLY, comparação/hash/output. ML **93** aprovados (6,901s), incluindo política antiga/nova/mistura/desconhecida, evidência/qualificação/modelo e hashes; pipeline **13** aprovados (18,437s), fit apenas sintético. Build exporter passou (recompilação26,20s,5warnings preexistentes/0erros; incrementalfinal10,81s/0warnings/erros). Diffcheck nos dois repositórios e whitespace de arquivos novos conferidos.

Fixture exclusiva `outputs/postgis-3g2-03/integration.report.json`: PostgreSQL16.4/PostGIS3.4.3, linhas completa/linear/circular; export1004GPS/4viagens, splits1002/1/1, descartes identidade1/wrap1, label ausente contabilizada na descoberta, execuções fora janela/cruzando corte purgadas. Nove negativos de audit/proteção/release/fronteira/journal passam. CSV repetido tem hash idêntico; gate volume ready=false/synthetic. Consulta geográfica independente confrontou distância e label do CSV. Não acessa telemetria operacional nem certifica fixture como real.

Par controlado: direta905,79553584m/rota1280,042846926m no curvo → predicado antigo rejeita1, atual0; linear615,58741454m/615,587414545m → ambas aceitam. Antes no PostGIS é o predicado antigo aplicado às mesmas medidas, não uma segunda execução de binário antigo. Regressão C# anterior comprova o comportamento do validador original.

Tentativas01/02 falharam na inicialização/conexão da fixture, sem executar a política. Harness passou a usar tmpfs256MiB/memória512MiB/rede exclusiva com ICCoff/porta loopback e pg_isready TCP final, prazo180s conforme runner3E.1. Esses limites são apenas fixture, não timeout de polling/inferência nem infraestrutura produtiva. Todos os recursos próprios removidos no finally; nenhum banco/container existente reutilizado ou apagado. Logs/relatórios das falhas preservados.

Não executada consulta nova sobre `eta_snapshot_3b2b_real02`: arquivos do resultado anteriormente observado já permitem comparação reproduzível sem novo acesso. Para consulta conectada manual, verificar isolamento restaurado, gerar diagnóstico novo e seguir os comandos3B.2C exclusivamente locais; não re-selar bundle nem usar relatório como auditoria de proteção.

## Arquivos desta etapa

Backend: EtaDataset.cs, EtaDatasetTests.cs, exporter Program.cs, diagnose.py/sourcepins/test_diagnose, novo compare_distance_policy.py/test_compare_distance_policy, novo validation_policies.py, collection_profiles/config.real/pipeline/evaluate, runnerintegration_postgis, este relatório/documentação3A/3B/MIGRACAO/HISTORICO. ML: cópias policies/profiles/config/pipeline/evaluate, qualify_real.py/test_qualify_real, receiptguard interno eta_history_service, novo test_validation_policy, test_migration_3c/novo revisionmanifest, README. Working tree também contém alterações anteriores preservadas.

## Reprodução PowerShell

```powershell
Set-Location D:/repositorio_github/NoPonto/noponto-backend
$env:PYTHONPATH='tools/eta_ml'
dotnet build tools/EtaMl.Export/EtaMl.Export.csproj --no-restore
dotnet test NoPonto/NoPonto.csproj --no-restore --filter 'FullyQualifiedName~EtaDatasetTests|FullyQualifiedName~TelemetriaMlIdentidadeTests' --verbosity minimal
python -m unittest tools.eta_ml.test_diagnose tools.eta_ml.test_exporter tools.eta_ml.test_compare_distance_policy -q
$run=[Guid]::NewGuid().ToString('N')
python tools/eta_ml/integration_postgis.py --output "tools/eta_ml/outputs/postgis-3g2-$run"
python -m tools.eta_ml.compare_distance_policy --bundle tools/eta_ml/outputs/snapshot-3b2b-v2-real-01 --candidates tools/eta_ml/outputs/diagnostic-3b2c-copy-hotfix02/diagnostic-candidates.csv --previous-report tools/eta_ml/outputs/diagnostic-3b2c-copy-hotfix02-completo/report.json --output "tools/eta_ml/outputs/comparison-3g2-$run"
git diff --check

Set-Location D:/repositorio_github/NoPonto/ml
$env:PYTHONPATH='tools/eta_ml'
python -m unittest discover -s tests -p 'test_*.py' -q
python -m unittest discover -s tools/eta_ml -p test_pipeline.py -q
git diff --check
```

Próximo marco continua evidência de proteção/completude e volume; só então novo snapshot/exporter/qualify_real dry-run. Não treinar com os758preliminares ou promover sintético. Aprovação técnica de distância não modifica essa decisão.
