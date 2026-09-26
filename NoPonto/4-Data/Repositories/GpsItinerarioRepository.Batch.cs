using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using NoPonto.Application.GPS;

namespace NoPonto.Data.Repositories;

public sealed partial class GpsPadraoRepository
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
        ExecutarGlobaisEmLoteAsync(entradas, tamanhoChunk, cancellationToken, null);

    public Task<ResultadoMatchingLote<ResultadoMatchingGlobalLote>> BuscarGlobaisEmLoteAsync(
        IReadOnlyList<EntradaMatchingGlobalLote> entradas, int tamanhoChunk,
        CancellationToken cancellationToken, MatchingBatchStageProtection protecao) =>
        ExecutarGlobaisEmLoteAsync(entradas, tamanhoChunk, cancellationToken, protecao);

    public Task<ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>> BuscarDirecionadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingDirecionadoLote> entradas,
        int tamanhoChunk = TamanhoChunkMatchingPadrao,
        CancellationToken cancellationToken = default) =>
        ExecutarDirecionadosEmLoteAsync(entradas, tamanhoChunk, cancellationToken, null);

    public Task<ResultadoMatchingLote<ResultadoMatchingDirecionadoLote>> BuscarDirecionadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingDirecionadoLote> entradas, int tamanhoChunk,
        CancellationToken cancellationToken, MatchingBatchStageProtection protecao) =>
        ExecutarDirecionadosEmLoteAsync(entradas, tamanhoChunk, cancellationToken, protecao);

    public async Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>> BuscarCombinadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingCombinadoLote> entradas,
        int tamanhoChunk = TamanhoChunkMatchingPadrao,
        CancellationToken cancellationToken = default)
        => await ExecutarCombinadosEmLoteAsync(entradas, tamanhoChunk, cancellationToken, null);

    public Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>> BuscarCombinadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingCombinadoLote> entradas, int tamanhoChunk,
        CancellationToken cancellationToken, MatchingBatchStageProtection protecao) =>
        ExecutarCombinadosEmLoteAsync(entradas, tamanhoChunk, cancellationToken, protecao);

    private async Task<ResultadoMatchingLote<ResultadoMatchingCombinadoLote>> ExecutarCombinadosEmLoteAsync(
        IReadOnlyList<EntradaMatchingCombinadoLote> entradas, int tamanhoChunk,
        CancellationToken cancellationToken, MatchingBatchStageProtection? protecao)
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
                    ResultadoBuscaPadrao.NotEligible(),
                    ResultadoBuscaPadrao.NotEligible(),
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
            if (protecao?.DevePular(TipoBatchMatching.Combinado) == true)
            {
                protecao.RegistrarPulo(chunk.Length);
                foreach (var entrada in chunk)
                    resultados[entrada.InputId] = FalhaCombinada(entrada.ProjecaoOperacional.HasValue);
                continue;
            }
            var inicio = Stopwatch.GetTimestamp();
            var comandoBatchRegistrado = false;
            var tentativaPostgres = false;
            try
            {
                AntesDoComandoBatchParaTeste?.Invoke(TipoBatchMatching.Combinado, numeroChunk);
                cancellationToken.ThrowIfCancellationRequested();
                await ExecutarCombinadoChunkAsync(chunk, resultados,
                    () => tentativaPostgres = true, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (tentativaPostgres)
                {
                    comandos.Add(new(TipoBatchMatching.Combinado,
                        OrigemComandoMatchingLote.Batch, chunk.Length,
                        Stopwatch.GetElapsedTime(inicio)));
                    comandoBatchRegistrado = true;
                }
                var acao = protecao?.RegistrarFalha(TipoBatchMatching.Combinado,
                    ClassificarFalhaBatch(ex)) ?? AcaoFalhaMatchingBatch.RecuperarChunk;
                if (acao == AcaoFalhaMatchingBatch.AbrirCircuito)
                {
                    _logger.LogWarning("Circuito de matching batch aberto apos falha de infraestrutura no COMBINADO.");
                    foreach (var entrada in chunk)
                        resultados[entrada.InputId] = FalhaCombinada(entrada.ProjecaoOperacional.HasValue);
                    continue;
                }

                var pularPrimeira = false;
                if (acao == AcaoFalhaMatchingBatch.ExecutarSonda)
                {
                    var primeira = chunk[0];
                    AntesDoFallbackIndividualParaTeste?.Invoke(TipoBatchMatching.Combinado, primeira.InputId);
                    var inicioSonda = Stopwatch.GetTimestamp();
                    var sonda = await BuscarMatchingCombinadoAsync(primeira.CodigoLinha,
                        primeira.PadraoVersaoAnteriorId, primeira.Latitude, primeira.Longitude,
                        primeira.Bearing!.Value, primeira.DistanciaMaximaMetros, primeira.Faixa,
                        primeira.ProjecaoOperacional, cancellationToken);
                    comandos.Add(new(TipoBatchMatching.Combinado,
                        OrigemComandoMatchingLote.FallbackIndividual, 1,
                        Stopwatch.GetElapsedTime(inicioSonda)));
                    resultados[primeira.InputId] = sonda;
                    var infraestrutura = sonda.Global.Status == StatusBuscaPadrao.InfrastructureFailure
                        || sonda.Anterior.Status == StatusBuscaPadrao.InfrastructureFailure
                        || sonda.Operacional?.Status == StatusProjecaoOperacional.FalhaInfraestrutura;
                    if (protecao!.ConcluirSonda(TipoBatchMatching.Combinado, infraestrutura))
                    {
                        _logger.LogWarning("Circuito de matching batch aberto apos sonda COMBINADA de infraestrutura.");
                        foreach (var entrada in chunk)
                            resultados[entrada.InputId] = FalhaCombinada(entrada.ProjecaoOperacional.HasValue);
                        continue;
                    }
                    pularPrimeira = true;
                }
                else if (protecao is not null)
                {
                    _logger.LogWarning(ex, "Operacao COMBINADA batch degradada no estagio; recuperando somente o chunk de {quantidade} entradas.", chunk.Length);
                }
                else _logger.LogWarning(ex, "Falha no matching combinado em lote com {quantidade} entradas.", chunk.Length);
                foreach (var entrada in pularPrimeira ? chunk.Skip(1) : chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AntesDoFallbackIndividualParaTeste?.Invoke(
                        TipoBatchMatching.Combinado, entrada.InputId);
                    var inicioFallback = Stopwatch.GetTimestamp();
                    try
                    {
                        resultados[entrada.InputId] = await BuscarMatchingCombinadoAsync(
                            entrada.CodigoLinha, entrada.PadraoVersaoAnteriorId,
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
                if (tentativaPostgres && !comandoBatchRegistrado)
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
        CancellationToken cancellationToken, MatchingBatchStageProtection? protecao)
    {
        ValidarLote(entradas, tamanhoChunk, x => x.InputId);
        cancellationToken.ThrowIfCancellationRequested();
        var resultados = entradas.ToDictionary(x => x.InputId,
            _ => ResultadoBuscaPadrao.NotEligible(), StringComparer.Ordinal);
        var comandos = new List<MetricaComandoMatchingLote>();
        var validas = entradas.Where(x => x.Bearing.HasValue
            && DadosBasicosValidos(x.Latitude, x.Longitude, x.Bearing.Value,
                x.DistanciaMaximaMetros)).ToArray();

        var numeroChunk = 0;
        foreach (var chunk in validas.Chunk(tamanhoChunk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            numeroChunk++;
            if (protecao?.DevePular(TipoBatchMatching.GlobalSimples) == true)
            {
                protecao.RegistrarPulo(chunk.Length);
                foreach (var entrada in chunk)
                    resultados[entrada.InputId] = ResultadoBuscaPadrao.InfrastructureFailure();
                continue;
            }
            var inicio = Stopwatch.GetTimestamp();
            var comandoBatchRegistrado = false;
            var tentativaPostgres = false;
            try
            {
                AntesDoComandoBatchParaTeste?.Invoke(TipoBatchMatching.GlobalSimples, numeroChunk);
                cancellationToken.ThrowIfCancellationRequested();
                var json = JsonSerializer.Serialize(chunk.Select(x => new
                {
                    input_id = x.InputId, codigo = x.CodigoLinha, lat = x.Latitude,
                    lon = x.Longitude, bearing = x.Bearing!.Value,
                    dist_max = x.DistanciaMaximaMetros, padrao_versao_id = Guid.Empty,
                    usar_faixa = false, fracao_min = 0d, fracao_max = 1d
                }));
                await ExecutarRotaChunkAsync(json, direcionado: false, resultados,
                    () => tentativaPostgres = true, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (tentativaPostgres)
                {
                    comandos.Add(new(TipoBatchMatching.GlobalSimples,
                        OrigemComandoMatchingLote.Batch, chunk.Length,
                        Stopwatch.GetElapsedTime(inicio)));
                    comandoBatchRegistrado = true;
                }
                var acao = protecao?.RegistrarFalha(TipoBatchMatching.GlobalSimples,
                    ClassificarFalhaBatch(ex)) ?? AcaoFalhaMatchingBatch.RecuperarChunk;
                if (acao == AcaoFalhaMatchingBatch.AbrirCircuito)
                {
                    _logger.LogWarning("Circuito de matching batch aberto apos falha de infraestrutura no GLOBAL.");
                    foreach (var entrada in chunk)
                        resultados[entrada.InputId] = ResultadoBuscaPadrao.InfrastructureFailure();
                    continue;
                }

                var pularPrimeira = false;
                if (acao == AcaoFalhaMatchingBatch.ExecutarSonda)
                {
                    var primeira = chunk[0];
                    AntesDoFallbackIndividualParaTeste?.Invoke(TipoBatchMatching.GlobalSimples, primeira.InputId);
                    var inicioSonda = Stopwatch.GetTimestamp();
                    var infraestrutura = false;
                    try
                    {
                        var rota = await BuscarMatchingAsync(primeira.CodigoLinha,
                            primeira.Latitude, primeira.Longitude, primeira.Bearing!.Value,
                            primeira.DistanciaMaximaMetros, null, null, cancellationToken,
                            propagarFalhaGlobalParaDiagnostico: true);
                        resultados[primeira.InputId] = rota is null
                            ? ResultadoBuscaPadrao.NotEligible() : ResultadoBuscaPadrao.Found(rota);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception probeEx)
                    {
                        infraestrutura = ClassificarFalhaBatch(probeEx) is
                            CategoriaFalhaMatchingBatch.Connectivity or CategoriaFalhaMatchingBatch.Timeout;
                        resultados[primeira.InputId] = ResultadoBuscaPadrao.InfrastructureFailure();
                    }
                    finally
                    {
                        comandos.Add(new(TipoBatchMatching.GlobalSimples,
                            OrigemComandoMatchingLote.FallbackIndividual, 1,
                            Stopwatch.GetElapsedTime(inicioSonda)));
                    }
                    if (protecao!.ConcluirSonda(TipoBatchMatching.GlobalSimples, infraestrutura))
                    {
                        _logger.LogWarning("Circuito de matching batch aberto apos sonda GLOBAL de infraestrutura.");
                        foreach (var entrada in chunk)
                            resultados[entrada.InputId] = ResultadoBuscaPadrao.InfrastructureFailure();
                        continue;
                    }
                    pularPrimeira = true;
                }
                else if (protecao is not null)
                {
                    _logger.LogWarning(ex, "Operacao GLOBAL batch degradada no estagio; recuperando somente o chunk de {quantidade} entradas.", chunk.Length);
                }
                else _logger.LogWarning(ex, "Falha no matching global em lote com {quantidade} entradas.", chunk.Length);

                foreach (var entrada in pularPrimeira ? chunk.Skip(1) : chunk)
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
                            ? ResultadoBuscaPadrao.NotEligible()
                            : ResultadoBuscaPadrao.Found(rota);
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
                if (tentativaPostgres && !comandoBatchRegistrado)
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
        CancellationToken cancellationToken, MatchingBatchStageProtection? protecao)
    {
        ValidarLote(entradas, tamanhoChunk, x => x.InputId);
        cancellationToken.ThrowIfCancellationRequested();
        var resultados = new Dictionary<string, ResultadoBuscaPadrao>(StringComparer.Ordinal);
        var comandos = new List<MetricaComandoMatchingLote>();
        foreach (var entrada in entradas)
            resultados[entrada.InputId] = !entrada.Bearing.HasValue
                ? ResultadoBuscaPadrao.NotEligible()
                : !DadosBasicosValidos(entrada.Latitude, entrada.Longitude,
                      entrada.Bearing.Value, entrada.DistanciaMaximaMetros)
                    || entrada.Faixa is { } faixa && !faixa.Valida
                    ? ResultadoBuscaPadrao.InfrastructureFailure()
                    : ResultadoBuscaPadrao.NotEligible();

        var validas = entradas.Where(x => x.Bearing.HasValue
            && DadosBasicosValidos(x.Latitude, x.Longitude, x.Bearing.Value,
                x.DistanciaMaximaMetros)
            && (x.Faixa is not { } faixa || faixa.Valida)).ToArray();
        var numeroChunk = 0;
        foreach (var chunk in validas.Chunk(tamanhoChunk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            numeroChunk++;
            if (protecao?.DevePular(TipoBatchMatching.Direcionado) == true)
            {
                protecao.RegistrarPulo(chunk.Length);
                foreach (var entrada in chunk)
                    resultados[entrada.InputId] = ResultadoBuscaPadrao.InfrastructureFailure();
                continue;
            }
            var inicio = Stopwatch.GetTimestamp();
            var comandoBatchRegistrado = false;
            var tentativaPostgres = false;
            try
            {
                AntesDoComandoBatchParaTeste?.Invoke(TipoBatchMatching.Direcionado, numeroChunk);
                cancellationToken.ThrowIfCancellationRequested();
                var json = JsonSerializer.Serialize(chunk.Select(x => new
                {
                    input_id = x.InputId, codigo = x.CodigoLinha, lat = x.Latitude,
                    lon = x.Longitude, bearing = x.Bearing!.Value,
                    dist_max = x.DistanciaMaximaMetros, padrao_versao_id = x.PadraoVersaoId,
                    usar_faixa = x.Faixa.HasValue, fracao_min = x.Faixa?.Min ?? 0d,
                    fracao_max = x.Faixa?.Max ?? 1d
                }));
                await ExecutarRotaChunkAsync(json, direcionado: true, resultados,
                    () => tentativaPostgres = true, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (tentativaPostgres)
                {
                    comandos.Add(new(TipoBatchMatching.Direcionado,
                        OrigemComandoMatchingLote.Batch, chunk.Length,
                        Stopwatch.GetElapsedTime(inicio)));
                    comandoBatchRegistrado = true;
                }
                var acao = protecao?.RegistrarFalha(TipoBatchMatching.Direcionado,
                    ClassificarFalhaBatch(ex)) ?? AcaoFalhaMatchingBatch.RecuperarChunk;
                if (acao == AcaoFalhaMatchingBatch.AbrirCircuito)
                {
                    _logger.LogWarning("Circuito de matching batch aberto apos falha de infraestrutura no DIRECIONADO.");
                    foreach (var entrada in chunk)
                        resultados[entrada.InputId] = ResultadoBuscaPadrao.InfrastructureFailure();
                    continue;
                }

                var pularPrimeira = false;
                if (acao == AcaoFalhaMatchingBatch.ExecutarSonda)
                {
                    var primeira = chunk[0];
                    AntesDoFallbackIndividualParaTeste?.Invoke(TipoBatchMatching.Direcionado, primeira.InputId);
                    var inicioSonda = Stopwatch.GetTimestamp();
                    var sonda = await BuscarEnriquecimentoDoPadraoAsync(primeira.CodigoLinha,
                        primeira.PadraoVersaoId, primeira.Latitude, primeira.Longitude,
                        primeira.Bearing!.Value, primeira.DistanciaMaximaMetros,
                        cancellationToken, primeira.Faixa);
                    comandos.Add(new(TipoBatchMatching.Direcionado,
                        OrigemComandoMatchingLote.FallbackIndividual, 1,
                        Stopwatch.GetElapsedTime(inicioSonda)));
                    resultados[primeira.InputId] = sonda;
                    if (protecao!.ConcluirSonda(TipoBatchMatching.Direcionado,
                        sonda.Status == StatusBuscaPadrao.InfrastructureFailure))
                    {
                        _logger.LogWarning("Circuito de matching batch aberto apos sonda DIRECIONADA de infraestrutura.");
                        foreach (var entrada in chunk)
                            resultados[entrada.InputId] = ResultadoBuscaPadrao.InfrastructureFailure();
                        continue;
                    }
                    pularPrimeira = true;
                }
                else if (protecao is not null)
                {
                    _logger.LogWarning(ex, "Operacao DIRECIONADA batch degradada no estagio; recuperando somente o chunk de {quantidade} entradas.", chunk.Length);
                }
                else _logger.LogWarning(ex, "Falha no matching direcionado em lote com {quantidade} entradas.", chunk.Length);
                foreach (var entrada in pularPrimeira ? chunk.Skip(1) : chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AntesDoFallbackIndividualParaTeste?.Invoke(
                        TipoBatchMatching.Direcionado, entrada.InputId);
                    var inicioFallback = Stopwatch.GetTimestamp();
                    try
                    {
                        resultados[entrada.InputId] = await BuscarEnriquecimentoDoPadraoAsync(
                            entrada.CodigoLinha, entrada.PadraoVersaoId,
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
                if (tentativaPostgres && !comandoBatchRegistrado)
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
        Dictionary<string, ResultadoBuscaPadrao> resultados,
        Action registrarTentativaPostgres,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH inputs AS (
                SELECT * FROM jsonb_to_recordset(@inputs::jsonb) AS x(
                    input_id text, codigo text, lat double precision, lon double precision,
                    bearing double precision, dist_max double precision, padrao_versao_id uuid,
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
                    SELECT i."Id", po."Id" AS padrao_operacional_id,
                        po."SentidoId" AS sentido_id, s."LinhaId" AS linha_id,
                        i."Topologia" AS topologia, i."Geometria"
                    FROM "PadroesVersoes" i
                    JOIN "PadroesOperacionais" po ON po."VersaoAtualId" = i."Id"
                    JOIN "Sentidos" s ON s."Id" = po."SentidoId"
                    JOIN "Linhas" l ON l."Id" = s."LinhaId"
                    WHERE l."Codigo" = entrada.codigo
                    /*FILTRO_PADRAO*/
                ),
                geometrias_projecao AS (
                    SELECT r.*,
                        CASE WHEN entrada.usar_faixa
                            THEN ST_LineSubstring(r."Geometria", entrada.fracao_min, entrada.fracao_max)
                            ELSE r."Geometria" END AS geometria_projecao
                    FROM rotas r
                ),
                candidatos AS (
                    SELECT r."Id", r.padrao_operacional_id, r.sentido_id, r.linha_id, r.topologia, r."Geometria",
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
                padrao_escolhido AS (
                    SELECT cs.*, ST_LineInterpolatePoint(cs."Geometria", cs.posicao_na_rota) AS ponto_rota
                    FROM com_score cs ORDER BY cs.score ASC /*DESEMPATE_GLOBAL*/ LIMIT 1
                ),
                proxima_parada AS (
                    SELECT p."Nome" AS parada_nome, pi."Id" AS ocorrencia_id,
                        pi."ParadaId" AS parada_id, pi."Ordem" AS parada_ordem,
                        pi."DistanciaAcumuladaMetros" AS parada_distancia_acumulada,
                        pi."DistanciaDaLinhaMetros" AS parada_distancia_linha,
                        ST_Distance(v.ponto, p."Localizacao"::geography) AS distancia_parada_metros
                    FROM "OcorrenciasParadasPadroes" pi
                    JOIN "Paradas" p ON p."Id" = pi."ParadaId"
                    JOIN padrao_escolhido ie ON ie."Id" = pi."PadraoVersaoId"
                    CROSS JOIN veiculo v
                    WHERE pi."PosicaoTracado" > ie.posicao_na_rota
                    ORDER BY pi."PosicaoTracado" ASC, pi."Ordem" ASC LIMIT 1
                )
                SELECT ie."Id" AS padrao_versao_id, ie.padrao_operacional_id,
                    ie.sentido_id, ie.linha_id, ie.topologia,
                    ie.posicao_na_rota, ie.comprimento_metros,
                    ie.distancia_rota_metros, ie.bearing_local,
                    ST_Y(ie.ponto_rota) AS lat_rota, ST_X(ie.ponto_rota) AS lon_rota,
                    pp.parada_nome, pp.ocorrencia_id, pp.parada_id, pp.parada_ordem,
                    pp.parada_distancia_acumulada, pp.parada_distancia_linha,
                    pp.distancia_parada_metros
                FROM padrao_escolhido ie LEFT JOIN proxima_parada pp ON true LIMIT 1
            ) escolhido ON true
            ORDER BY entrada.input_id DESC
            """;

        registrarTentativaPostgres();
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.Replace("/*FILTRO_PADRAO*/",
            direcionado ? "AND i.\"Id\" = entrada.padrao_versao_id" : "")
            .Replace("/*DESEMPATE_GLOBAL*/",
                direcionado ? "" : ", cs.\"Id\" ASC");
        cmd.Parameters.AddWithValue("inputs", NpgsqlDbType.Jsonb, json);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var inputId = reader.GetString(reader.GetOrdinal("input_id"));
            resultados[inputId] = reader.IsDBNull(reader.GetOrdinal("padrao_versao_id"))
                ? ResultadoBuscaPadrao.NotEligible()
                : ResultadoBuscaPadrao.Found(LerRota(reader));
        }
    }

    private async Task ExecutarCombinadoChunkAsync(
        EntradaMatchingCombinadoLote[] chunk,
        Dictionary<string, ResultadoMatchingCombinado> resultados,
        Action registrarTentativaPostgres,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(chunk.Select(x => new
        {
            input_id = x.InputId, codigo = x.CodigoLinha, lat = x.Latitude,
            lon = x.Longitude, bearing = x.Bearing!.Value,
            dist_max = x.DistanciaMaximaMetros,
            usar_anterior = x.PadraoVersaoAnteriorId.HasValue && x.Faixa.HasValue,
            padrao_versao_id = x.PadraoVersaoAnteriorId ?? Guid.Empty,
            fracao_min = x.Faixa?.Min ?? 0d, fracao_max = x.Faixa?.Max ?? 1d,
            usar_operacional = x.ProjecaoOperacional.HasValue,
            padrao_operacional = x.ProjecaoOperacional?.PadraoVersaoId ?? Guid.Empty,
            posicao_operacional_anterior = x.ProjecaoOperacional?.PosicaoAnterior ?? 0d,
            orcamento_operacional_metros = x.ProjecaoOperacional?.OrcamentoMetros ?? 1d
        }));

        foreach (var entrada in chunk)
            resultados[entrada.InputId] = new(
                ResultadoBuscaPadrao.NotEligible(), ResultadoBuscaPadrao.NotEligible(),
                entrada.ProjecaoOperacional.HasValue
                    ? ResultadoProjecaoOperacional.Inelegivel()
                    : ResultadoProjecaoOperacional.NaoSolicitada());

        registrarTentativaPostgres();
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
                case "GLOBAL": atual = atual with { Global = ResultadoBuscaPadrao.Found(LerRota(reader)) }; break;
                case "ANTERIOR": atual = atual with { Anterior = ResultadoBuscaPadrao.Found(LerRota(reader)) }; break;
                case "OPERACIONAL":
                    atual = atual with { Operacional = ResultadoProjecaoOperacional.Encontrada(new(
                        reader.GetGuid(reader.GetOrdinal("padrao_versao_id")),
                        reader.GetDouble(reader.GetOrdinal("posicao_na_rota")),
                        reader.GetDouble(reader.GetOrdinal("distancia_rota_metros")),
                        reader.GetDouble(reader.GetOrdinal("comprimento_metros")))) };
                    break;
                default: throw new InvalidOperationException("Ramo inesperado no matching combinado em lote.");
            }
            resultados[inputId] = atual;
        }
    }

    internal static CategoriaFalhaMatchingBatch ClassificarFalhaBatch(Exception ex)
    {
        if (ex is OperationCanceledException) return CategoriaFalhaMatchingBatch.Cancellation;
        if (ex is TimeoutException) return CategoriaFalhaMatchingBatch.Timeout;
        if (ex is System.Net.Sockets.SocketException or IOException)
            return CategoriaFalhaMatchingBatch.Connectivity;
        if (ex is InvalidCastException or FormatException or IndexOutOfRangeException)
            return CategoriaFalhaMatchingBatch.DataOrMapping;
        if (ex is JsonException or NotSupportedException or ArgumentException)
            return CategoriaFalhaMatchingBatch.Serialization;
        if (ex is PostgresException postgres)
        {
            if (postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
                || postgres.SqlState is "57P01" or "57P02" or "57P03" or "53300")
                return CategoriaFalhaMatchingBatch.Connectivity;
            if (postgres.SqlState is "40P01" or "40001" or "55P03")
                return CategoriaFalhaMatchingBatch.TransientPostgres;
            if (postgres.SqlState.StartsWith("42", StringComparison.Ordinal)
                || postgres.SqlState.StartsWith("0A", StringComparison.Ordinal))
                return CategoriaFalhaMatchingBatch.SqlOrSchema;
            if (postgres.SqlState.StartsWith("22", StringComparison.Ordinal))
                return CategoriaFalhaMatchingBatch.DataOrMapping;
        }
        if (ex is NpgsqlException npgsql)
        {
            for (var inner = npgsql.InnerException; inner is not null; inner = inner.InnerException)
                if (inner is TimeoutException) return CategoriaFalhaMatchingBatch.Timeout;
                else if (inner is System.Net.Sockets.SocketException or IOException)
                    return CategoriaFalhaMatchingBatch.Connectivity;
            return CategoriaFalhaMatchingBatch.Connectivity;
        }
        return CategoriaFalhaMatchingBatch.Unknown;
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
                usar_anterior boolean, padrao_versao_id uuid,
                fracao_min double precision, fracao_max double precision,
                usar_operacional boolean, padrao_operacional uuid,
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
                SELECT i."Id",po."Id" AS padrao_operacional_id,
                    po."SentidoId" AS sentido_id,s."LinhaId" AS linha_id,
                    i."Topologia" AS topologia,i."Geometria" FROM "PadroesVersoes" i
                JOIN "PadroesOperacionais" po ON po."VersaoAtualId"=i."Id"
                JOIN "Sentidos" s ON s."Id"=po."SentidoId"
                JOIN "Linhas" l ON l."Id"=s."LinhaId"
                WHERE l."Codigo"=entrada.codigo
            ),
            candidatos_global AS (
                SELECT r."Id",r.padrao_operacional_id,r.sentido_id,r.linha_id,r.topologia,r."Geometria",
                    ST_Length(r."Geometria"::geography) AS comprimento_metros,
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
                FROM score_global sg ORDER BY sg.score ASC, sg."Id" ASC LIMIT 1
            ),
            proxima_parada_global AS (
                SELECT p."Nome" AS parada_nome,pi."Id" AS ocorrencia_id,pi."ParadaId" AS parada_id,
                    pi."Ordem" AS parada_ordem,pi."DistanciaAcumuladaMetros" AS parada_distancia_acumulada,
                    pi."DistanciaDaLinhaMetros" AS parada_distancia_linha,
                    ST_Distance(v.ponto,p."Localizacao"::geography) AS distancia_parada_metros
                FROM "OcorrenciasParadasPadroes" pi JOIN "Paradas" p ON p."Id"=pi."ParadaId"
                JOIN global_escolhido ge ON ge."Id"=pi."PadraoVersaoId" CROSS JOIN veiculo v
                WHERE pi."PosicaoTracado">ge.posicao_na_rota
                ORDER BY pi."PosicaoTracado" ASC, pi."Ordem" ASC LIMIT 1
            ),
            rota_anterior AS (
                SELECT i."Id",po."Id" AS padrao_operacional_id,
                    po."SentidoId" AS sentido_id,s."LinhaId" AS linha_id,
                    i."Topologia" AS topologia,i."Geometria" FROM "PadroesVersoes" i
                JOIN "PadroesOperacionais" po ON po."VersaoAtualId"=i."Id"
                JOIN "Sentidos" s ON s."Id"=po."SentidoId"
                JOIN "Linhas" l ON l."Id"=s."LinhaId"
                WHERE entrada.usar_anterior AND l."Codigo"=entrada.codigo AND i."Id"=entrada.padrao_versao_id
            ),
            geometria_anterior AS (
                SELECT r.*,ST_LineSubstring(r."Geometria",entrada.fracao_min,entrada.fracao_max) AS geometria_projecao
                FROM rota_anterior r
            ),
            candidatos_anterior AS (
                SELECT r."Id",r.padrao_operacional_id,r.sentido_id,r.linha_id,r.topologia,r."Geometria",
                    ST_Length(r."Geometria"::geography) AS comprimento_metros,
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
                SELECT p."Nome" AS parada_nome,pi."Id" AS ocorrencia_id,pi."ParadaId" AS parada_id,
                    pi."Ordem" AS parada_ordem,pi."DistanciaAcumuladaMetros" AS parada_distancia_acumulada,
                    pi."DistanciaDaLinhaMetros" AS parada_distancia_linha,
                    ST_Distance(v.ponto,p."Localizacao"::geography) AS distancia_parada_metros
                FROM "OcorrenciasParadasPadroes" pi JOIN "Paradas" p ON p."Id"=pi."ParadaId"
                JOIN anterior_escolhido ae ON ae."Id"=pi."PadraoVersaoId" CROSS JOIN veiculo v
                WHERE pi."PosicaoTracado">ae.posicao_na_rota
                ORDER BY pi."PosicaoTracado" ASC, pi."Ordem" ASC LIMIT 1
            ),
            rota_operacional AS (
                SELECT i."Id",i."Geometria",ST_Length(i."Geometria"::geography) AS comprimento_metros
                FROM "PadroesVersoes" i
                JOIN "PadroesOperacionais" po ON po."VersaoAtualId"=i."Id"
                WHERE entrada.usar_operacional
                  AND i."Id"=entrada.padrao_operacional
                  AND EXISTS(SELECT 1 FROM global_escolhido ge WHERE ge."Id"<>entrada.padrao_operacional)
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
            SELECT 'GLOBAL'::text AS ramo,ge."Id" AS padrao_versao_id,ge.padrao_operacional_id,
                ge.sentido_id,ge.linha_id,ge.topologia,ge.posicao_na_rota,
                ge.comprimento_metros,ge.distancia_rota_metros,ge.bearing_local,
                ST_Y(ge.ponto_rota) AS lat_rota,ST_X(ge.ponto_rota) AS lon_rota,
                ppg.parada_nome,ppg.ocorrencia_id,ppg.parada_id,ppg.parada_ordem,
                ppg.parada_distancia_acumulada,ppg.parada_distancia_linha,ppg.distancia_parada_metros
            FROM global_escolhido ge LEFT JOIN proxima_parada_global ppg ON true
            UNION ALL
            SELECT 'ANTERIOR',ae."Id",ae.padrao_operacional_id,ae.sentido_id,ae.linha_id,ae.topologia,
                ae.posicao_na_rota,ae.comprimento_metros,ae.distancia_rota_metros,
                ae.bearing_local,ST_Y(ae.ponto_rota),ST_X(ae.ponto_rota),ppa.parada_nome,
                ppa.ocorrencia_id,ppa.parada_id,ppa.parada_ordem,ppa.parada_distancia_acumulada,
                ppa.parada_distancia_linha,ppa.distancia_parada_metros
            FROM anterior_escolhido ae LEFT JOIN proxima_parada_anterior ppa ON true
            UNION ALL
            SELECT 'OPERACIONAL',oe."Id",NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,
                oe.posicao_na_rota,oe.comprimento_metros,oe.distancia_rota_metros,
                NULL::double precision,NULL::double precision,NULL::double precision,NULL::text,
                NULL::uuid,NULL::uuid,NULL::integer,NULL::double precision,NULL::double precision,NULL::double precision
            FROM operacional_elegivel oe
        ) resultado
        ORDER BY entrada.input_id DESC, resultado.ramo DESC
        """;
}
