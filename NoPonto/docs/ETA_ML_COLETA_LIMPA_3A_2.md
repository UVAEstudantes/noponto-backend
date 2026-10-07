# Etapa 3A.2 — release e início da coleta limpa ETA/ML

Preparação de 07/10/2026, sem execução de produção. **Próximo passo: checkpoint reproduzível, ensaio mínimo e rollout coordenado; começar coleta prospectiva após health-check, mantendo sampling existente.** Não recuperar histórico antigo para treino e não criar arquitetura nova. Nenhum timestamp oficial de coleta foi escolhido nesta execução. Nenhum dado apagado.

Resultado manual INFORMADO PELO USUÁRIO: EtaDatasetPostgresTests após correção3A.1, **1 aprovado/0 falhas** em PostgreSQL16/PostGIS3.4+Redis7 descartáveis. Isso aprova o cenário da consulta/journal/geography/paginação, não certifica toda a procedência do dataset ou capacidade em4GiB. Resultados anteriores132 locais/124 reais consolidados têm escopos/sobreposições diferentes; não somar como casos únicos.

## 1. Checkpoint reproduzível: inventário do workspace

HEAD de referência: `fcad913cf6fa36a9949c5e25614cd4f13ddb39cf`. **Esse commit sozinho não representa a versão estabilizada:**22 arquivos rastreados modificados e23 untracked preexistentes no início desta etapa. Necessário preservar baseline completa do repositório, csproj/configurações sem segredos, todas as alterações abaixo e novos arquivos. Não implantar checkout de HEAD sem a extensão/migration/DI.

Arquivos rastreados modificados necessários ao conjunto compilado, caminhos relativos a NoPonto:

```text
2-Application/DTOs/PosicaoApiDto.cs
2-Application/Services/GPS/EtaV2Shadow.cs
2-Application/Services/GPS/GpsEnriquecimentoService.cs
2-Application/Services/GPS/GpsPollingService.cs
2-Application/Services/GPS/GpsPoolingOptions.cs
2-Application/Services/GPS/ProjecaoOperacional.cs
2-Application/Services/GPS/TelemetriaMl.cs
2-Application/Services/GPS/ViagemObservadaService.cs
2-Application/Services/GPS/ViagemObservadaState.cs
2-Application/Services/GPS/ViagemOperacional.cs
4-Data/Interfaces/IViagemObservadaRepository.cs
4-Data/Repositories/HistoricoEventoRepository.cs
4-Data/Repositories/ViagemOperacionalCodec.cs
4-Data/Repositories/ViagemOperacionalRepository.cs
Program.cs
5-Testes/EtaV2FoundationTests.cs
5-Testes/GpsBrtTelemetriaTests.cs
5-Testes/TelemetriaMlTests.cs
5-Testes/ViagemObservadaServiceTests.cs
5-Testes/ViagemOperacionalFixture.cs
5-Testes/ViagemOperacionalIntegracaoTests.cs
```

Outro rastreado modificado: `.gitignore` (não necessário ao binário, relevante ao checkpoint). Não excluir mudanças anteriores por aparentarem ser de testes: csproj inclui5-Testes na compilação atual. Não modificar separação de projetos nesta etapa.

Novos arquivos compilados necessários ao checkpoint:

```text
2-Application/Services/GPS/EtaDataset.cs
2-Application/Services/GPS/RetryOperacionalGps.cs
2-Application/Services/GPS/ViagemOperacional.Integridade.cs
2-Application/Services/GPS/ViagemOperacional.Mudanca.cs
4-Data/Repositories/PendenciaOperacionalGpsRepository.cs
4-Data/Repositories/ViagemOperacionalRepository.Integridade.cs
Migrations/20261006180000_IntegridadeCircularDuravel.cs
5-Testes/CancelamentoCandidatoCursorTests.cs
5-Testes/EtaDatasetPostgresTests.cs
5-Testes/EtaDatasetTests.cs
5-Testes/IntegridadeCircularPostgresTests.cs
5-Testes/IntegridadeCircularRegraTests.cs
5-Testes/MudancaOperacionalIntegracaoLogicaTests.cs
5-Testes/MudancaOperacionalPontaAPontaTests.cs
5-Testes/MudancaOperacionalRegraTests.cs
5-Testes/RetryOperacionalGpsRedisIntegracaoTests.cs
5-Testes/RetryOperacionalGpsTests.cs
5-Testes/TelemetriaMlIdentidadeTests.cs
5-Testes/ViagemOperacionalCodecCompatibilidadeTests.cs
5-Testes/WrapDuranteCandidatoIntegracaoTests.cs
5-Testes/WrapDuranteCandidatoTests.cs
```

Outros untracked preexistentes: AGENTS.md na raiz e HISTORICO.md. Documentais, preservar no checkpoint, não precisam do binário. Migration nova tem atributos DbContext/Migration e entra por compilação; não omitir porque ainda está untracked.

Documentos/SQL locais existentes, **ignorados pelo padrão docs/** em NoPonto/.gitignore e invisíveis no status normal:

```text
NoPonto/docs/AUDITORIA_2A_2N.md
NoPonto/docs/RECUPERACAO_GPS_2A_2O.md
NoPonto/docs/RETRY_OPERACIONAL_2A_2O_1.md
NoPonto/docs/ENCERRAMENTO_OPERACIONAL_2A_2P.md
NoPonto/docs/ETA_ML_DATASET_3A.md
NoPonto/docs/ETA_ML_CANDIDATOS_3A.sql
NoPonto/docs/rail-schedule-architecture.md
```

Necessários como documentação/evidência/offline, não ao startup da coleta. SQL atualmente só encontrado em docs; LocalizarConsulta do teste procura ETA_ML_CANDIDATOS_3A.sql na raiz do projeto/ancestrais. O resultado manual1/0 se refere à rodada informada, não elimina esse requisito de localização para nova reprodução. No checkpoint offline, incluir cópia do SQL no caminho esperado pelo teste, sem mover/apagar o original; alternativa futura é ajustar helper só de teste. Isso não impede startup da coleta. Nenhum arquivo movido/copiado nesta etapa.

Proposta: usuário decide commit/checkpoint depois. Até lá, artefato de staging isolado da fonte com lista explícita de arquivos, SHA256 de cada input público/artefato publicado, versão SDK/dependências, baseline HEAD, relatório dos testes e configuração efetiva redigida. Excluir .env, credenciais, bin/obj do snapshot de fonte, caches/lab-output; nunca depender de git archive HEAD para capturar dirty/untracked/ignored. Identificador da release = ID de build + hash do artefato, não apenas HEAD. Nada arquivado/commitado nesta execução.

## 2. Coleta ML e sampling real

GpsPollingService.ConfirmarPosicaoAsync: depois do CAS GPS Accepted e processamento operacional, chama ShouldCollect(posicao, DateTimeOffset.UtcNow), forma EventoTelemetriaMl, TryWrite publisher. Sampling independe de obter identidade de viagem: posição sem associação pode persistir com viagem/volta/alvo operacional NULL; saúde do pipeline não equivale a exemplo elegível.

Publisher → channel limitado10.000 → stream `noponto:ml:telemetria`. Worker registrado no Program → grupo `telemetria-ml-postgres` → validação/lote100 → TelemetriaMlRepository PostgreSQL → ACK depois do commit. IDs repetidos ON CONFLICT DO NOTHING. Erros inválidos/exauridos vão à DLQ `noponto:ml:telemetria:dead-letter`; tentativas têm chaves `noponto:ml:telemetria:tentativas:{stream_id}` e sufixo`:erro`. Retention/backpressure já existentes; retry operacional NÃO republica telemetria.

Configuração: seção TelemetriaMlSampling, IOptionsMonitor, algoritmo v1, Seed NOPONTO_ML_V1, LinePercentage10, BlockMinutes60. Hash SHA256 de algoritmo/seed/blocoUTC/modal normalizado/código de linha normalizado. **Unidade selecionada: linha/modal no bloco**, todos seus veículos/observações elegíveis; bloco calculado pelo relógio do processamento, não GPS. Não Bernoulli por GPS nem10% exatos de linhas/veículos/registros em janela curta. Variação de frota por linha distorce proporção de registros. Enabled=false ou taxa100 coleta tudo; taxa0 exclui identidades válidas; identidade ausente/erro faz fail-open e coleta, com métricas.

**Divergência de defaults versus decisão10%:** appsettings.json e docker-compose.yml149–152 têm Enabled=false, Percentage10, Block60, Seed padrão. Ambiente de destino pode sobrescrever por env/.env/configuração; não acessado. Se a coleta atual é10%, confirmar override efetivo `TelemetriaMlSampling__Enabled=true` + `LinePercentage=10`, sem trocar seed/bloco. Não modificar defaults nem ambiente nesta execução. Se destino estiver false, NÃO alegar10%; resolver configuração real no processo de rollout autorizado antes de declarar health aprovado. Só o percentual10 sem Enabled=true não basta.

Riscos de parar/reduzir persistência com mapa ainda funcionando: sampling/erro de configuração, nenhuma linha selecionada no bloco, channel cheio, backpressure, Redis/PG indisponível, consumidor parado/validação com DLQ. Métricas Telemetria ML a cada rodada do reporter incluem candidates/selected/skipped/fail_open, enabled/percentage/bloco, produzidos/persistidos/duplicados/retries/DLQ, backlog/drops. Usar deltas da mesma instância; restart zera contadores locais. Não inferir persistência só por HTTP200/mapa.

Campos prospectivos: GPS/recepção/evento timestamps, origemREAL, modal/provedor/ordem/código, linha/sentido/versão, ocorrência observacional e próxima operacional, viagem/volta quando confiáveis, posição/comprimento/distância, velocidades instantânea/média causal. PadraoId/ParadaId/Topologia/posição destino vêm das relações da versão; passagem/journal fornecem label. Coordenadas projetadas não são preenchidas pela factory. Sem os IDs completos, excluir do dataset, não preencher por veículo/código.

## 3. Manifesto: preencher depois do rollout, nunca retroativamente

Modelo documental, não arquivo de produção nem configuração alterada:

```json
{
  "dataset_schema": "noponto-eta-gps-v1",
  "collection_id": null,
  "build_id": null,
  "artifact_sha256": null,
  "source_manifest_sha256": null,
  "base_commit": "fcad913cf6fa36a9949c5e25614cd4f13ddb39cf",
  "installed_migrations": null,
  "schema_verification": null,
  "collection_started_at_utc": null,
  "health_check_evidence": null,
  "modals": ["ONIBUS", "BRT"],
  "gps_sources_effective": null,
  "sampling_effective": {"enabled": null,"line_percentage": null,"block_minutes": null,"algorithm": "v1","seed": null},
  "structural_versions": null,
  "gtfs_import_version": null,
  "operational_flags_effective": null,
  "eligibility": "execucao iniciada depois do marco, identidade completa, journal compativel, mesma volta/alvo adiante, qualidade auditada, sem intervalo protegido",
  "legacy_history_excluded": true
}
```

Marcar início UTC somente após confirmar artefato/schema/health e linhas novas realmente persistidas. Pode usar relógio PG UTC no health final, registrar literalmente resultado real e sincronização de clocks. Não preencher com horário desta auditoria, data de commit ou primeiro registro antigo. Guardar manifesto fora de secrets e manter imutável; mudança de versão/fontes/estrutura/sampling/flags abre segmento novo de coleta.

Fontes padrão GpsSources: BusPrimarySource ZIRIX_DIRECT, BrtPrimarySource BRT_CURRENT, sombras DATARIO; registrar valores **efetivamente resolvidos**, não assumir que registro DI/HTTP disponível significa fonte habilitada. ONIBUS/BRT são modais persistidos desejados; consultar valores reais na tabela, pois rótulo BUS do resolver não é automaticamente modal persistido. Registrar versões por padrão/linha, hash estrutural, importação GTFS quando aplicável; não inventar uma versão única global.

**Não entram viagens iniciadas antes do marco**, mesmo que todos seus GPS sejam novos. Conferir evento ViagemIniciada do journal com instante >=marco e identidade compatível; exigir execução completa/fechada para split, observação e passagem na mesma execução. Viagens antigas podem continuar dias: não resetar estado/cursor nem gerar fim artificial para acelerar dataset. Esperar novas execuções naturais e monitorar quantas já começaram depois do marco.

Coleta limpa é corte prospectivo, não certificação automática. Permanecem requisitos3A de ausência de proteção e procedência. Inicialmente usar execuções lineares completas verificadas; armazenar dados circulares também, mas admitir no dataset somente quando intervalo for auditável. Campos NULL na proteção preservam integridade com menor coverage. Nenhuma nova tabela/qualidade retroativa.

## 4. Dados descartáveis e dados preservados

| Objeto | Papel | Política de limpeza |
|---|---|---|
| TelemetriasVeiculoMl | Histórico exclusivo de observações ML no modelo/repository inspecionado | Candidato a purge antigo APENAS depois de provar dependências reais e preservar novos registros; corte já basta para excluir do treino |
| Stream ML, DLQ, chaves tentativas/erro | Transporte/diagnóstico exclusivo ML | Não DEL/FLUSH/resetar grupo: pode destruir pending/ACK/retries novos; preferir retention existente |
| PrevisoesEtaV2 | Previsões/ground truth do ETA shadow, consumidor operacional ainda fecha/expira registros | Não necessária à limpeza oficial GPS; preservar pendentes e adiar purge até auditar consumidores/estatísticas |
| HistoricoPassagens / EventosViagem / OutboxViagens | Chegadas/journal/entrega operacional | NUNCA limpar por motivo “começar ML do zero” |
| ViagensOperacionais / IntegridadeCircular / Redis de operação/GPS/retry operacional | Estado/identidade/continuidade/mapa | Preservar integralmente; NÃO apagar extensão/cursor/volta/watermark/fila |
| Linhas/Sentidos/PadroesOperacionais/PadroesVersoes/Ocorrencias/Paradas/importações | Estrutura e identidade espacial | Preservar versões necessárias ao histórico/coleta |

Modelo EF inspecionado não aponta FKs entrantes para TelemetriasVeiculoMl, mas integrações externas/produção não foram inspecionadas. Exclusivamente ML no código NÃO comprova ausência de consumidor externo. **Purge ainda condicional; não autorizado como operação a executar agora.** Não é gate para começar coleta.

Consultas futuras READ ONLY, ambiente/banco/schema previamente identificados e parâmetro `:marco` real pós-health (psql variável, sem interpolação de credenciais):

```sql
BEGIN READ ONLY;
SELECT current_database(),current_schema(),inet_server_addr(),inet_server_port();
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
SELECT column_name,data_type FROM information_schema.columns
WHERE table_schema=current_schema() AND table_name='ViagensOperacionais' AND column_name='IntegridadeCircular';
SELECT "Modal",count(*),min("TimestampGps"),max("TimestampGps"),max("RecebidoEmUtc")
FROM "TelemetriasVeiculoMl" GROUP BY "Modal";
SELECT count(*) FILTER(WHERE "TimestampGps"<:'marco'::timestamptz) AS antigos,
       count(*) FILTER(WHERE "TimestampGps">=:'marco'::timestamptz) AS novos,
       count(*) FILTER(WHERE "TimestampGps">=:'marco'::timestamptz AND "ViagemId" IS NOT NULL
         AND "Volta" IS NOT NULL AND "ProximaOcorrenciaParadaPadraoId" IS NOT NULL) AS novos_com_identidade
FROM "TelemetriasVeiculoMl";
SELECT pg_size_pretty(pg_total_relation_size('"TelemetriasVeiculoMl"')) AS bytes_com_indices;
SELECT conrelid::regclass AS origem,confrelid::regclass AS destino,conname,pg_get_constraintdef(oid)
FROM pg_constraint WHERE contype='f' AND
 (conrelid='"TelemetriasVeiculoMl"'::regclass OR confrelid='"TelemetriasVeiculoMl"'::regclass);
SELECT schemaname,viewname FROM pg_views WHERE definition ILIKE '%TelemetriasVeiculoMl%';
SELECT schemaname,matviewname FROM pg_matviews WHERE definition ILIKE '%TelemetriasVeiculoMl%';
COMMIT;
```

Catálogo não detecta todos os consumidores externos/functions/dynamic SQL: revisar serviço ML, jobs, grants/owners e política de backup com operador. Varredura total da tabela pode custar I/O; executar uma vez em janela adequada e consultas de saúde seguintes por intervalo/linha usando índices existentes. Não usar EXPLAIN ANALYZE em produção para descobrir custo.

**Proposta de purge futuro, NÃO EXECUTADA, só após auditoria/decisão final:** batches pequenos, sem CASCADE/TRUNCATE, inicialmente ONIBUS/BRT e ambos TimestampGps/RecebidoEmUtc anteriores ao marco; preservar late arrivals recebidos depois do marco até revisão. Confirmar cobertura nova e backup/export opcional antes. SQL abaixo é modelo para revisão; COMMIT deliberadamente ausente:

```sql
BEGIN;
SET LOCAL lock_timeout='2s';
SET LOCAL statement_timeout='5s';
WITH antigos AS (
  SELECT "Id" FROM "TelemetriasVeiculoMl"
  WHERE "Modal" IN ('ONIBUS','BRT')
    AND "TimestampGps"<:'marco'::timestamptz AND "RecebidoEmUtc"<:'marco'::timestamptz
  ORDER BY "TimestampGps","Id" LIMIT 1000 FOR UPDATE SKIP LOCKED
)
DELETE FROM "TelemetriasVeiculoMl" t USING antigos a WHERE t."Id"=a."Id" RETURNING t."Id";
ROLLBACK;
```

Não executar o modelo acima nesta preparação nem como simples ensaio em produção: mesmo rollback escreve WAL/locks. Em futura execução autorizada, operador decide commit, cadência e repetição após conferir contagens/marco real/dependências.1000/2s/5s são limites propostos de manutenção, não benchmark. Não VACUUM FULL/reindex por padrão; DELETE não devolve imediatamente tamanho ao SO. Se prova de exclusividade/consumidores faltar, **não limpar**, só filtrar pelo manifesto.

Não preparar remoção cega de streams: ID Redis é instante de enqueue, não GPS. XTRIM por horário não prova que entrada não seja nova/pendente e grupo pode ter PEL. Usar XINFO/XPENDING/DLQ length e deixar retention existente trabalhar. Não usar XGROUP DESTROY, XACK manual, XDEL arbitrário, DEL prefixado, FLUSHDB ou volumes.

## 5. Ensaio mínimo e comandos de saúde — TODOS NÃO EXECUTADOS

Homologação descartável, mesmo artefato/schema/config efetiva e10% desejados, sem fonte de produção. Rodada curta: aquecimento2min + observação10min com volume/cadência alvo do TCC declarados, linear+circular confiável+um cenário de proteção/retry já coberto por fixture. Não chamar124 testes de benchmark. Se não houver replay/harness contínuo, registrar preparação faltante, não alegar carga sustentada. Nenhum container/serviço iniciado nesta etapa.

Operador fornece nomes de containers **já existentes**, banco/usuário exclusivos do teste e URI autorizada. Não usar docker compose config/inspect Env completo porque pode imprimir credenciais.

```powershell
$backendTeste='SUBSTITUIR_BACKEND_DESCARTAVEL'
$pgTeste='SUBSTITUIR_POSTGRES_DESCARTAVEL'
$redisTeste='SUBSTITUIR_REDIS_DESCARTAVEL'
$uriTeste='SUBSTITUIR_URI_LOCAL_AUTORIZADA'
Invoke-WebRequest -Uri "$uriTeste/" -Method Get
docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}} {{.BlockIO}}' $backendTeste $pgTeste $redisTeste
docker logs --since 10m $backendTeste 2>&1 | Select-String 'Performance GPS|Telemetria ML:|ML Sampling Block|Outbox batch|Retry operacional|Pendência|protegida|recuperada|Falha|invalido'
docker exec $redisTeste redis-cli PING
docker exec $redisTeste redis-cli XINFO GROUPS noponto:ml:telemetria
docker exec $redisTeste redis-cli XINFO CONSUMERS noponto:ml:telemetria telemetria-ml-postgres
docker exec $redisTeste redis-cli XPENDING noponto:ml:telemetria telemetria-ml-postgres
docker exec $redisTeste redis-cli XLEN noponto:ml:telemetria
docker exec $redisTeste redis-cli XLEN noponto:ml:telemetria:dead-letter
docker exec $redisTeste redis-cli HLEN noponto:gps:retry:dados
docker exec $redisTeste redis-cli ZCARD noponto:gps:retry:prazos
docker exec $redisTeste redis-cli INFO memory
docker exec $redisTeste redis-cli INFO stats
docker exec $redisTeste redis-cli INFO commandstats
```

Grupo pode não existir antes do primeiro worker; erro nessa hora não é confirmação de saúde. XPENDING deve drenar normalmente; DLQ aumenta quando inválidos/falhas exauridas. Não despejar payloads/DLQ em relatório. INFO e comandos do observador entram nos deltas. Reportar p50/p95 de total_ms e start_to_start_ms dos logs, recursos a cada5–10s e snapshots Redis/PG antes/depois. `total_ms` começa após retry inicial: subestima trabalho de recuperação; start_to_start inclui delay. Não inventar duração pura do retry a partir desses números. Usar logs de IDs para tempo de recuperação/descarte e verificar pressão da cadência.

SQL de snapshots adicionais READ ONLY, mesma conexão identificada/schema correto:

```sql
SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"') AS utc_observado;
SELECT pg_current_wal_lsn(),wal_records,wal_fpi,wal_bytes FROM pg_stat_wal;
SELECT datname,xact_commit,xact_rollback,tup_inserted,tup_updated,deadlocks
FROM pg_stat_database WHERE datname=current_database();
SELECT relname,n_tup_ins,n_tup_upd,n_dead_tup,pg_total_relation_size(relid) AS bytes
FROM pg_stat_user_tables WHERE relname IN ('ViagensOperacionais','OutboxViagens','TelemetriasVeiculoMl','HistoricoPassagens');
SELECT count(*) AS backlog,min("CriadoEmUtc") AS mais_antigo,max("Tentativas") AS tentativas
FROM "OutboxViagens" WHERE "ProcessadoEmUtc" IS NULL;
SELECT count(*) AS inicios_depois_marco FROM "EventosViagem"
WHERE "Tipo"='ViagemIniciada' AND "TimestampEvento">=:'marco'::timestamptz;
SELECT "CodigoLinha",count(*) AS gps_novos,max("TimestampGps") AS ultimo_gps
FROM "TelemetriasVeiculoMl" WHERE "TimestampGps">=:'marco'::timestamptz GROUP BY "CodigoLinha";
```

Em pós-deploy real, adaptar esses comandos manualmente pelo acesso aprovado, sem SSH pelo agente. GET `/` apenas liveness (Hello World), não probe profundo de PG/Redis/GPS. Não encontrado health endpoint profundo registrado; AdminMlController está excluído da compilação. Cruzar liveness com logs/novos rows/check schema/consumer/outbox, nunca usar200 sozinho para definir marco.

Viagens avançando: comparar amostra de mesmas Ordens, Versao/AtualizadoEmUtc e fase/posição do snapshot em duas observações; viagens lineares têm checkpoints e não escrevem PG por GPS, portanto não exigir AtualizadoEmUtc por ciclo. Circular tem leitura autoritativa/âncora e pode gerar commit por observação: medir WAL/tuplas/pool. Flag false não retira proteção já persistida.

Sampling: conferir `ml_sampling_enabled`, percentage10, block60 e candidates/selected/skipped; comparar múltiplos blocos/linhas, sem exigir exatamente10% dos GPS. Depois, comparar deltas selected/produzidos/publicados/persistidos + backlog/DLQ/drop; selected não é persistidos. Se a janela tiver nenhuma linha selecionada, esperar bloco seguinte ou conferir outra linha elegível, sem subir sampling para fabricar health. Confirmar modais/fontes reais e pelo menos persistência nova que corresponda a observações reais elegíveis.

Gate mínimo sem números inventados: sem OOM/swap/CPU sustentada no limite; cadência não deteriora progressivamente; outbox/ML não crescem continuamente em condições normais e drenam após falha transitória; sem conflitos de payload/identidade ou perdas inexplicadas; WAL/disco comportam volume alvo/retention observados. Medir, registrar e decidir com margem real da máquina4GiB. Ausência de benchmark não impede preparação, mas não comprova capacidade.

## 6. Rollout coordenado — ordem manual futura

1. Identificar todos os escritores/materializadores e configuração efetiva sem secrets. Interromper ingresso/escritores incompatíveis; drenar consumidores antigos enquanto só existem payloads antigos, depois pará-los. Não misturar versões para manter zero downtime.
2. Conferir backup/snapshot recuperável, hashes do novo artefato e artefato de retorno **compatível com integridade/motivo**; preservar Redis/PG/outbox/journal/estrutura. Não contar rollback para HEAD antigo.
3. Comparar __EFMigrationsHistory com migrations do artefato. Nova untracked necessária:20261006180000_IntegridadeCircularDuravel (JSONB NULL + CHECK). Outras migrations da baseline podem estar pendentes, não conhecido sem banco real; não aplicar só a última cegamente. Aplicar schema revisado em janela com writers parados. Não incluído comando destrutivo/cópia de conexão nesta preparação.
4. Subir consumers compatíveis antes dos novos writers quando a topologia permitir. **Implementação atual é monolítica:** Program registra consumers/backend juntos; não existe flag geral para subir só outbox/ML. Se mesma unidade, iniciar uma única versão compatível após schema e deixar consumers iniciarem junto antes de observar ingresso; não inventar container/perfil separando serviços. Bootstrap de timestamps e Migrate acontecem antes dos hosted services.
5. Subir backend compatível. `Program.cs623` chama db.Database.Migrate automaticamente no startup: iniciar binário é potencial execução de migrations mesmo sem comando EF. Schema deve estar aplicado/verificado antes; não usar startup como teste read-only de produção. Artefato não foi iniciado pelo agente.
6. Manter MudancaOperacionalHabilitada conforme estratégia atual desligada; não ativar nesta etapa. Preservar sampling efetivo10% existente e flags de ETA/shadows conforme estado aprovado. Flag false não dispensa IntegridadeCircular nem permite writer antigo. Consumers precisam preservar motivo_fim=PerdaContinuidadeCircular para evitar payload divergente em reentrega.
7. Health-check: schema/artefato/liveness/GPS/Redis/viagens/outbox/ML/recursos. Confirmar persistência nova antes de registrar início. Qualquer falha inexplicada suspende declaração da janela oficial, sem apagar estado.
8. Registrar timestamp UTC REAL e manifesto completo depois do health. Conferir linhas posteriores ao marco e monitorar execuções iniciadas depois dele. O período de probe anterior ao marco não entra no dataset oficial.
9. Somente após nova coleta comprovada considerar purge ML antigo condicionado à seção4. Coleta não depende da purge; preferir filtrar por marco primeiro.

## 7. Retorno, recuperação e 3B em paralelo

Falha pré-health: parar writers/consumers novos se integridade/carga exigir, preservar dados e corrigir para frente ou voltar binário compatível. Migration Down bloqueia remover extensão; não apagar markers/cursor nem restaurar writer pré-integridade. Desligar flag não desfaz eventos nem limpa proteção. Sem artefato compatível, manter writers parados; restauração de backup é recuperação de desastre com reconciliação explícita, não rollback rotineiro.

Redis mantido + restart: retry de melhor esforço pode recuperar dentro do TTL; após perda Redis/registro incompleto/outage longo pode perder pendências, limitação aceita. PG/outbox/journal continuam autoridade. Não fabricar chegada/label para gaps. Commit com resposta perdida é reconciliado pelo retry; consumer com lease expirado reentrega idempotentemente. Não XACK/mark processado manualmente para acelerar health.

3B pode avançar agora com schema/features, split temporal por ViagemId, baseline simples, métricas MAE/RMSE/coverage e teste de contrato do serviço ML, enquanto coleta cresce. Dados sintéticos só para desenvolver pipeline, nunca reportar como treinamento real. Depois selecionar novas execuções completas com journal/qualidade auditada, conferir distância geography e gerar CSV/manifesto/checksum. Sampling por linha/bloco limita cobertura de linhas: acompanhar suporte por modal/linha/horário, sem tentar recuperar passado nem aumentar taxa nesta etapa.

## Resultado desta execução

Somente este relatório e acréscimo em HISTORICO alterados. Sem código/configuração/sampling/flags, infra/CI/CD, testes/builds repetidos, Docker/serviços externos/produção/SSH, migration/purge, commit/push/operação Git destrutiva. Todos os comandos acima são **futuros e NÃO EXECUTADOS**. A aprovação manual1/0 foi registrada, não repetida pelo agente. Não foi escolhido marco UTC nem alegada configuração efetiva de produção.
