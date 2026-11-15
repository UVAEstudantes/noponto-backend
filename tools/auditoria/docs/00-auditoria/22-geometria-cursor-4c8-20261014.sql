-- PREPARADA, NÃO EXECUTADA. Uma versão por execução; repetir apenas para as quatro
-- versões existentes no log 4C7 query.version. Parâmetros Npgsql tipados:
-- @versao uuid, @cursor_id uuid (query.ultima_id), @cursor_ordem int,
-- @anterior double precision, @atual double precision (query.anterior/atual).
-- Usar valores completos do log, não os arredondados da tabela de resumo.
-- SELECT somente: sem coordenadas, geometria bruta, identificadores físicos ou ambiente.
WITH sequencia AS MATERIALIZED (
  SELECT o.*, lag(o."PosicaoTracado") OVER (ORDER BY o."Ordem", o."Id") AS fracao_anterior,
    max(o."Ordem") OVER () AS ordem_terminal, count(*) OVER () AS quantidade
  FROM "OcorrenciasParadasPadroes" o WHERE o."PadraoVersaoId" = @versao
), cursor AS (
  SELECT "PosicaoTracado" AS fracao_cursor FROM sequencia
  WHERE "Id" = @cursor_id AND "Ordem" = @cursor_ordem
)
SELECT v."Id" AS versao, v."PadraoOperacionalId", v."Numero", v."Topologia",
  v."MetodoConstrucao", v."AlgoritmoVersao", v."HashEstrutural", v."ComprimentoMetros",
  v."ResultadoValidacao", v."CriadoEmUtc", v."PublicadoEmUtc",
  s."Id" AS ocorrencia, s."ParadaId", s."Ordem", s."SourceSequence",
  s."PosicaoTracado", s."DistanciaAcumuladaMetros", s."DistanciaDaLinhaMetros",
  s."SourceShapeDistTraveledMetros", s.ordem_terminal, s.quantidade,
  c.fracao_cursor, c.fracao_cursor <= @anterior AS cursor_nao_adiantado,
  s."PosicaoTracado" < s.fracao_anterior AS regressao_na_sequencia,
  s."Ordem" = s.ordem_terminal AS terminal,
  s."Ordem" > @cursor_ordem AS depois_cursor,
  s."PosicaoTracado" > @anterior AS acima_anterior,
  s."PosicaoTracado" <= @atual AS ate_atual,
  (@atual >= @anterior AND s."Ordem" > @cursor_ordem
    AND s."PosicaoTracado" > @anterior AND s."PosicaoTracado" <= @atual) AS cruzamento_linear
FROM "PadroesVersoes" v JOIN sequencia s ON s."PadraoVersaoId" = v."Id"
LEFT JOIN cursor c ON true
WHERE v."Id" = @versao ORDER BY s."Ordem";
