-- SOMENTE descoberta offline de candidatos, NÃO certificação de qualidade/dataset.
-- Executar com transação READ ONLY em ambiente descartável, via Npgsql parametrizado.
-- @inicio/@fim/@cursor_ts timestamptz; @cursor_id uuid; @codigo text;
-- @tamanho int (1..1000). Horizonte é opção do gerador (padrão3600s).
-- Primeira página: cursor_ts=inicio, cursor_id=UUID vazio. Intervalo [inicio,fim).
-- Congelar snapshot de extração; a fonte precisa agrupar execução inteira e conferir journal.
WITH gps AS (
    SELECT t.* FROM "TelemetriasVeiculoMl" t
    WHERE t."CodigoLinha"=@codigo AND t."TimestampGps">=@inicio AND t."TimestampGps"<@fim
      AND (t."TimestampGps",t."Id")>(@cursor_ts,@cursor_id)
    ORDER BY t."TimestampGps",t."Id" LIMIT @tamanho
)
SELECT to_jsonb(t) AS gps, to_jsonb(h) AS passagem, j."Payload" AS journal,
       o."Id" AS ocorrencia_id, o."ParadaId" AS parada_id,
       o."PosicaoTracado" AS posicao_destino, v."Id" AS versao_id,
       v."PadraoOperacionalId" AS padrao_id, v."Topologia" AS topologia,
       s."Id" AS sentido_id, l."Id" AS linha_id, l."Codigo" AS codigo_linha,
       CASE WHEN t."PosicaoNaRota">=0 AND t."PosicaoNaRota"<o."PosicaoTracado"
                 AND o."PosicaoTracado"<=1
            THEN ST_Length(ST_LineSubstring(v."Geometria",t."PosicaoNaRota",o."PosicaoTracado")::geography)
       END AS distancia_rota_conferida_metros
FROM gps t
LEFT JOIN "OcorrenciasParadasPadroes" o ON o."Id"=t."ProximaOcorrenciaParadaPadraoId"
LEFT JOIN "PadroesVersoes" v ON v."Id"=o."PadraoVersaoId"
LEFT JOIN "PadroesOperacionais" p ON p."Id"=v."PadraoOperacionalId"
LEFT JOIN "Sentidos" s ON s."Id"=p."SentidoId"
LEFT JOIN "Linhas" l ON l."Id"=s."LinhaId"
LEFT JOIN "HistoricoPassagens" h ON h."ViagemId"=t."ViagemId"
    AND h."OcorrenciaParadaPadraoId"=t."ProximaOcorrenciaParadaPadraoId" AND h."Volta"=t."Volta"
LEFT JOIN "EventosViagem" j ON j."EventId"='passagem:'||h."ViagemId"::text||':'||
    h."OcorrenciaParadaPadraoId"::text||':'||h."Volta"::text
ORDER BY t."TimestampGps",t."Id";
-- Horizonte é validado pelo gerador, não pelo join: preservar tempos inválidos
-- para contabilizar descarte. Não usar TimestampRegistro/hora/velocidade futura como feature.
