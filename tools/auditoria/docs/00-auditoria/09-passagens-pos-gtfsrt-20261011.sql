-- Auditoria MANUAL, read-only. Não executada pelo agente.
-- Dialeto psql/PostgreSQL. Executar UM bloco por vez, após conferir índices/plano.
-- \set só define variáveis no cliente. Nenhuma credencial neste arquivo.
-- Caps são de diagnóstico: se cortado=true, NÃO interpretar contagem como total.
-- Não elevar caps/timeouts automaticamente. Não executar EXPLAIN ANALYZE.

-- Q1: catálogo limitado às relações relevantes. Conferir indisvalid/indisready.
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '10s';
SET LOCAL lock_timeout = '1s';
SET LOCAL idle_in_transaction_session_timeout = '30s';
SELECT n.nspname AS schema_name, t.relname AS tabela, x.relname AS indice,
       i.indisvalid, i.indisready, pg_get_indexdef(i.indexrelid) AS definicao
FROM pg_index i
JOIN pg_class t ON t.oid = i.indrelid
JOIN pg_class x ON x.oid = i.indexrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
WHERE n.nspname = 'public' AND t.relname IN
 ('EventosViagem','HistoricoPassagens','TelemetriasVeiculoMl','OutboxViagens',
  'FontesEstruturais','LinhasIdentidadesExternas','SentidosIdentidadesExternas',
  'Sentidos','PadroesOperacionais','PadroesVersoes','OcorrenciasParadasPadroes')
ORDER BY t.relname, x.relname;
ROLLBACK;

-- Q2: cada janela busca até 20001 eventos no índice TimestampEvento.
-- Conta até 20000; a linha adicional indica truncamento. O histórico é procurado
-- pela chave da passagem, não por outro relógio. Inclui todas as modalidades;
-- não há modal/provedor no journal: fonte exige evidência externa/Q3.
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '10s';
SET LOCAL lock_timeout = '1s';
SET LOCAL idle_in_transaction_session_timeout = '30s';
WITH janelas(nome, inicio, fim) AS (VALUES
 ('quinta', '2026-10-08T23:45:00Z'::timestamptz, '2026-10-08T23:55:00Z'::timestamptz),
 ('sexta', '2026-10-09T23:45:00Z'::timestamptz, '2026-10-09T23:55:00Z'::timestamptz),
 ('sabado', '2026-10-10T23:45:00Z'::timestamptz, '2026-10-10T23:55:00Z'::timestamptz),
 ('domingo_outro_horario', '2026-10-11T15:00:00Z'::timestamptz, '2026-10-11T15:10:00Z'::timestamptz)
), eventos AS MATERIALIZED (
 SELECT w.*, e."EventId", e."Tipo", e."Payload", e."TimestampEvento",
        row_number() OVER (PARTITION BY w.nome ORDER BY e."TimestampEvento", e."EventId") AS rn
 FROM janelas w CROSS JOIN LATERAL (
   SELECT "EventId", "Tipo", "Payload", "TimestampEvento"
   FROM "EventosViagem"
   WHERE "TimestampEvento" >= w.inicio AND "TimestampEvento" < w.fim
   ORDER BY "TimestampEvento", "EventId" LIMIT 20001
 ) e
), escopo AS (
 SELECT *, "Payload"->>'codigo_linha' AS codigo,
   ("Payload"->>'viagem_id')::uuid AS viagem,
   ("Payload"->>'ocorrencia_parada_padrao_id')::uuid AS ocorrencia,
   ("Payload"->>'volta')::integer AS volta
 FROM eventos WHERE rn <= 20000 AND "Tipo" = 'PassagemParada'
)
SELECT w.nome, extract(dow FROM w.inicio AT TIME ZONE 'America/Sao_Paulo') AS dia_local,
       EXISTS(SELECT 1 FROM eventos e WHERE e.nome=w.nome AND rn>20000) AS cortado,
       count(p."EventId") AS passagens_journal,
       count(DISTINCT p.codigo) AS linhas_com_passagem,
       count(h."Id") AS historicos_da_mesma_chave,
       count(p."EventId") FILTER (WHERE h."Id" IS NULL) AS sem_historico,
       count(h."Id") FILTER (WHERE h."TimestampPassagem" < w.inicio
                                  OR h."TimestampPassagem" >= w.fim) AS historicos_fora_janela_semantica
FROM janelas w LEFT JOIN escopo p ON p.nome=w.nome
LEFT JOIN "HistoricoPassagens" h
 ON h."ViagemId"=p.viagem AND h."OcorrenciaParadaPadraoId"=p.ocorrencia AND h."Volta"=p.volta
GROUP BY w.nome, w.inicio ORDER BY w.inicio;
-- Para painel por linha: reaproveitar o mesmo CTE e agrupar escopo por nome,codigo.
-- Payload legado inválido pode falhar em cast; não corrigir/backfill para fazê-lo passar.
ROLLBACK;

-- Q3: UMA linha escolhida pelo operador, repetir nas janelas equivalentes.
-- Preferir linha perdida e linha controle do mesmo modal. Não selecionar só sobreviventes.
-- Valores PREENCHER devem ser substituídos. Resultado vazio não comprova ausência de GPS:
-- sampling seleciona linhas por bloco, não 10% uniforme de cada veículo.
\set codigo 'PREENCHER_CODIGO_EXATO'
\set inicio '2026-10-09T23:45:00Z'
\set fim '2026-10-09T23:55:00Z'
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '10s';
SET LOCAL lock_timeout = '1s';
SET LOCAL idle_in_transaction_session_timeout = '30s';
WITH gps AS MATERIALIZED (
 SELECT "Modal","Provedor","OrdemVeiculo","TimestampGps","PadraoVersaoId",
        "PosicaoNaRota","ViagemId","Volta","ProximaOcorrenciaParadaPadraoId",
        "OcorrenciaParadaPadraoId","Bearing"
 FROM "TelemetriasVeiculoMl"
 WHERE "CodigoLinha"=:'codigo' AND "TimestampGps">=:'inicio'::timestamptz
   AND "TimestampGps"<:'fim'::timestamptz
 ORDER BY "TimestampGps","Id" LIMIT 10001
), amostra AS (SELECT * FROM gps ORDER BY "TimestampGps","OrdemVeiculo" LIMIT 10000)
SELECT "Modal","Provedor", (SELECT count(*)>10000 FROM gps) AS cortado,
 count(*) AS gps_amostrados, count(DISTINCT "OrdemVeiculo") AS veiculos,
 count(*) FILTER (WHERE "PadraoVersaoId" IS NOT NULL AND "PosicaoNaRota" IS NOT NULL) AS com_matching,
 count(*) FILTER (WHERE "ViagemId" IS NOT NULL) AS com_viagem,
 count(*) FILTER (WHERE "ViagemId" IS NOT NULL AND "Volta" IS NOT NULL
   AND "ProximaOcorrenciaParadaPadraoId" IS NOT NULL
   AND "ProximaOcorrenciaParadaPadraoId"="OcorrenciaParadaPadraoId") AS com_alvo_operacional,
 count(*) FILTER (WHERE "Bearing" IS NULL) AS bearing_nulo,
 count(DISTINCT "PadraoVersaoId") AS versoes
FROM amostra GROUP BY "Modal","Provedor";

-- Agrupamento de passagens pelo GPS confirmador: indício de catch-up, não erro comprovado.
-- Índice CodigoLinha,TimestampPassagem. Tempo de interpolação máximo por grupo;
-- gap entre observações operacionais requer logs ou série selecionada por veículo.
WITH h AS MATERIALIZED (
 SELECT "ViagemId","Volta","TimestampGps","TimestampPassagem"
 FROM "HistoricoPassagens"
 WHERE "CodigoLinha"=:'codigo' AND "TimestampPassagem">=:'inicio'::timestamptz
   AND "TimestampPassagem"<:'fim'::timestamptz
 ORDER BY "TimestampPassagem" LIMIT 10001
), amostra AS (SELECT * FROM h ORDER BY "TimestampPassagem" LIMIT 10000)
SELECT "ViagemId","Volta","TimestampGps", count(*) AS ocorrencias_confirmadas,
 max(extract(epoch FROM "TimestampGps"-"TimestampPassagem")) AS interpolacao_max_s,
 (SELECT count(*)>10000 FROM h) AS cortado
FROM amostra GROUP BY "ViagemId","Volta","TimestampGps"
ORDER BY ocorrencias_confirmadas DESC LIMIT 30;
ROLLBACK;

-- Q4: viagem específica, sem filtro JSON global. Fronteiras via PK e histórico via ViagemId.
-- Zero UUID é placeholder, substituir pelo UUID de um caso real conhecido.
\set viagem '00000000-0000-0000-0000-000000000000'
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '10s';
SET LOCAL lock_timeout = '1s';
SET LOCAL idle_in_transaction_session_timeout = '30s';
SELECT "EventId","Tipo","TimestampEvento",
 "Payload"->>'codigo_linha' AS codigo, "Payload"->>'ordem_veiculo' AS veiculo,
 "Payload"->>'motivo_fim' AS motivo_fim,
 "Payload"->>'ocorrencia_parada_padrao_id' AS ocorrencia_fronteira,
 "Payload"->>'padrao_versao_id' AS versao
FROM "EventosViagem" WHERE "EventId" IN ('inicio:'||:'viagem','fim:'||:'viagem');

SELECT f."TimestampEvento" AS fim, h."TimestampPassagem" AS ultima_passagem,
 extract(epoch FROM f."TimestampEvento"-h."TimestampPassagem") AS gap_fim_s,
 h."TimestampGps" AS gps_confirmador, h."TimestampRegistro" AS registro_historico,
 h."OcorrenciaParadaPadraoId", h."Volta"
FROM "EventosViagem" f LEFT JOIN LATERAL (
 SELECT "TimestampPassagem","TimestampGps","TimestampRegistro","OcorrenciaParadaPadraoId","Volta"
 FROM "HistoricoPassagens"
 WHERE "ViagemId"=:'viagem'::uuid AND "TimestampPassagem" IS NOT NULL
 ORDER BY "TimestampPassagem" DESC LIMIT 1
) h ON true WHERE f."EventId"='fim:'||:'viagem';

-- Até 201 eventos numa faixa de 2 segundos junto do fim, depois filtro de veículo.
-- Top200 é só investigação de fronteira, não reconstituição histórica completa.
WITH f AS (SELECT "Payload","TimestampEvento" FROM "EventosViagem"
           WHERE "EventId"='fim:'||:'viagem'), proximos AS MATERIALIZED (
 SELECT e.* FROM f CROSS JOIN LATERAL (
  SELECT "EventId","Tipo","TimestampEvento","Payload" FROM "EventosViagem"
  WHERE "TimestampEvento">=f."TimestampEvento"-interval '1 second'
    AND "TimestampEvento"<f."TimestampEvento"+interval '1 second'
  ORDER BY "TimestampEvento","EventId" LIMIT 201
 ) e
)
SELECT e."EventId",e."Tipo",e."TimestampEvento",
 e."Payload"->>'codigo_linha' AS codigo, e."Payload"->>'viagem_id' AS viagem,
 (SELECT count(*)>200 FROM proximos) AS cortado,
 e."TimestampEvento"=f."TimestampEvento" AS mesmo_instante
FROM (SELECT * FROM proximos ORDER BY "TimestampEvento","EventId" LIMIT 200) e CROSS JOIN f
WHERE e."Tipo"='ViagemIniciada'
 AND e."Payload"->>'ordem_veiculo'=f."Payload"->>'ordem_veiculo';
ROLLBACK;

-- Q5: backlog atual limitado pelo índice parcial. Não é total global de pendentes.
-- A falta de linha processada pode ser retenção de7dias, não perda comprovada.
\set evento 'PREENCHER_EVENT_ID_CANONICO'
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '10s';
SET LOCAL lock_timeout = '1s';
SET LOCAL idle_in_transaction_session_timeout = '30s';
SELECT "EventId","Tipo","CriadoEmUtc","Tentativas","ProximaTentativaEmUtc","BloqueadoAteUtc",
       now()-"CriadoEmUtc" AS idade_pendente,
       "UltimoErro" IS NOT NULL AS tem_erro
FROM "OutboxViagens" WHERE "ProcessadoEmUtc" IS NULL
ORDER BY "CriadoEmUtc","EventId" LIMIT 100;
SELECT "EventId","Tipo","CriadoEmUtc","ProcessadoEmUtc","Tentativas",
       "ProcessadoEmUtc"-"CriadoEmUtc" AS atraso_materializacao,
       "UltimoErro" IS NOT NULL AS tem_erro
FROM "OutboxViagens" WHERE "EventId"=:'evento';
-- Mensagens de erro completas só localmente; podem conter payloads/identificadores sensíveis.
ROLLBACK;

-- Q6: UMA rota externa conhecida, sem inferir por substring ou importar/reconciliar.
-- Reproduz inspeção de metadados, não todo o gate booleano do lookup.
-- Resultado estrutura_ok apenas verifica presença; não certifica geometria/ocorrências.
\set route 'PREENCHER_ROUTE_ID_EXATO'
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '10s';
SET LOCAL lock_timeout = '1s';
SET LOCAL idle_in_transaction_session_timeout = '30s';
WITH alvo AS MATERIALIZED (
 SELECT f."Id" AS fonte, lie."LinhaId",lie."ExternalId",l."Codigo"
 FROM "FontesEstruturais" f JOIN "LinhasIdentidadesExternas" lie
   ON lie."FonteEstruturalId"=f."Id"
 JOIN "Linhas" l ON l."Id"=lie."LinhaId"
 WHERE f."Codigo"='DATARIO_GTFS' AND lie."Tipo"='ROUTE_ID' AND lie."ExternalId"=:'route'
)
SELECT a."ExternalId",a."LinhaId",a."Codigo", metadata.agencies,metadata.origins,
 s."Id" AS sentido,sie."ExternalId" AS direcao_externa,
 p."Id" AS padrao,p."VersaoAtualId",v."Topologia",
 v."Geometria" IS NOT NULL AS tem_geometria,
 EXISTS(SELECT 1 FROM "OcorrenciasParadasPadroes" o WHERE o."PadraoVersaoId"=v."Id") AS tem_ocorrencias
FROM alvo a LEFT JOIN LATERAL (
 SELECT array_agg(m."ExternalId") FILTER (WHERE m."Tipo"='ROUTE_AGENCY') AS agencies,
        array_agg(m."ExternalId") FILTER (WHERE m."Tipo"='ROUTE_ORIGIN_V1') AS origins
 FROM "LinhasIdentidadesExternas" m
 WHERE m."FonteEstruturalId"=a.fonte AND m."LinhaId"=a."LinhaId"
   AND m."Tipo" IN ('ROUTE_AGENCY','ROUTE_ORIGIN_V1')
   AND starts_with(m."ExternalId",a."ExternalId"||':')
) metadata ON true
LEFT JOIN "Sentidos" s ON s."LinhaId"=a."LinhaId"
LEFT JOIN "SentidosIdentidadesExternas" sie ON sie."FonteEstruturalId"=a.fonte
 AND sie."Tipo"='GTFS_DIRECTION' AND sie."SentidoId"=s."Id"
 AND sie."ExternalId" IN (a."ExternalId"||':0',a."ExternalId"||':1')
LEFT JOIN "PadroesOperacionais" p ON p."SentidoId"=s."Id"
LEFT JOIN "PadroesVersoes" v ON v."Id"=p."VersaoAtualId"
ORDER BY s."Id",p."Id" LIMIT 101;
-- Se vierem101 linhas, recortar por sentido/padrão antes de concluir completude.
ROLLBACK;
