using NoPonto.Application.GPS;

namespace NoPonto.Data.Repositories;

public sealed partial class GpsPadraoRepository
{
    private const string SqlMatchingCombinadoLoteSetBased = """
        WITH entradas AS MATERIALIZED (
          SELECT * FROM jsonb_to_recordset(@inputs::jsonb) AS x(
            input_id text,codigo text,lat double precision,lon double precision,bearing double precision,
            dist_max double precision,usar_anterior boolean,padrao_versao_id uuid,
            fracao_min double precision,fracao_max double precision,usar_operacional boolean,
            padrao_operacional uuid,validar_identidade_operacional boolean,
            padrao_operacional_esperado uuid,sentido_operacional_esperado uuid,
            linha_operacional_esperada uuid,posicao_operacional_anterior double precision,
            orcamento_operacional_metros double precision)
        ),
        pontos AS MATERIALIZED (
          SELECT e.*,ST_SetSRID(ST_MakePoint(e.lon,e.lat),4326) AS ponto_geom,
            ST_SetSRID(ST_MakePoint(e.lon,e.lat),4326)::geography AS ponto_geography
          FROM entradas e
        ),
        codigos AS MATERIALIZED (SELECT DISTINCT codigo FROM entradas),
        contexto_global AS MATERIALIZED (
          SELECT l."Codigo" codigo,pv."Id",po."Id" padrao_operacional_id,
            po."SentidoId" sentido_id,s."LinhaId" linha_id,pv."Topologia" topologia,
            pv."Geometria",pv."Geometria"::geography geometria_geography,
            ST_Length(pv."Geometria"::geography) comprimento_metros
          FROM codigos c JOIN "Linhas" l ON l."Codigo"=c.codigo
          JOIN "Sentidos" s ON s."LinhaId"=l."Id"
          JOIN "PadroesOperacionais" po ON po."SentidoId"=s."Id"
          JOIN "PadroesVersoes" pv ON pv."Id"=po."VersaoAtualId"
        ),
        versoes_requeridas AS MATERIALIZED (
          SELECT padrao_versao_id id FROM entradas WHERE usar_anterior
          UNION SELECT padrao_operacional FROM entradas WHERE usar_operacional
        ),
        contexto_versoes AS MATERIALIZED (
          SELECT pv."Id",po."Id" padrao_operacional_id,po."SentidoId" sentido_id,
            s."LinhaId" linha_id,l."Codigo" codigo,pv."Topologia" topologia,pv."Geometria",
            pv."Geometria"::geography geometria_geography,
            ST_Length(pv."Geometria"::geography) comprimento_metros
          FROM versoes_requeridas vr JOIN "PadroesVersoes" pv ON pv."Id"=vr.id
          JOIN "PadroesOperacionais" po ON po."Id"=pv."PadraoOperacionalId"
          JOIN "Sentidos" s ON s."Id"=po."SentidoId"
          JOIN "Linhas" l ON l."Id"=s."LinhaId"
        ),
        global_espacial AS (
          SELECT e.input_id,e.bearing,e.dist_max,e.ponto_geom,e.ponto_geography,c.*,
            ST_Distance(e.ponto_geography,c.geometria_geography) distancia_rota_metros,
            ST_LineLocatePoint(c."Geometria",e.ponto_geom) posicao_na_rota
          FROM pontos e JOIN contexto_global c ON c.codigo=e.codigo
          WHERE ST_Distance(e.ponto_geography,c.geometria_geography)<=e.dist_max
        ),
        global_bearing AS (
          SELECT c.*,degrees(ST_Azimuth(
            ST_LineInterpolatePoint(c."Geometria",GREATEST(0.0,LEAST(1.0,c.posicao_na_rota-0.025)))::geography,
            ST_LineInterpolatePoint(c."Geometria",GREATEST(0.0,LEAST(1.0,c.posicao_na_rota+0.025)))::geography)) bearing_local
          FROM global_espacial c
        ),
        global_diff AS (
          SELECT c.*,ABS(MOD((c.bearing_local-c.bearing+540.0)::numeric,360.0)-180.0) diff_bearing
          FROM global_bearing c
        ),
        global_rank AS (
          SELECT c.*,ROW_NUMBER() OVER(PARTITION BY input_id ORDER BY
            (diff_bearing/80.0)+(distancia_rota_metros/dist_max),"Id") ranking
          FROM global_diff c WHERE diff_bearing<80
        ),
        global_vencedor AS MATERIALIZED (
          SELECT c.*,ST_LineInterpolatePoint(c."Geometria",GREATEST(0.0,LEAST(1.0,c.posicao_na_rota))) ponto_rota
          FROM global_rank c WHERE ranking=1
        ),
        anterior_geometria AS (
          SELECT e.input_id,e.bearing,e.dist_max,e.ponto_geom,e.ponto_geography,c.*,
            ST_LineSubstring(c."Geometria",e.fracao_min,e.fracao_max) geometria_projecao,
            e.fracao_min,e.fracao_max
          FROM pontos e JOIN contexto_versoes c
            ON e.usar_anterior AND c."Id"=e.padrao_versao_id AND c.codigo=e.codigo
        ),
        anterior_espacial AS (
          SELECT a.*,ST_Distance(a.ponto_geography,a.geometria_projecao::geography) distancia_rota_metros,
            a.fracao_min+ST_LineLocatePoint(a.geometria_projecao,a.ponto_geom)
              *(a.fracao_max-a.fracao_min) posicao_na_rota
          FROM anterior_geometria a
          WHERE ST_Distance(a.ponto_geography,a.geometria_projecao::geography)<=a.dist_max
        ),
        anterior_bearing AS (
          SELECT a.*,degrees(ST_Azimuth(
            ST_LineInterpolatePoint(a."Geometria",GREATEST(0.0,LEAST(1.0,a.posicao_na_rota-0.025)))::geography,
            ST_LineInterpolatePoint(a."Geometria",GREATEST(0.0,LEAST(1.0,a.posicao_na_rota+0.025)))::geography)) bearing_local
          FROM anterior_espacial a
        ),
        anterior_vencedor AS MATERIALIZED (
          SELECT a.*,ST_LineInterpolatePoint(a."Geometria",GREATEST(0.0,LEAST(1.0,a.posicao_na_rota))) ponto_rota
          FROM anterior_bearing a
          WHERE ABS(MOD((a.bearing_local-a.bearing+540.0)::numeric,360.0)-180.0)<80
        ),
        operacional_geometria AS (
          SELECT e.input_id,e.ponto_geom,e.ponto_geography,e.dist_max,c.*,
            e.posicao_operacional_anterior,e.orcamento_operacional_metros,
            GREATEST(0.0,e.posicao_operacional_anterior-
              (e.orcamento_operacional_metros/c.comprimento_metros)) fracao_min,
            LEAST(1.0,e.posicao_operacional_anterior+
              (e.orcamento_operacional_metros/c.comprimento_metros)) fracao_max
          FROM pontos e JOIN contexto_versoes c
            ON e.usar_operacional AND c."Id"=e.padrao_operacional
            AND (NOT e.validar_identidade_operacional OR (
              c.padrao_operacional_id=e.padrao_operacional_esperado
              AND c.sentido_id=e.sentido_operacional_esperado
              AND c.linha_id=e.linha_operacional_esperada AND c.codigo=e.codigo))
          JOIN global_vencedor g ON g.input_id=e.input_id AND g."Id"<>e.padrao_operacional
          WHERE c.comprimento_metros>0
        ),
        operacional_vencedor AS MATERIALIZED (
          SELECT o.*,ST_Distance(o.ponto_geography,
              ST_LineSubstring(o."Geometria",o.fracao_min,o.fracao_max)::geography) distancia_rota_metros,
            o.fracao_min+ST_LineLocatePoint(
              ST_LineSubstring(o."Geometria",o.fracao_min,o.fracao_max),o.ponto_geom)
              *(o.fracao_max-o.fracao_min) posicao_na_rota
          FROM operacional_geometria o
          WHERE o.fracao_min<o.fracao_max AND ST_DWithin(
            ST_LineSubstring(o."Geometria",o.fracao_min,o.fracao_max)::geography,
            o.ponto_geography,o.dist_max)
        ),
        global_resultado AS (
          SELECT g.*,pp.* FROM global_vencedor g LEFT JOIN LATERAL (
            SELECT p."Nome" parada_nome,op."Id" ocorrencia_id,op."ParadaId" parada_id,
              op."Ordem" parada_ordem,op."DistanciaAcumuladaMetros" parada_distancia_acumulada,
              op."DistanciaDaLinhaMetros" parada_distancia_linha,
              ST_Distance(g.ponto_geography,p."Localizacao"::geography) distancia_parada_metros,
              GREATEST(0.0,CASE WHEN op."PosicaoTracado">g.posicao_na_rota THEN
                ST_Length(ST_LineSubstring(g."Geometria",0,op."PosicaoTracado")::geography)
                -ST_Length(ST_LineSubstring(g."Geometria",0,g.posicao_na_rota)::geography)
              ELSE g.comprimento_metros-ST_Length(ST_LineSubstring(g."Geometria",0,g.posicao_na_rota)::geography)
                +ST_Length(ST_LineSubstring(g."Geometria",0,op."PosicaoTracado")::geography) END)
                distancia_restante_rota_metros
            FROM "OcorrenciasParadasPadroes" op JOIN "Paradas" p ON p."Id"=op."ParadaId"
            WHERE op."PadraoVersaoId"=g."Id" AND
              (op."PosicaoTracado">g.posicao_na_rota OR g.topologia='CIRCULAR')
            ORDER BY CASE WHEN op."PosicaoTracado">g.posicao_na_rota THEN 0 ELSE 1 END,
              op."PosicaoTracado",op."Ordem" LIMIT 1) pp ON true
        ),
        anterior_resultado AS (
          SELECT a.*,pp.* FROM anterior_vencedor a LEFT JOIN LATERAL (
            SELECT p."Nome" parada_nome,op."Id" ocorrencia_id,op."ParadaId" parada_id,
              op."Ordem" parada_ordem,op."DistanciaAcumuladaMetros" parada_distancia_acumulada,
              op."DistanciaDaLinhaMetros" parada_distancia_linha,
              ST_Distance(a.ponto_geography,p."Localizacao"::geography) distancia_parada_metros,
              GREATEST(0.0,CASE WHEN op."PosicaoTracado">a.posicao_na_rota THEN
                ST_Length(ST_LineSubstring(a."Geometria",0,op."PosicaoTracado")::geography)
                -ST_Length(ST_LineSubstring(a."Geometria",0,a.posicao_na_rota)::geography)
              ELSE a.comprimento_metros-ST_Length(ST_LineSubstring(a."Geometria",0,a.posicao_na_rota)::geography)
                +ST_Length(ST_LineSubstring(a."Geometria",0,op."PosicaoTracado")::geography) END)
                distancia_restante_rota_metros
            FROM "OcorrenciasParadasPadroes" op JOIN "Paradas" p ON p."Id"=op."ParadaId"
            WHERE op."PadraoVersaoId"=a."Id" AND
              (op."PosicaoTracado">a.posicao_na_rota OR a.topologia='CIRCULAR')
            ORDER BY CASE WHEN op."PosicaoTracado">a.posicao_na_rota THEN 0 ELSE 1 END,
              op."PosicaoTracado",op."Ordem" LIMIT 1) pp ON true
        )
        SELECT input_id,'GLOBAL' ramo,"Id" padrao_versao_id,padrao_operacional_id,
          sentido_id,linha_id,topologia,posicao_na_rota,comprimento_metros,distancia_rota_metros,
          bearing_local,ST_Y(ponto_rota) lat_rota,ST_X(ponto_rota) lon_rota,
          parada_nome,ocorrencia_id,parada_id,parada_ordem,parada_distancia_acumulada,
          parada_distancia_linha,distancia_parada_metros,distancia_restante_rota_metros
        FROM global_resultado
        UNION ALL
        SELECT input_id,'ANTERIOR',"Id",padrao_operacional_id,sentido_id,linha_id,topologia,
          posicao_na_rota,comprimento_metros,distancia_rota_metros,bearing_local,
          ST_Y(ponto_rota),ST_X(ponto_rota),parada_nome,ocorrencia_id,parada_id,parada_ordem,
          parada_distancia_acumulada,parada_distancia_linha,distancia_parada_metros,
          distancia_restante_rota_metros FROM anterior_resultado
        UNION ALL
        SELECT input_id,'OPERACIONAL',"Id",NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,
          posicao_na_rota,comprimento_metros,distancia_rota_metros,NULL::double precision,
          NULL::double precision,NULL::double precision,NULL::text,NULL::uuid,NULL::uuid,NULL::integer,
          NULL::double precision,NULL::double precision,NULL::double precision,NULL::double precision
        FROM operacional_vencedor WHERE posicao_na_rota>=posicao_operacional_anterior
        ORDER BY input_id DESC,ramo DESC
        """;

}
