-- NoPonto 3B.2A / noponto-eta-gps-v1. Preparado, NAO executado em banco.
-- Cliente psql >= 16; servidor PostgreSQL >= 16 + PostGIS.
-- Por padrao somente A/B/H (catalogos). C/D/E/F/G exigem -v detalhado=true.
-- C/D/E/F/G: scans e agregacoes potencialmente caros; ver docs/ETA_ML_TREINO_3B.md.
-- Nenhuma qualidade positiva e atribuida. candidate_inventory != AuditadaSemProtecao.
\set ON_ERROR_STOP on
\pset pager off
\if :{?detalhado}
\else
\set detalhado false
\endif

BEGIN READ ONLY;
-- Uma unica fotografia MVCC para todos os relatorios; configuracao so da transacao.
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;

-- A: instante UTC do inicio da transacao, nao relogio mutavel entre blocos.
SELECT 'A_contexto' AS relatorio, current_database() AS banco,
       current_schema() AS schema, CURRENT_TIMESTAMP AT TIME ZONE 'UTC' AS auditoria_utc,
       version() AS postgresql, postgis_full_version() AS postgis,
       'noponto-eta-gps-v1' AS dataset_contract,
       TIMESTAMPTZ '2026-10-07T14:53:59.225082Z' AS collection_started_at_utc,
       current_setting('transaction_read_only') AS read_only,
       current_setting('transaction_isolation') AS isolamento;

-- B/H: tamanhos fisicos COMPLETOS (heap/TOAST/indices), nao tamanho de dump filtrado.
-- reltuples e aproximado e pode estar desatualizado/-1; nao executar ANALYZE aqui.
WITH fontes(nome, papel) AS (VALUES
 ('TelemetriasVeiculoMl', 'GPS do exportador'),
 ('HistoricoPassagens', 'labels e identidade de passagem'),
 ('EventosViagem', 'journal e fronteiras'),
 ('ViagensOperacionais', 'evidencia negativa corrente; nao trilha historica'),
 ('Linhas', 'estrutura'), ('Sentidos', 'estrutura'),
 ('PadroesOperacionais', 'estrutura'), ('PadroesVersoes', 'estrutura/geometria'),
 ('OcorrenciasParadasPadroes', 'estrutura/destino'), ('Paradas', 'estrutura/parada')),
 tamanhos AS (
 SELECT f.*, c.oid, c.reltuples::bigint AS linhas_estimadas,
        pg_total_relation_size(c.oid) AS bytes_completos
 FROM fontes f LEFT JOIN pg_class c ON c.oid = to_regclass(format('%I', f.nome))
)
SELECT 'B_H_fontes' AS relatorio, nome, papel, oid IS NOT NULL AS existe,
       linhas_estimadas, bytes_completos, pg_size_pretty(bytes_completos) AS tamanho,
       sum(bytes_completos) OVER () AS soma_bytes_fontes_completas
FROM tamanhos ORDER BY nome;

-- H: colunas REAIS, tipos e FKs do banco; inclui dependencias de restauracao.
-- O subconjunto de 10 fontes nao e promessa de fechamento de FKs de schema inteiro.
SELECT 'H_colunas' AS relatorio, table_schema, table_name, column_name, data_type,
       udt_name, is_nullable
FROM information_schema.columns
WHERE table_schema = current_schema() AND table_name IN
 ('TelemetriasVeiculoMl','HistoricoPassagens','EventosViagem','ViagensOperacionais',
  'Linhas','Sentidos','PadroesOperacionais','PadroesVersoes','OcorrenciasParadasPadroes','Paradas')
ORDER BY table_name, ordinal_position;

SELECT 'H_fks' AS relatorio, conrelid::regclass AS origem,
       confrelid::regclass AS destino, pg_get_constraintdef(oid) AS relacionamento
FROM pg_constraint
WHERE contype = 'f' AND conrelid IN (SELECT c.oid FROM pg_class c
 JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=current_schema()
 AND c.relname IN ('TelemetriasVeiculoMl','HistoricoPassagens','EventosViagem',
 'ViagensOperacionais','Linhas','Sentidos','PadroesOperacionais','PadroesVersoes',
 'OcorrenciasParadasPadroes','Paradas')) ORDER BY conrelid::regclass::text, confrelid::regclass::text;

SELECT 'B_indices' AS relatorio, tablename, indexname, indexdef
FROM pg_indexes WHERE schemaname=current_schema() AND tablename IN
 ('TelemetriasVeiculoMl','HistoricoPassagens','EventosViagem','ViagensOperacionais')
ORDER BY tablename,indexname;

\if :detalhado
-- OPCIONAL C-G: uma leitura das fontes pos-cutoff materializada nesta consulta.
-- GPS global sem indice TimestampGps isolado pode percorrer toda a tabela.
-- CTEs, joins, percentis e inventario podem usar memoria/disco temporario interno.
-- Nao ha calculo geografico, execucao do SQL 3A ou escrita de tabelas temporarias.
WITH
gps AS MATERIALIZED (
 SELECT t."ViagemId", t."TimestampGps", t."Modal", t."CodigoLinha", t."OrdemVeiculo",
        t."LinhaId", t."SentidoId", t."PadraoVersaoId", t."Volta",
        t."ProximaOcorrenciaParadaPadraoId", t."OcorrenciaParadaPadraoId",
        t."PosicaoNaRota", p."Id" AS padrao_id,
        COALESCE(t."ViagemId" <> '00000000-0000-0000-0000-000000000000'::uuid
         AND t."Volta">=0 AND btrim(t."ObservacaoId")<>'' AND btrim(t."Provedor")<>''
         AND btrim(t."OrdemVeiculo")<>'' AND t."OrigemPosicao"='REAL'
         AND t."LinhaId"<>'00000000-0000-0000-0000-000000000000'::uuid
         AND t."SentidoId"<>'00000000-0000-0000-0000-000000000000'::uuid
         AND t."PadraoVersaoId"<>'00000000-0000-0000-0000-000000000000'::uuid
         AND o."Id"<>'00000000-0000-0000-0000-000000000000'::uuid
         AND d."Id"<>'00000000-0000-0000-0000-000000000000'::uuid
         AND p."Id"<>'00000000-0000-0000-0000-000000000000'::uuid
         AND t."Modal" IN ('ONIBUS','BRT')
         -- Alvo operacional obrigatorio; ocorrencia observacional opcional e independente.
         -- Factory atual copia o alvo operacional quando existe; auditoria nao infere
         -- essa igualdade. EtaDataset ainda exige igualdade e pode descartar candidatos.
         AND (t."OcorrenciaParadaPadraoId" IS NULL OR
           (obs."Id"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND obs."PadraoVersaoId"=v."Id"))
         AND t."PadraoVersaoId"=v."Id" AND t."SentidoId"=s."Id"
         AND t."LinhaId"=l."Id" AND t."CodigoLinha"=l."Codigo"
         AND o."ParadaId"=d."Id" AND v."Topologia" IN ('LINEAR','CIRCULAR'), false)
         AS identidade_estrutural
 FROM "TelemetriasVeiculoMl" t
 LEFT JOIN "OcorrenciasParadasPadroes" o ON o."Id"=t."ProximaOcorrenciaParadaPadraoId"
 LEFT JOIN "OcorrenciasParadasPadroes" obs ON obs."Id"=t."OcorrenciaParadaPadraoId"
 LEFT JOIN "PadroesVersoes" v ON v."Id"=o."PadraoVersaoId"
 LEFT JOIN "PadroesOperacionais" p ON p."Id"=v."PadraoOperacionalId"
 LEFT JOIN "Sentidos" s ON s."Id"=p."SentidoId"
 LEFT JOIN "Linhas" l ON l."Id"=s."LinhaId"
 LEFT JOIN "Paradas" d ON d."Id"=o."ParadaId"
 WHERE t."TimestampGps">=TIMESTAMPTZ '2026-10-07T14:53:59.225082Z'
),
journal AS MATERIALIZED (
 SELECT e.*, e."Payload"->>'viagem_id' AS trip_text,
        CASE WHEN pg_input_is_valid(e."Payload"->>'viagem_id','uuid')
             THEN (e."Payload"->>'viagem_id')::uuid END AS trip_id,
        CASE WHEN pg_input_is_valid(e."Payload"->>'timestamp_evento','timestamptz')
             THEN (e."Payload"->>'timestamp_evento')::timestamptz END AS payload_ts
 FROM "EventosViagem" e
 WHERE e."TimestampEvento">=TIMESTAMPTZ '2026-10-07T14:53:59.225082Z'
),
jconferido AS MATERIALIZED (
 SELECT j.*, COALESCE(j.trip_id<>'00000000-0000-0000-0000-000000000000'::uuid
   AND j."Payload"->>'event_id'=j."EventId" AND j."Payload"->>'tipo'=j."Tipo"
   AND j."Payload"->>'schema_version'='2' AND j.payload_ts=j."TimestampEvento"
   AND isfinite(j.payload_ts),false) AS envelope_valido
 FROM journal j
),
historico AS MATERIALIZED (
 SELECT h.*, COALESCE(j.envelope_valido AND j."Tipo"='PassagemParada'
   AND j.trip_id=h."ViagemId"
   AND j."Payload"->>'ocorrencia_parada_padrao_id'=h."OcorrenciaParadaPadraoId"::text
   AND j."Payload"->>'parada_id'=h."ParadaId"::text
   AND j."Payload"->>'padrao_versao_id'=h."PadraoVersaoId"::text
   AND j."Payload"->>'sentido_id'=h."SentidoId"::text
   AND j."Payload"->>'ordem_veiculo'=h."Ordem"
   AND j."Payload"->>'codigo_linha'=h."CodigoLinha"
   AND j."Payload"->>'volta'=h."Volta"::text
   AND j.payload_ts=h."TimestampPassagem",false) AS journal_compativel
 FROM "HistoricoPassagens" h
 LEFT JOIN jconferido j ON j."EventId"='passagem:'||h."ViagemId"::text||':'
   ||h."OcorrenciaParadaPadraoId"::text||':'||h."Volta"::text
 WHERE h."TimestampGps">=TIMESTAMPTZ '2026-10-07T14:53:59.225082Z'
),
fronteiras AS MATERIALIZED (
 SELECT j.trip_id,
   count(*) FILTER (WHERE j."Tipo"='ViagemIniciada') AS inicios,
   count(*) FILTER (WHERE j."Tipo"='ViagemFinalizada') AS fins,
   min(j."TimestampEvento") FILTER (WHERE j."Tipo"='ViagemIniciada'
     AND j.envelope_valido AND j."EventId"='inicio:'||j.trip_id::text) AS inicio,
   max(j."TimestampEvento") FILTER (WHERE j."Tipo"='ViagemFinalizada'
     AND j.envelope_valido AND j."EventId"='fim:'||j.trip_id::text) AS fim,
   count(*) FILTER (WHERE NOT j.envelope_valido OR
     j."EventId"<>CASE j."Tipo" WHEN 'ViagemIniciada' THEN 'inicio:' ELSE 'fim:' END
       ||j.trip_id::text) AS fronteiras_invalidas,
   count(DISTINCT jsonb_build_array(j."Payload"->>'ordem_veiculo',
     j."Payload"->>'codigo_linha',j."Payload"->>'linha_id',j."Payload"->>'sentido_id',
     j."Payload"->>'padrao_operacional_id',j."Payload"->>'padrao_versao_id')) AS identidades,
   max(j."Payload"->>'codigo_linha') AS codigo_linha,
   max(j."Payload"->>'ordem_veiculo') AS ordem_veiculo,
   max(j."Payload"->>'linha_id') AS linha_id,
   max(j."Payload"->>'sentido_id') AS sentido_id,
   max(j."Payload"->>'padrao_versao_id') AS versao_id,
   max(j."Payload"->>'padrao_operacional_id') AS padrao_id,
   max(j."Payload"->>'motivo_fim') FILTER (WHERE j."Tipo"='ViagemFinalizada') AS motivo_fim
 FROM jconferido j WHERE j."Tipo" IN ('ViagemIniciada','ViagemFinalizada')
 AND j.trip_id IS NOT NULL GROUP BY j.trip_id
),
markers AS MATERIALIZED (
 SELECT "IntegridadeCircular"->>'ViagemId' AS trip_text,
   "IntegridadeCircular" AS marker, "AtualizadoEmUtc" AS atualizado
 FROM "ViagensOperacionais" WHERE "IntegridadeCircular" IS NOT NULL
),
gps_viagens AS MATERIALIZED (
 SELECT "ViagemId" AS trip_id, count(*) AS gps_total,
   count(*) FILTER (WHERE identidade_estrutural) AS gps_identidade,
   CASE WHEN count(DISTINCT "Modal")=1 THEN min("Modal") ELSE 'MULTIPLO' END AS modal
 FROM gps GROUP BY "ViagemId"
),
historico_viagens AS MATERIALIZED (
 SELECT "ViagemId" AS trip_id, count(*) AS passagens,
   count(*) FILTER (WHERE journal_compativel) AS passagens_conferidas
 FROM historico GROUP BY "ViagemId"
),
pares AS MATERIALIZED (
 SELECT g."ViagemId" AS trip_id, count(*) AS pares_potenciais
 FROM gps g JOIN historico h ON h."ViagemId"=g."ViagemId"
   AND h."OcorrenciaParadaPadraoId"=g."ProximaOcorrenciaParadaPadraoId"
   AND h."Volta"=g."Volta"
 JOIN fronteiras f ON f.trip_id=g."ViagemId"
 WHERE g.identidade_estrutural AND h.journal_compativel
   AND g."TimestampGps">=f.inicio AND h."TimestampGps"<=f.fim
   AND h."TimestampPassagem">g."TimestampGps" AND h."TimestampPassagem"<=f.fim
   AND h."TimestampPassagem"<=h."TimestampGps"
   AND h."SentidoId"=g."SentidoId" AND h."PadraoVersaoId"=g."PadraoVersaoId"
   AND h."CodigoLinha"=g."CodigoLinha" AND h."Ordem"=g."OrdemVeiculo"
   AND g."LinhaId"::text=f.linha_id AND g."SentidoId"::text=f.sentido_id
   AND g."PadraoVersaoId"::text=f.versao_id AND g.padrao_id::text=f.padrao_id
   AND g."OrdemVeiculo"=f.ordem_veiculo AND g."CodigoLinha"=f.codigo_linha
 GROUP BY g."ViagemId"
),
markers_negativos AS MATERIALIZED (
 SELECT DISTINCT trip_text FROM markers WHERE marker->>'Continuidade'='1'
   OR NULLIF(marker->>'PerdaEm','') IS NOT NULL OR NULLIF(marker->>'Motivo','') IS NOT NULL
),
viagens AS MATERIALIZED (
 SELECT f.*, COALESCE(gg.gps_total,0) AS gps_total,
   COALESCE(gg.gps_identidade,0) AS gps_identidade, COALESCE(gg.modal,'SEM_GPS') AS modal,
   COALESCE(hh.passagens,0) AS passagens, COALESCE(hh.passagens_conferidas,0) AS passagens_conferidas,
   COALESCE(pp.pares_potenciais,0) AS pares_potenciais,
   m.trip_text IS NOT NULL AS marker_negativo,
   CASE WHEN f.fim>=f.inicio AND f.inicios=1 AND f.fins=1
     AND f.fronteiras_invalidas=0 AND f.identidades=1
     THEN extract(epoch FROM f.fim-f.inicio) END AS duracao_segundos
 FROM fronteiras f
 LEFT JOIN gps_viagens gg ON gg.trip_id=f.trip_id
 LEFT JOIN historico_viagens hh ON hh.trip_id=f.trip_id
 LEFT JOIN pares pp ON pp.trip_id=f.trip_id
 LEFT JOIN markers_negativos m ON m.trip_text=f.trip_id::text
 WHERE f.inicio>=TIMESTAMPTZ '2026-10-07T14:53:59.225082Z'
),
inventario AS MATERIALIZED (
 SELECT v.*, (inicios=1 AND fins=1 AND fim>inicio AND fronteiras_invalidas=0
   AND identidades=1 AND pares_potenciais>0 AND NOT marker_negativo
   AND motivo_fim IS DISTINCT FROM 'PerdaContinuidadeCircular') AS candidate_inventory
 FROM viagens v
),
gps_distribuicao AS (
 SELECT CASE WHEN GROUPING("Modal")=0 THEN 'modal'
             WHEN GROUPING("CodigoLinha")=0 THEN 'linha'
             WHEN GROUPING(("TimestampGps" AT TIME ZONE 'UTC')::date)=0 THEN 'dia_utc'
             WHEN GROUPING(extract(hour FROM "TimestampGps" AT TIME ZONE 'UTC'))=0 THEN 'hora_utc'
             ELSE 'total' END AS grupo,
        "Modal" AS modal, "CodigoLinha" AS codigo_linha,
        ("TimestampGps" AT TIME ZONE 'UTC')::date AS dia_utc,
        extract(hour FROM "TimestampGps" AT TIME ZONE 'UTC') AS hora_utc,
        count(*) AS registros, min("TimestampGps") AS primeiro_gps,
        max("TimestampGps") AS ultimo_gps,
        count(DISTINCT ("TimestampGps" AT TIME ZONE 'UTC')::date) AS dias_utc,
        count(*) FILTER (WHERE "ViagemId" IS NOT NULL) AS com_viagem_id,
        count(*) FILTER (WHERE "PadraoVersaoId" IS NOT NULL
          AND "ProximaOcorrenciaParadaPadraoId" IS NOT NULL
          AND "OcorrenciaParadaPadraoId" IS NOT NULL AND "Volta">=0) AS com_versao_ocorrencias_volta,
        count(*) FILTER (WHERE identidade_estrutural) AS identidade_estrutural_conferida
 FROM gps GROUP BY GROUPING SETS ((),("Modal"),("CodigoLinha"),
   (("TimestampGps" AT TIME ZONE 'UTC')::date),
   (extract(hour FROM "TimestampGps" AT TIME ZONE 'UTC')))
),
viagens_distribuicao AS (
 SELECT CASE WHEN GROUPING(modal)=0 THEN 'modal'
             WHEN GROUPING(codigo_linha)=0 THEN 'linha'
             WHEN GROUPING((inicio AT TIME ZONE 'UTC')::date)=0 THEN 'dia_utc'
             WHEN GROUPING(motivo_fim)=0 THEN 'motivo_fim' ELSE 'total' END AS grupo,
   modal,codigo_linha,(inicio AT TIME ZONE 'UTC')::date AS dia_utc,motivo_fim,
   count(*) AS iniciadas, count(*) FILTER (WHERE fim IS NOT NULL) AS com_fim_journal,
   count(*) FILTER (WHERE fins=0) AS sem_fim_observado,
   count(*) FILTER (WHERE fins>0 AND fim IS NULL) AS fim_invalido,
   count(*) FILTER (WHERE fim IS NOT NULL AND passagens=0) AS fechadas_sem_passagem,
   count(*) FILTER (WHERE passagens>0) AS com_passagem,
   count(*) FILTER (WHERE candidate_inventory) AS candidatos_inventario,
   min(duracao_segundos) AS duracao_min_segundos,
   percentile_cont(0.5) WITHIN GROUP (ORDER BY duracao_segundos) AS duracao_mediana_segundos,
   percentile_cont(0.9) WITHIN GROUP (ORDER BY duracao_segundos) AS duracao_p90_segundos,
   max(duracao_segundos) AS duracao_max_segundos
 FROM inventario GROUP BY GROUPING SETS ((),(modal),(codigo_linha),
   ((inicio AT TIME ZONE 'UTC')::date),(motivo_fim))
)
SELECT * FROM (
SELECT 'C_telemetria_nao_e_dataset' AS relatorio,
  to_jsonb(c)||jsonb_build_object('rank_por_volume',
    dense_rank() OVER (PARTITION BY grupo ORDER BY registros DESC)) AS dados
FROM gps_distribuicao c
UNION ALL
SELECT 'D_viagens',to_jsonb(d) FROM viagens_distribuicao d
UNION ALL
SELECT 'E_journal',jsonb_build_object('tipo',"Tipo",'eventos',count(*),
  'envelopes_invalidos',count(*) FILTER (WHERE NOT envelope_valido),
  'primeiro_evento',min("TimestampEvento"),'ultimo_evento',max("TimestampEvento"))
FROM jconferido GROUP BY "Tipo"
UNION ALL
SELECT 'E_historico',jsonb_build_object('registros',count(*),
  'viagens_com_passagem',count(DISTINCT "ViagemId"),
  'sem_journal_compativel',count(*) FILTER (WHERE NOT journal_compativel),
  'passagem_apos_gps',count(*) FILTER (WHERE "TimestampPassagem">"TimestampGps"),
  'sem_timestamp_passagem',count(*) FILTER (WHERE "TimestampPassagem" IS NULL)) FROM historico
UNION ALL
SELECT 'E_G_fronteiras',jsonb_build_object('viagens_com_inicio_e_fim',
  count(*) FILTER (WHERE inicio IS NOT NULL AND fim IS NOT NULL),
  'fronteiras_invalidas',sum(fronteiras_invalidas),
  'identidades_divergentes',count(*) FILTER (WHERE identidades>1),
  'fim_antes_ou_igual_inicio',count(*) FILTER (WHERE fim<=inicio),
  'fim_sem_inicio_pos_cutoff',count(*) FILTER (WHERE inicios=0 AND fins>0),
  'perda_continuidade',count(*) FILTER (WHERE motivo_fim='PerdaContinuidadeCircular')) FROM fronteiras
UNION ALL
SELECT 'F_candidate_inventory',to_jsonb(i) FROM inventario i
UNION ALL
SELECT 'G_markers_correntes_nao_aprovam',jsonb_build_object('viagem_id',trip_text,
  'atualizado_em_utc',atualizado,'continuidade',marker->>'Continuidade',
  'perda_em',marker->>'PerdaEm','motivo',marker->>'Motivo','contrato',marker->>'Contrato',
  'evidencia_negativa',marker->>'Continuidade'='1'
    OR NULLIF(marker->>'PerdaEm','') IS NOT NULL OR NULLIF(marker->>'Motivo','') IS NOT NULL)
FROM markers
) AS relatorios ORDER BY relatorio,dados::text;
\else
\echo C/D/E/F/G omitidos: opcional potencialmente caro; habilitar com -v detalhado=true.
\endif

COMMIT;
