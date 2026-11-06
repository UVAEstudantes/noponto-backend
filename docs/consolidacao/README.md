# Base Git reproduzível network-27 + ETA2I — 2026-11-05

Branch chore/principal iniciada do commit documental2I `da3f065`, descendente de origin/main `8e30a5f`. Commit funcional publicado: `823057cd2a885029904429ac6bb59e9ba1d5226e`. Reconstrução feita exclusivamente em noponto-backend-principal, sem alterar checkout/WIP original.

## Composição e equivalência

1. Snapshot local network-27/source.zip com592/592SHA256 conferidos contra o manifesto original.
2. Overlay dos23arquivos C# do commit funcional823057c, incluindo serviço de eventos, estimador, worker, opções/fallback e regressões. Program.cs vem do snapshot, acrescido exatamente dos três registros DI do2I (AdaptiveEtaShadow, AdaptiveEtaShadowWorker e hosted service compartilhado).
3. Proto gtfs-realtime.proto e recurso embutido trem-estrutura-v2.json recuperados dos blobs já rastreados no próprio823057c. Não foi copiado nenhum recurso de WIP. Demais arquivos de projeto/dependência e recursos rastreados herdados dessa árvore Git; csproj do snapshot é idêntico ao herdado normalizando CRLF.
4. backend-proveniencia.json registra613entradas: origem e SHA256; git_content_sha256 normaliza CRLF. Program registra explicitamente a transformação. Todos os613conteúdos foram reconferidos após a composição.

O código runtime corresponde à receita e às fontes da publicação documentada network27+2I. Permanecem equivalentes os arquivos públicos, arbitragem/canonical store/projeções REST+SignalR, campos de ETA, seleção HybridFixed20/HybridRecentEma, fallback físico e flag HybridPublicEnabled=false para rollback. O worker continua desacoplado do polling e sem SQL adicional na requisição pública.

Não se afirma identidade binária da DLL reconstruída nem foi feita inspeção ao vivo do servidor. Configuração operacional, flags habilitadas e dependências da imagem publicada continuam externas, não versionadas; a árvore preserva os defaults seguros dos fontes. Nenhuma tentativa de reproduzir produção com main nem mudança de configuração produtiva. Sem migrations executadas.

## Escopo

Incluídos somente fontes íntegros do snapshot e overlay publicado. WIP posterior ITS/ETA, experimentos ML2E/2F/3G, ferramentas externas de laboratório e capturas/artifacts não foram importados. Código legado e suportes de diagnóstico já presentes na base publicada permanecem preservados e sujeitos às flags/opt-ins existentes; não houve nova instrumentação ou coleta. Docker, workflow, banco, Redis e credenciais inalterados.

## Validação

SDK9.0.306, net9.0, xUnit2/VSTest:
- `dotnet build NoPonto/NoPonto.csproj --no-restore -c Release`: aprovado, zero erros, seis warnings preexistentes.
- Testes direcionados executados via dotnet test --no-build com filtro dos grupos HybridEtaPublic, HybridEta, PhysicalEtaSelectedStop/Mvp, ViagemOperacionalRegra, CanonicalPhysical, PhysicalGlobal25, UnifiedLocation/Cohorts, GtfsRouteEvidence, PhysicalPresenceMvp, GpsSignalRPublisher, GpsTimestampDeduplication, EventosParadaProjection e ItsCurrentPhysicalAdapter: Passed336, Failed0, Skipped1. O ignorado é Frozen2D_BackendMatches2GReferences_AndPhysicalFieldsRemainUnchanged, que depende de captura privada ausente.
- `python .deploy/test_eta_hybrid_release.py`:3aprovados, subprocessos/health simulados; nenhum comando de deploy executado.
- HybridEtaPublicTests.PublicSelection_UsesHybrid_WithSameSqlCount_AndRollbackRestoresPhysical e RealHttpController_ReturnsHybridThroughExistingPublicContract verificam seleção/fallback/rollback, contrato público e contagem SQL.
- Resultados/logs locais em diretório irmão auditoria-radar-20261105/consolidacao e backend-principal-*.log. Não versionados.

Tentativas iniciais incluíram UnifiedLocationRedisTests (falha por conexão isolada não configurada) e VeiculosLinhaRuntimeReaderTests (falhas de conexão localhost). Não são falhas do cálculo/contrato; a rodada final explicitamente excluiu esses grupos, sem alterar seus testes nem iniciar/modificar Redis. Não foram executados serviços reais, SQL/PostGIS, replay privado, teste integral ou deploy. Um aviso whitespace em PhysicalGlobal25Tests já existe no snapshot e foi mantido para preservar os hashes de proveniência.

Main e branches antigas preservadas. Notificações não implementadas. chore/principal e feat/radar-notificacoes são enviadas sem merge main; workflow de deploy existente só atende push main.
