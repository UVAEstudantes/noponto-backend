using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using NoPonto.Application.GPS;

namespace NoPonto.Data.Repositories;

public sealed partial class GpsItinerarioRepository
{
    public const int TamanhoChunkMatchingPadrao = 100;

    // Divergencia intencional em relacao ao global individual: cancelamento do
    // batch sempre interrompe a operacao e nunca e convertido em inelegibilidade
    // nem dispara fallback. O matching individual produtivo permanece inalterado.

    // Seams internos e inertes em producao, usados apenas para provar falha e
    // cancelamento entre chunks sem deformar a SQL nominal.
    internal Action<TipoBatchMatching, int>? AntesDoComandoBatchParaTeste { get; init; }
    internal Action<TipoBatchMatching, int>? AposChunkParaTeste { get; init; }
    internal Action<TipoBatchMatching, string>? AntesDoFallbackIndividualParaTeste { get; init; }

    public Task<ResultadoMatchingLote<ResultadoMatchingGlobalLote>> BuscarGlobaisEmLoteAsync(
        IReadOnlyList<EntradaMatchingGlobalLote> entradas,
        int tamanhoChunk = TamanhoChunkMatchingPadrao,
        CancellationToken cancellationToken = default) =>
        ExecutarGlobaisEmLoteAsync(entradas, tamanhoChunk, cancellationToken);

    public Task<ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>> BuscarDirecionadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingDirecionadoLote> entradas,
        int tamanhoChunk = TamanhoChunkMatchingPadrao,
        CancellationToken cancellationToken = default) =>
        ExecutarDirecionadosEmLoteAsync(entradas, tamanhoChunk, cancellationToken);

    public async Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>> BuscarCombinadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingCombinadoLote> entradas,
        int tamanhoChunk = TamanhoChunkMatchingPadrao,
        CancellationToken cancellationToken = default)
    {
        ValidarLote(entradas, tamanhoChunk, x => x.InputId);
        cancellationToken.ThrowIfCancellationRequested();
        var resultados = new Dictionary<string, ResultadoMatchingCombinado>(StringComparer.Ordinal);
        var comandos = new List<MetricaComandoMatchingLote>();

        foreach (var entrada in entradas)
        {
            if (!entrada.Bearing.HasValue)
            {
                resultados[entrada.InputId] = new(
                    ResultadoBuscaItinerario.NotEligible(),
                    ResultadoBuscaItinerario.NotEligible(),
                    entrada.ProjecaoOperacional.HasValue
                        ? ResultadoProjecaoOperacional.Inelegivel()
                        : ResultadoProjecaoOperacional.NaoSolicitada());
            }
            else if (!DadosBasicosValidos(entrada.Latitude, entrada.Longitude,
                         entrada.Bearing.Value, entrada.DistanciaMaximaMetros)
                || (entrada.Faixa is { } faixa && !faixa.Valida)
                || (entrada.ProjecaoOperacional is { } operacional && !operacional.Valida))
            {
                resultados[entrada.InputId] = FalhaCombinada(entrada.ProjecaoOperacional.HasValue);
            }
        }

        var validas = entradas.Where(x => x.Bearing.HasValue
            && DadosBasicosValidos(x.Latitude, x.Longitude, x.Bearing.Value,
                x.DistanciaMaximaMetros)
            && (x.Faixa is not { } faixa || faixa.Valida)
            && (x.ProjecaoOperacional is not { } operacional || operacional.Valida)).ToArray();

        var numeroChunk = 0;
        foreach (var chunk in validas.Chunk(tamanhoChunk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            numeroChunk++;
            var inicio = Stopwatch.GetTimestamp();
            var comandoBatchRegistrado = false;
            try
            {
                AntesDoComandoBatchParaTeste?.Invoke(TipoBatchMatching.Combinado, numeroChunk);
                cancellationToken.ThrowIfCancellationRequested();
                await ExecutarCombinadoChunkAsync(chunk, resultados, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                comandos.Add(new(TipoBatchMatching.Combinado,
                    OrigemComandoMatchingLote.Batch, chunk.Length,
                    Stopwatch.GetElapsedTime(inicio)));
                comandoBatchRegistrado = true;
                _logger.LogWarning(ex, "Falha no matching combinado em lote com {quantidade} entradas.", chunk.Length);
                foreach (var entrada in chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AntesDoFallbackIndividualParaTeste?.Invoke(
                        TipoBatchMatching.Combinado, entrada.InputId);
                    var inicioFallback = Stopwatch.GetTimestamp();
                    try
                    {
                        resultados[entrada.InputId] = await BuscarMatchingCombinadoAsync(
                            entrada.CodigoLinha, entrada.ItinerarioAnteriorId,
                            entrada.Latitude, entrada.Longitude, entrada.Bearing!.Value,
                            entrada.DistanciaMaximaMetros, entrada.Faixa,
                            entrada.ProjecaoOperacional, cancellationToken);
                    }
                    finally
                    {
                        comandos.Add(new(TipoBatchMatching.Combinado,
                            OrigemComandoMatchingLote.FallbackIndividual, 1,
                            Stopwatch.GetElapsedTime(inicioFallback)));
                    }
                }
            }
            finally
            {
                if (!comandoBatchRegistrado)
                    comandos.Add(new(TipoBatchMatching.Combinado,
                        OrigemComandoMatchingLote.Batch, chunk.Length,
                        Stopwatch.GetElapsedTime(inicio)));
            }
            AposChunkParaTeste?.Invoke(TipoBatchMatching.Combinado, numeroChunk);
        }

        return new(entradas.Select(x => new ResultadoMatchingCombinadoLote(
            x.InputId, resultados[x.InputId])).ToArray(), new(entradas.Count, comandos));
    }

    private async Task<ResultadoMatchingLote<ResultadoMatchingGlobalLote>> ExecutarGlobaisEmLoteAsync(
        IReadOnlyList<EntradaMatchingGlobalLote> entradas,
        int tamanhoChunk,
        CancellationToken cancellationToken)
    {
        ValidarLote(entradas, tamanhoChunk, x => x.InputId);
        cancellationToken.ThrowIfCancellationRequested();
        var resultados = entradas.ToDictionary(x => x.InputId,
            _ => ResultadoBuscaItinerario.NotEligible(), StringComparer.Ordinal);
        var comandos = new List<MetricaComandoMatchingLote>();
        var validas = entradas.Where(x => x.Bearing.HasValue
            && DadosBasicosValidos(x.Latitude, x.Longitude, x.Bearing.Value,
                x.DistanciaMaximaMetros)).ToArray();

        var numeroChunk = 0;
        foreach (var chunk in validas.Chunk(tamanhoChunk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            numeroChunk++;
            var inicio = Stopwatch.GetTimestamp();
            var comandoBatchRegistrado = false;
            try
            {
                AntesDoComandoBatchParaTeste?.Invoke(TipoBatchMatching.GlobalSimples, numeroChunk);
                cancellationToken.ThrowIfCancellationRequested();
                var json = JsonSerializer.Serialize(chunk.Select(x => new
                {
                    input_id = x.InputId, codigo = x.CodigoLinha, lat = x.Latitude,
                    lon = x.Longitude, bearing = x.Bearing!.Value,
                    dist_max = x.DistanciaMaximaMetros, itinerario_id = Guid.Empty,
                    usar_faixa = false, fracao_min = 0d, fracao_max = 1d
                }));
                await ExecutarRotaChunkAsync(json, direcionado: false, resultados, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                comandos.Add(new(TipoBatchMatching.GlobalSimples,
                    OrigemComandoMatchingLote.Batch, chunk.Length,
                    Stopwatch.GetElapsedTime(inicio)));
                comandoBatchRegistrado = true;
                _logger.LogWarning(ex, "Falha no matching global em lote com {quantidade} entradas.", chunk.Length);
                foreach (var entrada in chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AntesDoFallbackIndividualParaTeste?.Invoke(
                        TipoBatchMatching.GlobalSimples, entrada.InputId);
                    var inicioFallback = Stopwatch.GetTimestamp();
                    try
                    {
                        var rota = await BuscarEnriquecimentoAsync(entrada.CodigoLinha,
                            entrada.Latitude, entrada.Longitude, entrada.Bearing!.Value,
                            entrada.DistanciaMaximaMetros, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        resultados[entrada.InputId] = rota is null
                            ? ResultadoBuscaItinerario.NotEligible()
                            : ResultadoBuscaItinerario.Found(rota);
                    }
                    finally
                    {
                        comandos.Add(new(TipoBatchMatching.GlobalSimples,
                            OrigemComandoMatchingLote.FallbackIndividual, 1,
                            Stopwatch.GetElapsedTime(inicioFallback)));
                    }
                }
            }
            finally
            {
                if (!comandoBatchRegistrado)
                    comandos.Add(new(TipoBatchMatching.GlobalSimples,
                        OrigemComandoMatchingLote.Batch, chunk.Length,
                        Stopwatch.GetElapsedTime(inicio)));
            }
            AposChunkParaTeste?.Invoke(TipoBatchMatching.GlobalSimples, numeroChunk);
        }

        return new(entradas.Select(x => new ResultadoMatchingGlobalLote(
            x.InputId, resultados[x.InputId])).ToArray(), new(entradas.Count, comandos));
    }

    private async Task<ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>> ExecutarDirecionadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingDirecionadoLote> entradas,
        int tamanhoChunk,
        CancellationToken cancellationToken)
    {
        ValidarLote(entradas, tamanhoChunk, x => x.InputId);
        cancellationToken.ThrowIfCancellationRequested();
        var resultados = new Dictionary<string, ResultadoBuscaItinerario>(StringComparer.Ordinal);
        var comandos = new List<MetricaComandoMatchingLote>();
        foreach (var entrada in entradas)
            resultados[entrada.InputId] = !entrada.Bearing.HasValue
                ? ResultadoBuscaItinerario.NotEligible()
                : !DadosBasicosValidos(entrada.Latitude, entrada.Longitude,
                      entrada.Bearing.Value, entrada.DistanciaMaximaMetros)
                    || entrada.Faixa is { } faixa && !faixa.Valida
                    ? ResultadoBuscaItinerario.InfrastructureFailure()
                    : ResultadoBuscaItinerario.NotEligible();

        var validas = entradas.Where(x => x.Bearing.HasValue
            && DadosBasicosValidos(x.Latitude, x.Longitude, x.Bearing.Value,
                x.DistanciaMaximaMetros)
            && (x.Faixa is not { } faixa || faixa.Valida)).ToArray();
        var numeroChunk = 0;
        foreach (var chunk in validas.Chunk(tamanhoChunk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            numeroChunk++;
            var inicio = Stopwatch.GetTimestamp();
            var comandoBatchRegistrado = false;
            try
            {
                AntesDoComandoBatchParaTeste?.Invoke(TipoBatchMatching.Direcionado, numeroChunk);
                cancellationToken.ThrowIfCancellationRequested();
                var json = JsonSerializer.Serialize(chunk.Select(x => new
                {
                    input_id = x.InputId, codigo = x.CodigoLinha, lat = x.Latitude,
                    lon = x.Longitude, bearing = x.Bearing!.Value,
                    dist_max = x.DistanciaMaximaMetros, itinerario_id = x.ItinerarioId,
                    usar_faixa = x.Faixa.HasValue, fracao_min = x.Faixa?.Min ?? 0d,
                    fracao_max = x.Faixa?.Max ?? 1d
                }));
                await ExecutarRotaChunkAsync(json, direcionado: true, resultados, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                comandos.Add(new(TipoBatchMatching.Direcionado,
                    OrigemComandoMatchingLote.Batch, chunk.Length,
                    Stopwatch.GetElapsedTime(inicio)));
                comandoBatchRegistrado = true;
                _logger.LogWarning(ex, "Falha no matching direcionado em lote com {quantidade} entradas.", chunk.Length);
                foreach (var entrada in chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AntesDoFallbackIndividualParaTeste?.Invoke(
                        TipoBatchMatching.Direcionado, entrada.InputId);
                    var inicioFallback = Stopwatch.GetTimestamp();
                    try
                    {
                        resultados[entrada.InputId] = await BuscarEnriquecimentoDoItinerarioAsync(
                            entrada.CodigoLinha, entrada.ItinerarioId,
                            entrada.Latitude, entrada.Longitude, entrada.Bearing!.Value,
                            entrada.DistanciaMaximaMetros, cancellationToken, entrada.Faixa);
                    }
                    finally
                    {
                        comandos.Add(new(TipoBatchMatching.Direcionado,
                            OrigemComandoMatchingLote.FallbackIndividual, 1,
                            Stopwatch.GetElapsedTime(inicioFallback)));
                    }
                }
            }
            finally
            {
                if (!comandoBatchRegistrado)
                    comandos.Add(new(TipoBatchMatching.Direcionado,
                        OrigemComandoMatchingLote.Batch, chunk.Length,
                        Stopwatch.GetElapsedTime(inicio)));
            }
            AposChunkParaTeste?.Invoke(TipoBatchMatching.Direcionado, numeroChunk);
        }

        return new(entradas.Select(x => new ResultadoMatchingDirecionadoLote(
            x.InputId, resultados[x.InputId])).ToArray(), new(entradas.Count, comandos));
    }

    private async Task ExecutarRotaChunkAsync(
        string json,
        bool direcionado,
        Dictionary<string, ResultadoBuscaItinerario> resultados,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH inputs AS (
                SELECT * FROM jsonb_to_recordset(@inputs::jsonb) AS x(
                    input_id text, codigo text, lat double precision, lon double precision,
                    bearing double precision, dist_max double precision, itinerario_id uuid,
                    usar_faixa boolean, fracao_min double precision, fracao_max double precision)
            )
            SELECT entrada.input_id, escolhido.*
            FROM inputs entrada
            LEFT JOIN LATERAL (
                WITH veiculo AS (
                    SELECT
                        ST_SetSRID(ST_MakePoint(entrada.lon, entrada.lat), 4326)::geography AS ponto,
                        ST_SetSRID(ST_MakePoint(entrada.lon, entrada.lat), 4326) AS ponto_geom
                ),
                rotas AS (
                    SELECT i."Id", i."Geometria"
                    FROM "Itinerarios" i
                    JOIN "Sentidos" s ON s."Id" = i."SentidoId"
                    JOIN "Linhas" l ON l."Id" = s."LinhaId"
                    WHERE l."Codigo" = entrada.codigo
                    /*FILTRO_ITINERARIO*/
                ),
                geometrias_projecao AS (
                    SELECT r.*,
                        CASE WHEN entrada.usar_faixa
                            THEN ST_LineSubstring(r."Geometria", entrada.fracao_min, entrada.fracao_max)
                            ELSE r."Geometria" END AS geometria_projecao
                    FROM rotas r
                ),
                candidatos AS (
                    SELECT r."Id", r."Geometria",
                        ST_Length(r."Geometria"::geography) AS comprimento_metros,
                        ST_Distance(v.ponto, r.geometria_projecao::geography) AS distancia_rota_metros,
                        CASE WHEN entrada.usar_faixa THEN
                            entrada.fracao_min + ST_LineLocatePoint(r.geometria_projecao, v.ponto_geom)
                                * (entrada.fracao_max - entrada.fracao_min)
                            ELSE ST_LineLocatePoint(r.geometria_projecao, v.ponto_geom)
                        END AS posicao_na_rota
                    FROM geometrias_projecao r CROSS JOIN veiculo v
                    WHERE ST_Distance(v.ponto, r.geometria_projecao::geography) <= entrada.dist_max
                ),
                com_bearing_local AS (
                    SELECT c.*, degrees(ST_Azimuth(
                        ST_LineInterpolatePoint(c."Geometria", GREATEST(0.0, c.posicao_na_rota - 0.025))::geography,
                        ST_LineInterpolatePoint(c."Geometria", LEAST(1.0, c.posicao_na_rota + 0.025))::geography
                    )) AS bearing_local FROM candidatos c
                ),
                com_diff_bearing AS (
                    SELECT cb.*, ABS(MOD((cb.bearing_local - entrada.bearing + 540.0)::numeric, 360.0) - 180.0) AS diff_bearing
                    FROM com_bearing_local cb
                ),
                com_score AS (
                    SELECT cd.*, (cd.diff_bearing / 80.0) + (cd.distancia_rota_metros / entrada.dist_max) AS score
                    FROM com_diff_bearing cd WHERE cd.diff_bearing < 80
                ),
                itinerario_escolhido AS (
                    SELECT cs.*, ST_LineInterpolatePoint(cs."Geometria", cs.posicao_na_rota) AS ponto_rota
                    FROM com_score cs ORDER BY cs.score ASC LIMIT 1
                ),
                proxima_parada AS (
                    SELECT p."Nome" AS parada_nome,
                        ST_Distance(v.ponto, p."Localizacao"::geography) AS distancia_parada_metros
                    FROM "ParadasItinerario" pi
                    JOIN "Paradas" p ON p."Id" = pi."ParadaId"
                    JOIN itinerario_escolhido ie ON ie."Id" = pi."ItinerarioId"
                    CROSS JOIN veiculo v
                    WHERE pi."PosicaoLinha" > ie.posicao_na_rota
                    ORDER BY pi."PosicaoLinha" ASC LIMIT 1
                )
                SELECT ie."Id" AS itinerario_id, ie.posicao_na_rota, ie.comprimento_metros,
                    ie.distancia_rota_metros, ie.bearing_local,
                    ST_Y(ie.ponto_rota) AS lat_rota, ST_X(ie.ponto_rota) AS lon_rota,
                    pp.parada_nome, pp.distancia_parada_metros
                FROM itinerario_escolhido ie LEFT JOIN proxima_parada pp ON true LIMIT 1
            ) escolhido ON true
            ORDER BY entrada.input_id DESC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.Replace("/*FILTRO_ITINERARIO*/",
            direcionado ? "AND i.\"Id\" = entrada.itinerario_id" : "");
        cmd.Parameters.AddWithValue("inputs", NpgsqlDbType.Jsonb, json);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var inputId = reader.GetString(reader.GetOrdinal("input_id"));
            resultados[inputId] = reader.IsDBNull(reader.GetOrdinal("itinerario_id"))
                ? ResultadoBuscaItinerario.NotEligible()
                : ResultadoBuscaItinerario.Found(LerRota(reader));
        }
    }

    private async Task ExecutarCombinadoChunkAsync(
        EntradaMatchingCombinadoLote[] chunk,
        Dictionary<string, ResultadoMatchingCombinado> resultados,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(chunk.Select(x => new
        {
            input_id = x.InputId, codigo = x.CodigoLinha, lat = x.Latitude,
            lon = x.Longitude, bearing = x.Bearing!.Value,
            dist_max = x.DistanciaMaximaMetros,
            usar_anterior = x.ItinerarioAnteriorId.HasValue && x.Faixa.HasValue,
            itinerario_id = x.ItinerarioAnteriorId ?? Guid.Empty,
            fracao_min = x.Faixa?.Min ?? 0d, fracao_max = x.Faixa?.Max ?? 1d,
            usar_operacional = x.ProjecaoOperacional.HasValue,
            itinerario_operacional = x.ProjecaoOperacional?.ItinerarioId ?? Guid.Empty,
            posicao_operacional_anterior = x.ProjecaoOperacional?.PosicaoAnterior ?? 0d,
            orcamento_operacional_metros = x.ProjecaoOperacional?.OrcamentoMetros ?? 1d
        }));

        foreach (var entrada in chunk)
            resultados[entrada.InputId] = new(
                ResultadoBuscaItinerario.NotEligible(), ResultadoBuscaItinerario.NotEligible(),
                entrada.ProjecaoOperacional.HasValue
                    ? ResultadoProjecaoOperacional.Inelegivel()
                    : ResultadoProjecaoOperacional.NaoSolicitada());

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SqlMatchingCombinadoLote;
        cmd.Parameters.AddWithValue("inputs", NpgsqlDbType.Jsonb, json);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var inputId = reader.GetString(reader.GetOrdinal("input_id"));
            var atual = resultados[inputId];
            switch (reader.GetString(reader.GetOrdinal("ramo")))
            {
                case "GLOBAL": atual = atual with { Global = ResultadoBuscaItinerario.Found(LerRota(reader)) }; break;
                case "ANTERIOR": atual = atual with { Anterior = ResultadoBuscaItinerario.Found(LerRota(reader)) }; break;
                case "OPERACIONAL":
                    atual = atual with { Operacional = ResultadoProjecaoOperacional.Encontrada(new(
                        reader.GetGuid(reader.GetOrdinal("itinerario_id")),
                        reader.GetDouble(reader.GetOrdinal("posicao_na_rota")),
                        reader.GetDouble(reader.GetOrdinal("distancia_rota_metros")),
                        reader.GetDouble(reader.GetOrdinal("comprimento_metros")))) };
                    break;
                default: throw new InvalidOperationException("Ramo inesperado no matching combinado em lote.");
            }
            resultados[inputId] = atual;
        }
    }

    private static void ValidarLote<T>(IReadOnlyList<T> entradas, int tamanhoChunk, Func<T, string> inputId)
    {
        ArgumentNullException.ThrowIfNull(entradas);
        if (tamanhoChunk <= 0) throw new ArgumentOutOfRangeException(nameof(tamanhoChunk));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entrada in entradas)
        {
            var id = inputId(entrada);
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("InputId e obrigatorio.", nameof(entradas));
            if (!ids.Add(id)) throw new ArgumentException($"InputId duplicado no lote: {id}", nameof(entradas));
        }
    }

    private static bool DadosBasicosValidos(
        double latitude, double longitude, double bearing, double distanciaMaximaMetros) =>
        double.IsFinite(latitude) && latitude is >= -90 and <= 90
        && double.IsFinite(longitude) && longitude is >= -180 and <= 180
        && double.IsFinite(bearing)
        && double.IsFinite(distanciaMaximaMetros);

    private const string SqlMatchingCombinadoLote = """
        WITH inputs AS (
            SELECT * FROM jsonb_to_recordset(@inputs::jsonb) AS x(
                input_id text, codigo text, lat double precision, lon double precision,
                bearing double precision, dist_max double precision,
                usar_anterior boolean, itinerario_id uuid,
                fracao_min double precision, fracao_max double precision,
                usar_operacional boolean, itinerario_operacional uuid,
                posicao_operacional_anterior double precision,
                orcamento_operacional_metros double precision)
        )
        SELECT entrada.input_id, resultado.*
        FROM inputs entrada
        CROSS JOIN LATERAL (
            WITH veiculo AS (
                SELECT ST_SetSRID(ST_MakePoint(entrada.lon, entrada.lat),4326)::geography AS ponto,
                       ST_SetSRID(ST_MakePoint(entrada.lon, entrada.lat),4326) AS ponto_geom
            ),
            rotas_global AS (
                SELECT i."Id", i."Geometria" FROM "Itinerarios" i
                JOIN "Sentidos" s ON s."Id"=i."SentidoId"
                JOIN "Linhas" l ON l."Id"=s."LinhaId"
                WHERE l."Codigo"=entrada.codigo
            ),
            candidatos_global AS (
                SELECT r."Id",r."Geometria",ST_Length(r."Geometria"::geography) AS comprimento_metros,
                    ST_Distance(v.ponto,r."Geometria"::geography) AS distancia_rota_metros,
                    ST_LineLocatePoint(r."Geometria",v.ponto_geom) AS posicao_na_rota
                FROM rotas_global r CROSS JOIN veiculo v
                WHERE ST_Distance(v.ponto,r."Geometria"::geography)<=entrada.dist_max
            ),
            bearing_global AS (
                SELECT c.*,degrees(ST_Azimuth(
                    ST_LineInterpolatePoint(c."Geometria",GREATEST(0.0,c.posicao_na_rota-0.025))::geography,
                    ST_LineInterpolatePoint(c."Geometria",LEAST(1.0,c.posicao_na_rota+0.025))::geography)) AS bearing_local
                FROM candidatos_global c
            ),
            diff_global AS (
                SELECT bg.*,ABS(MOD((bg.bearing_local-entrada.bearing+540.0)::numeric,360.0)-180.0) AS diff_bearing
                FROM bearing_global bg
            ),
            score_global AS (
                SELECT dg.*,(dg.diff_bearing/80.0)+(dg.distancia_rota_metros/entrada.dist_max) AS score
                FROM diff_global dg WHERE dg.diff_bearing<80
            ),
            global_escolhido AS (
                SELECT sg.*,ST_LineInterpolatePoint(sg."Geometria",sg.posicao_na_rota) AS ponto_rota
                FROM score_global sg ORDER BY sg.score ASC LIMIT 1
            ),
            proxima_parada_global AS (
                SELECT p."Nome" AS parada_nome,ST_Distance(v.ponto,p."Localizacao"::geography) AS distancia_parada_metros
                FROM "ParadasItinerario" pi JOIN "Paradas" p ON p."Id"=pi."ParadaId"
                JOIN global_escolhido ge ON ge."Id"=pi."ItinerarioId" CROSS JOIN veiculo v
                WHERE pi."PosicaoLinha">ge.posicao_na_rota ORDER BY pi."PosicaoLinha" ASC LIMIT 1
            ),
            rota_anterior AS (
                SELECT i."Id",i."Geometria" FROM "Itinerarios" i
                JOIN "Sentidos" s ON s."Id"=i."SentidoId"
                JOIN "Linhas" l ON l."Id"=s."LinhaId"
                WHERE entrada.usar_anterior AND l."Codigo"=entrada.codigo AND i."Id"=entrada.itinerario_id
            ),
            geometria_anterior AS (
                SELECT r.*,ST_LineSubstring(r."Geometria",entrada.fracao_min,entrada.fracao_max) AS geometria_projecao
                FROM rota_anterior r
            ),
            candidatos_anterior AS (
                SELECT r."Id",r."Geometria",ST_Length(r."Geometria"::geography) AS comprimento_metros,
                    ST_Distance(v.ponto,r.geometria_projecao::geography) AS distancia_rota_metros,
                    entrada.fracao_min+ST_LineLocatePoint(r.geometria_projecao,v.ponto_geom)
                        *(entrada.fracao_max-entrada.fracao_min) AS posicao_na_rota
                FROM geometria_anterior r CROSS JOIN veiculo v
                WHERE ST_Distance(v.ponto,r.geometria_projecao::geography)<=entrada.dist_max
            ),
            bearing_anterior AS (
                SELECT c.*,degrees(ST_Azimuth(
                    ST_LineInterpolatePoint(c."Geometria",GREATEST(0.0,c.posicao_na_rota-0.025))::geography,
                    ST_LineInterpolatePoint(c."Geometria",LEAST(1.0,c.posicao_na_rota+0.025))::geography)) AS bearing_local
                FROM candidatos_anterior c
            ),
            diff_anterior AS (
                SELECT ba.*,ABS(MOD((ba.bearing_local-entrada.bearing+540.0)::numeric,360.0)-180.0) AS diff_bearing
                FROM bearing_anterior ba
            ),
            score_anterior AS (
                SELECT da.*,(da.diff_bearing/80.0)+(da.distancia_rota_metros/entrada.dist_max) AS score
                FROM diff_anterior da WHERE da.diff_bearing<80
            ),
            anterior_escolhido AS (
                SELECT sa.*,ST_LineInterpolatePoint(sa."Geometria",sa.posicao_na_rota) AS ponto_rota
                FROM score_anterior sa ORDER BY sa.score ASC LIMIT 1
            ),
            proxima_parada_anterior AS (
                SELECT p."Nome" AS parada_nome,ST_Distance(v.ponto,p."Localizacao"::geography) AS distancia_parada_metros
                FROM "ParadasItinerario" pi JOIN "Paradas" p ON p."Id"=pi."ParadaId"
                JOIN anterior_escolhido ae ON ae."Id"=pi."ItinerarioId" CROSS JOIN veiculo v
                WHERE pi."PosicaoLinha">ae.posicao_na_rota ORDER BY pi."PosicaoLinha" ASC LIMIT 1
            ),
            rota_operacional AS (
                SELECT i."Id",i."Geometria",ST_Length(i."Geometria"::geography) AS comprimento_metros
                FROM "Itinerarios" i WHERE entrada.usar_operacional
                  AND i."Id"=entrada.itinerario_operacional
                  AND EXISTS(SELECT 1 FROM global_escolhido ge WHERE ge."Id"<>entrada.itinerario_operacional)
            ),
            geometria_operacional AS (
                SELECT ro.*,
                    GREATEST(0.0,entrada.posicao_operacional_anterior-(entrada.orcamento_operacional_metros/ro.comprimento_metros)) AS fracao_min,
                    LEAST(1.0,entrada.posicao_operacional_anterior+(entrada.orcamento_operacional_metros/ro.comprimento_metros)) AS fracao_max
                FROM rota_operacional ro WHERE ro.comprimento_metros>0
            ),
            projecao_operacional AS (
                SELECT go.*,ST_Distance(v.ponto,ST_LineSubstring(go."Geometria",go.fracao_min,go.fracao_max)::geography) AS distancia_rota_metros,
                    go.fracao_min+ST_LineLocatePoint(ST_LineSubstring(go."Geometria",go.fracao_min,go.fracao_max),v.ponto_geom)
                        *(go.fracao_max-go.fracao_min) AS posicao_na_rota
                FROM geometria_operacional go CROSS JOIN veiculo v
                WHERE go.fracao_min<go.fracao_max AND ST_DWithin(
                    ST_LineSubstring(go."Geometria",go.fracao_min,go.fracao_max)::geography,v.ponto,entrada.dist_max)
            ),
            operacional_elegivel AS (
                SELECT * FROM projecao_operacional WHERE posicao_na_rota>=entrada.posicao_operacional_anterior
            )
            SELECT 'GLOBAL'::text AS ramo,ge."Id" AS itinerario_id,ge.posicao_na_rota,
                ge.comprimento_metros,ge.distancia_rota_metros,ge.bearing_local,
                ST_Y(ge.ponto_rota) AS lat_rota,ST_X(ge.ponto_rota) AS lon_rota,
                ppg.parada_nome,ppg.distancia_parada_metros
            FROM global_escolhido ge LEFT JOIN proxima_parada_global ppg ON true
            UNION ALL
            SELECT 'ANTERIOR',ae."Id",ae.posicao_na_rota,ae.comprimento_metros,ae.distancia_rota_metros,
                ae.bearing_local,ST_Y(ae.ponto_rota),ST_X(ae.ponto_rota),ppa.parada_nome,ppa.distancia_parada_metros
            FROM anterior_escolhido ae LEFT JOIN proxima_parada_anterior ppa ON true
            UNION ALL
            SELECT 'OPERACIONAL',oe."Id",oe.posicao_na_rota,oe.comprimento_metros,oe.distancia_rota_metros,
                NULL::double precision,NULL::double precision,NULL::double precision,NULL::text,NULL::double precision
            FROM operacional_elegivel oe
        ) resultado
        ORDER BY entrada.input_id DESC, resultado.ramo DESC
        """;
}
