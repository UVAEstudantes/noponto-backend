# Backend MVP isolado — 2026-10-08

Base main: `70d7bcb8041e167df063237b4fdcafd4971b01d6`.
Checkpoint preservado: `27e1aec0dd4c59ef27ca0d7bcbe3e6ec329e54bb`, EXPERIMENTAL — NÃO IMPLANTAR.
Branch MVP: `feature/eta-ml-mvp`, worktree `noponto-backend-eta-ml-mvp`.

## Escopo e compatibilidade

Seleção do PLANO_ISOLAMENTO_MVP.md aprovado. Arquivos completos abaixo preservam bytes/hashes do checkpoint; Program.cs e GpsPollingService.cs foram reconstruídos sobre main com somente registro, campo, parâmetro opcional e captura causal do Shadow histórico. O cliente público e ML:BASE_URL permanecem na base. Shadow Enabled=false por padrão; nenhuma configuração operacional foi modificada.

Política `eta-route-versioned-3g2-v2`: distância operacional geography na geometria versionada, distância direta persistida somente diagnóstico. Contrato `noponto-eta-gps-v1`, labels, features, splits e gates mantidos. Catálogo único collection_profiles.json reconhece historical-pre-fix e official-post-fix; combinações misturadas são rejeitadas. Auxiliares Python permanecem compatibilidade legada; treino/inferência autoritativos estão no repositório ML, não modificado nesta execução. integration_postgis.py é necessário ao teste offline de metadados do exporter; seu harness conectado não foi executado.

Migration EtaTripEvidenceFoundation, coverage/ingress, repositories de evidência, fencing, ACLs, hooks de Retry/Outbox e certificação não foram transportados. Migrations e repositories operacionais permanecem na base. Ferramentas snapshot/diagnóstico opcionais ficaram fora.

FECHAMENTO_MVP_ETA_ML.md e MIGRACAO_3C.md são documentos históricos: referências ao working tree experimental e à imagem ML não descrevem código de certificação desta branch. Esta separação não reconstrói nem valida novamente a imagem instalada.

## Inventário transportado e hashes SHA-256

- `NoPonto/2-Application/Services/GPS/EtaDataset.cs` — `ff5e6c92d9436b0b53139743305050f113a67edcbb10e8d03446e007cbc8f028` (checkpoint-exact).
- `NoPonto/2-Application/Services/GPS/GpsPollingService.cs` — `04e8e2d4540b2379b8caf5d205983ef5a9c6bf742c9456d001fed9f489cf57a9` (shadow-hunks).
- `NoPonto/5-Testes/EtaDatasetTests.cs` — `94ee55e3884bd205807927cd6df890a6fa8929e2b6496e56cf71ad0e1c375f92` (checkpoint-exact).
- `NoPonto/Program.cs` — `f9259784af8e504729dc3d46d99d3a8e746b27953ffea49661040aa04bf6b5a0` (shadow-hunks).
- `NoPonto/docs/ETA_ML_DATASET_3A.md` — `eac1769e3391628607c3e62bc511442d8348642a90c5559e933e0754ebfb1ac3` (checkpoint-exact).
- `tools/EtaMl.Export/EtaMl.Export.csproj` — `a9a1f165770b556da85ac0953cfed181de32bdd7fadb0dfa8f86cb908499319c` (checkpoint-exact).
- `tools/EtaMl.Export/Program.cs` — `8786f511bf93281027a8be932d29c1e0559a506b9332eeb64ee629776a0bebe7` (checkpoint-exact).
- `tools/EtaMl.Export/audit.example.json` — `9884795e256e06d9e3502c94e0deb024b18907d44ada1b6f9a7987d9ee93d4c4` (checkpoint-exact).
- `tools/EtaMl.Export/options.example.json` — `b988b1f5c16c48fcfcd6db063da03bb621368429a772fa138b6272ebe8eb181b` (checkpoint-exact).
- `tools/eta_ml/config.real.example.json` — `019602ceb482dfdecfed20b3650ab2a539f017e9446f3beafa8ee26c4b44b263` (checkpoint-exact).
- `tools/eta_ml/evaluate.py` — `94a0bf023a02e6a5b661713f8569f9d4a54e7e22efb0eb8c706e3498305e2c3a` (checkpoint-exact).
- `tools/eta_ml/integration_postgis.py` — `edc312be4fec563c61e99687c9965f670b523314aacb70104352324b0430585b` (checkpoint-exact).
- `tools/eta_ml/pipeline.py` — `9afb94974d509d468158ea64574ec2cc0e64a30d34424f3fb09919003ea4ad81` (checkpoint-exact).
- `tools/eta_ml/test_exporter.py` — `4e9e84079eeb10186b45fe745fdae410edb8e0658406f530c23c5fdd3df63e30` (checkpoint-exact).
- `tools/eta_ml/test_pipeline.py` — `353697526822f4497b9e97a7b4058a68c88591b96225b53004739f6e7a932726` (checkpoint-exact).
- `NoPonto/2-Application/Services/GPS/HistoricalEtaOperationalStatistics.cs` — `c800530b61decbbf8f56afc994a227360e2b78a70275f25da96dee52f61f301b` (checkpoint-exact).
- `NoPonto/2-Application/Services/GPS/HistoricalEtaShadow.cs` — `38b1278f82039424cc3a7fa8e013954f6cbacd1c6c73e815315bc1b1ec25010e` (checkpoint-exact).
- `NoPonto/5-Testes/HistoricalEtaShadowLocalTests.cs` — `a4ee3b28c4c21a68c21d004da8a1b777e048f6b77554c5dfad09d067a556696b` (checkpoint-exact).
- `NoPonto/5-Testes/HistoricalEtaShadowTests.cs` — `61ed7c3e30c287dee4fb8fd8bfd78430de04c5469b1fea548aad170ac0f8ffff` (checkpoint-exact).
- `tools/EtaMl.Export/audit.historical.example.json` — `fa2d7498510feec1566a9fc45b53075955d40c8fc761073c97d36beb4da55ba7` (checkpoint-exact).
- `tools/EtaMl.Export/options.historical.example.json` — `da23919e07b94795e558dd8e051ca1ba6dba30b76fe800d407970bd1e4287952` (checkpoint-exact).
- `tools/collection_profiles.json` — `1dcd1a0a3be9ac200f9ce189194eef340f7191eaea723dc373c989ed83f8573c` (checkpoint-exact).
- `tools/eta_ml/FECHAMENTO_MVP_ETA_ML.md` — `ab79ebc0581b38bd781695be977ebe057f6cd544adfd465695272b89bf637457` (checkpoint-exact).
- `tools/eta_ml/HOMOLOGACAO_SHADOW_3E_1.md` — `55d9aedaeab75416fa8db1b010b2ff7d0f4b0424b9ceab10a837b4dd56491813` (checkpoint-exact).
- `tools/eta_ml/MIGRACAO_3C.md` — `451c81b39170af44b1f8ad81f09bd86912aac8b84a9cab93d945b25cfdbbc0c0` (checkpoint-exact).
- `tools/eta_ml/OBSERVABILIDADE_SHADOW_3F.md` — `e1d5558e43f8a902369b45ef34c434ae41c22e2935e82312e60597f071a7d5e8` (checkpoint-exact).
- `tools/eta_ml/SHADOW_HISTORICO_3E.md` — `7179e8626d928c8a7ef6c593a04b2368e89e1d6ecf0efb63598bda1b4ef10336` (checkpoint-exact).
- `tools/eta_ml/collection_profiles.py` — `6c037d1774979e798b49d3250535dfb37406b95f8870ced747ff4b9fb9d67e24` (checkpoint-exact).
- `tools/eta_ml/config.historical.example.json` — `7367e10fbf16ff1271cbd6fc0085ea76d4cf6cc7222a1460a52f1189b90f7e20` (checkpoint-exact).
- `tools/eta_ml/homologate_shadow.ps1` — `1ff359589a1493a1dd0c4e19b090932c5fb223fc55c190877558c58b476d1bcc` (checkpoint-exact).
- `tools/eta_ml/profile_shadow_startup.ps1` — `dfdd2af99d33e49187b2f6b2b708a7b72e9f9282beacbacc7bef516c4c3e4bfa` (checkpoint-exact).
- `tools/eta_ml/smoke_shadow_ml.ps1` — `f617db27186c1144a9e124f3d592765d0dd83b8ccad9d2656ac0852cb68c9ec1` (checkpoint-exact).
- `tools/eta_ml/validation_policies.py` — `734d621d88e950639c84d55a3c9cbaf9a185deaf564f2aca0848756d32ab039b` (checkpoint-exact).

## Reprodução offline

Na worktree MVP, PowerShell:

```powershell
dotnet restore tools/EtaMl.Export/EtaMl.Export.csproj --ignore-failed-sources
dotnet build tools/EtaMl.Export/EtaMl.Export.csproj --no-restore
dotnet test NoPonto/NoPonto.csproj --no-build --no-restore --filter "FullyQualifiedName~EtaDatasetTests|FullyQualifiedName~TelemetriaMlIdentidadeTests|FullyQualifiedName~HistoricalEtaShadowTests|FullyQualifiedName~GpsPollingFontesTests|FullyQualifiedName~GpsPollingCadenciaTests"
$env:PYTHONPATH = 'tools/eta_ml'
python -m unittest discover -s tools/eta_ml -p test_pipeline.py -q
python -m unittest discover -s tools/eta_ml -p test_exporter.py -q
git diff --check
```

Não iniciar a API: Database.Migrate() permanece no bootstrap da base. Homologação conectada/Docker e deploy precisam de execução separadamente autorizada, com fixture exclusiva. Não há modelo real certificado, certificação retrospectiva, treino real ou autorização de habilitar Shadow. Resultados desta execução serão registrados após os gates.

## Validação executada nesta worktree

Restore de dependências e build conjunto backend/exporter aprovados: zero erros, cinco warnings preexistentes (tarifas, ponto de entrada do test SDK e Assert.Single). Sem iniciar API ou executar migrations.

115 testes C# aprovados, zero falhas/ignorados: dataset, identidade de telemetria, Shadow histórico, fontes e cadência do polling. 13 testes Python de pipeline aprovados (20,264 s), exclusivamente fixtures sintéticas; 15 guards do exporter aprovados (9,491 s), incluindo perfil atual/histórico, misturas rejeitadas, host remoto bloqueado antes da conexão e proteção READ ONLY. Não houve treino real ou teste conectado.

Conferidos 77 arquivos protegidos da base, incluindo todos os migrations/repositories; sem novas migrations ou referências C# à certificação experimental. Os 31 arquivos completos transportados mantêm bytes idênticos ao checkpoint. Dois arquivos receberam exclusivamente hunks Shadow. Hashes acima correspondem aos arquivos efetivamente transportados. JSONs, inventário explícito, whitespace e padrões fortes de segredo verificados; nenhuma credencial/dado real/output/modelo incluído. Inspeção estática de segredos não garante detecção universal.

Builds/caches locais são ignorados, não descartados. git diff --check aprovado; conferência do índice usa cr-at-eol para preservar os bytes CRLF já hashados. Código ML, main e checkpoint experimental permanecem intactos.

Decisão: PRONTA PARA FUTURA HOMOLOGAÇÃO LOCAL. Testes conectados de PostGIS/Docker não foram repetidos nesta separação. A API mantém Database.Migrate() da base: startup exige homologação e autorização próprias. Nenhuma autorização de deploy, certificação ou habilitação Shadow decorre destes commits.
