# Release GTFS-RT local03 — 2026-10-09

## Identidade e procedência

Imagem implantada, conforme confirmação do operador: `noponto-api:gtfsrt-joint-20261009-local03`.
Image ID: `sha256:adebd1705ed8f216b8598de219daa94d5905b84e74f11a6ad0b444b91e9ea942`.
TAR SHA-256: `9a552adf791bde36fdec8fbda3a3a810ef0ffa22c6fa95c36182bd4709bdbfc8`.
Tamanho da imagem: 113.735.543 bytes; TAR: 113.754.624 bytes.

O build partiu de `551b7342138e5a3f982d02ba424c82ea714e6571` com alterações locais não commitadas. Os commits de consolidação posteriores preservam esse código; não foram usados para construir a imagem implantada. Os quatro commits anteriores são 5417b70 (fontes opt-in), 50f5fc1 (documentação), 9f6b71c (origens/crosswalk/integracao local) e 551b734 (reuso de prova estrutural/performance).

Manifesto completo e TAR permanecem fora do Git, em `gps-gtfsrt-release-artifacts`, ao lado dos repositórios. Portable PDB da local02: 389 documentos analisados; cinco fontes anteriores reconstruídos e conferidos. Na local03, 383 documentos do projeto correspondem ao manifesto, sem divergências; oito documentos gerados/externos tratados separadamente. As 147 dependências resolvidas foram preservadas. A ressalva do atributo de framework gerado foi aceita; não se promete reprodução byte a byte por novo build.

SDK Docker 9.0.318; ASP.NET/.NET 9.0.20, linux/amd64. Runtime fixado por digest `sha256:0712631f86d30f5290544fd97d37bd281e44aed8ebe1ba436f2ddb1dd0d4983f`. O estágio SDK ainda usa tag mutável. `.dockerignore` exclui `.env.local.example` e `NoPonto.csproj.lscache`.

## Conteúdo consolidado

GTFS-RT BUS/BRT, parser Protobuf pinado, crosswalk por identidades externas e continuidade operacional; gate `ML:ETA:Enabled` antes de batches/HTTP; descanso de cinco segundos após excesso de cadência GTFS-RT; instrumentação de matching no polling individual/batch, com contadores BUS/BRT/UNKNOWN e categoria exclusiva `GlobalNullUnresolved`.

Cinco fontes de instrumentação alterados: GpsCicloPerformance.cs, GpsEnriquecimentoService.Batch.cs, GpsEnriquecimentoService.cs, GpsPollingService.cs e ProjecaoOperacional.cs. Novos: GpsMatchingDiagnostics.cs e GpsMatchingDiagnosticsTests.cs. O gate ETA OFF e o descanso de cadência já existiam na local02. Retry não instrumentado e warnings individuais preservados. Nenhuma migration nova, alteração de tolerância, CAS, TTL ou política de coleta nesta consolidação.

## Configuração operacional

A imagem não habilita GTFS-RT nem desliga ETA automaticamente. Defaults legados permanecem; ausência de `ML:ETA:Enabled` significa true por compatibilidade. Configuração implantada informada:

- `GpsSources__BusPrimarySource=GTFSRT_BUS`, `GpsSources__BrtPrimarySource=GTFSRT_BRT`.
- BUS/BRT habilitados, unidade `KilometresPerHour`, `CrosswalkValidated=true`, aquisição GTFS-RT 30 segundos.
- `ML__ETA__Enabled=false`, `EtaV2__Enabled=false`, `EtaV2__ShadowEnabled=false`.
- Concorrência 4/4, limite API 1 CPU / 768 MiB / 256 PIDs; polling GPS e aquisição têm cadências distintas.

Reconciliações produtivas informadas: BRT 32 rotas/64 identidades e BUS 312 rotas/624 identidades. As 38 rotas BUS restantes continuam pendentes. Isso não certifica dataset ETA/ML nem cobertura integral do banco.

## Validação e pendências

Build Docker local aprovado, cinco warnings preexistentes. Consolidação: 435 testes .NET offline aprovados, zero falhas/ignorados, com compilação; três verificações Compose locais com ambiente sintético aprovadas. Nenhum teste conectado ou acesso produtivo nesta tarefa. HTTP 200, ausência de reinícios e funcionamento GPS em produção são informações do operador.

Pendência conhecida: rejeições `timestamp_non_monotonic` nos ciclos de reutilização do cache BRT. Não corrigida nesta release; deve ser investigada e corrigida em etapa separada. Matching BRT rejeitado não é automaticamente falha de parser/crosswalk. O coletor SPPO legado permanece no código; não houve limpeza coordenada.

Não entram nesta consolidação: certificação 3G, Shadow histórico de outra branch, alterações ML, scripts/manifests de reconciliação e propostas operacionais locais, TARs, logs, caches, backups ou segredos. Os arquivos excluídos continuam preservados localmente. Documentos anteriores são históricos; este é o resumo único da local03.

## Integração e rollback

`origin/main` e main locais apontam para 70d7bcb no momento da inspeção; referência remota conhecida, sem fetch. A main é ancestral da branch, permitindo fast-forward local se não avançar. Nenhum merge/push/tag foi executado.

Workflow atual: push na main constrói e publica imagens GHCR (inclusive latest); deploy SSH requer workflow_dispatch. Publicar main altera imagens disponíveis, mesmo sem deploy direto. Não executar workflow_dispatch nesta consolidação. Um novo build de CI não substitui a identidade da imagem homologada.

Rollback operacional permanece local02, ID `sha256:5b6417e141e6c38ff03b664e238cc6327567b0493ce79ce5def9980ff0b75110`, preservando configuração e recriando somente API, sem duas APIs simultâneas ou alterações PostgreSQL/Redis/ML. O startup executa Database.Migrate(); ausência de delta em migrations foi conferida, não usar startup como auditoria de schema.

## Hashes dos insumos da candidata implantada

Hashes abaixo são dos bytes da worktree usada no build. Git pode normalizar CRLF/LF; isso não indica mudança semântica. Artefatos devem ser conferidos por Image ID/TAR, não pelo HEAD dos commits posteriores.

| Insumo | SHA-256 |
|---|---|
| .dockerignore | `0a8df2cc0165605d67bfd509802c3a2fc5f2f3055cd8dafb3bb01267ea3f02d7` |
| 2-Application/Services/GPS/GpsMatchingDiagnostics.cs | `aed0c1e7c6ccc76955eec63850fd087ab90c11a10d76864a58b0fdac231cf069` |
| 5-Testes/GpsMatchingDiagnosticsTests.cs | `10d6ffe846c681241ba28ae14e68ac1387cdf3d15c2710095d43709b22834bfe` |
| Dockerfile | `34ed2dc53aceeab7a3a9fcf3fc6b7bd11afb96af5a5ce6f8e31e8a9b34ad15dd` |
| NoPonto.csproj | `b148e84683fd0db1d9b717e36e5414e11820a2d4edad319251b92c3679dcfff1` |

Assembly SHA-256: `8285a4eb6c91f319235bb426a915f3c8b2ac16607160cc60ddb9a24ffd5d8edb`.
