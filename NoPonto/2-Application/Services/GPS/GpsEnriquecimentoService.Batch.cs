namespace NoPonto.Application.GPS;

internal sealed record EntradaEnriquecimentoGps(
    PosicaoVeiculoDto Posicao,
    ContextoOperacional? Contexto);

/// <summary>
/// Executor em duas barreiras: todas as consultas iniciais são agrupadas antes
/// de qualquer SQL e somente o subconjunto que a decisão C# existente pedir é
/// enviado ao batch direcionado. Os grupos são executados sequencialmente.
/// </summary>
internal interface IExecutorMatchingGps
{
    Task<ResultadoBuscaItinerario> BuscarGlobalAsync(
        string inputId, string codigoLinha, double latitude, double longitude,
        double bearing, double distanciaMaximaMetros, CancellationToken ct);

    Task<ResultadoMatchingCombinado> BuscarCombinadoAsync(
        string inputId, string codigoLinha, Guid? itinerarioAnteriorId,
        double latitude, double longitude, double bearing, double distanciaMaximaMetros,
        FaixaProjecao? faixa, SolicitacaoProjecaoOperacional? projecaoOperacional,
        CancellationToken ct);

    Task SemConsultaInicialAsync(string inputId, CancellationToken ct);

    Task<ResultadoBuscaItinerario> BuscarDirecionadoAsync(
        string inputId, string codigoLinha, Guid itinerarioId,
        double latitude, double longitude, double bearing, double distanciaMaximaMetros,
        CancellationToken ct, FaixaProjecao? faixa);

    Task SemConsultaDirecionadaAsync(string inputId, CancellationToken ct);
}

public sealed partial class GpsEnriquecimentoService
{
    internal async Task<ResultadoEnriquecimentoGps[]> EnriquecerLoteComContextoAsync(
        IReadOnlyList<EntradaEnriquecimentoGps> entradas,
        CancellationToken ct,
        GpsCicloPerformance? performance)
    {
        if (!_opcoesBatch.Enabled)
            throw new InvalidOperationException("Matching batch não pode ser executado com a feature flag desligada.");

        ArgumentNullException.ThrowIfNull(entradas);
        if (entradas.Count == 0) return [];

        performance?.RegistrarMatchingBatchInputs(entradas.Count);

        var stageId = Guid.NewGuid().ToString("N");
        var ids = Enumerable.Range(0, entradas.Count)
            .Select(indice => $"{stageId}:{indice:D8}")
            .ToArray();
        var executor = new ExecutorMatchingGpsLote(
            _repositorio, ids, performance, _opcoesBatch.TamanhoChunk, ct);

        // A concorrência aqui é apenas da decisão C# por posição. O executor
        // emite no máximo um grupo simple, um combined e um directed, nessa ordem;
        // não existem batches concorrentes nem batch de tamanho 1 dentro de foreach.
        var resultados = await Task.WhenAll(entradas.Select(async (entrada, indice) =>
        {
            try
            {
                return await EnriquecerCoreAsync(entrada.Posicao, entrada.Contexto,
                    ct, performance, executor, ids[indice]);
            }
            catch (Exception ex)
            {
                executor.Abortar(ex);
                throw;
            }
        }));
        performance?.RegistrarProtecaoBatch(executor.Protecao);
        return resultados;
    }

    private sealed class ExecutorMatchingGpsLote : IExecutorMatchingGps
    {
        private readonly IGpsItinerarioRepository _repositorio;
        private readonly GpsCicloPerformance? _performance;
        private readonly object _sync = new();
        private readonly int _quantidade;
        private readonly int _tamanhoChunk;
        private readonly CancellationToken _ct;
        private readonly MatchingBatchStageProtection _protecao = new();
        internal MatchingBatchStageProtection Protecao => _protecao;
        private int _iniciaisRegistradas;
        private int _direcionadasRegistradas;
        private bool _inicialDisparado;
        private bool _direcionadoDisparado;

        private readonly Dictionary<string, EntradaMatchingGlobalLote> _globais =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, EntradaMatchingCombinadoLote> _combinados =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, EntradaMatchingDirecionadoLote> _direcionados =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<ResultadoBuscaItinerario>> _resultadosGlobais;
        private readonly Dictionary<string, TaskCompletionSource<ResultadoMatchingCombinado>> _resultadosCombinados;
        private readonly Dictionary<string, TaskCompletionSource<ResultadoBuscaItinerario>> _resultadosDirecionados;

        internal ExecutorMatchingGpsLote(
            IGpsItinerarioRepository repositorio,
            IReadOnlyList<string> ids,
            GpsCicloPerformance? performance,
            int tamanhoChunk,
            CancellationToken ct = default)
        {
            if (tamanhoChunk <= 0) throw new ArgumentOutOfRangeException(nameof(tamanhoChunk));
            _repositorio = repositorio;
            _performance = performance;
            _quantidade = ids.Count;
            _tamanhoChunk = tamanhoChunk;
            _ct = ct;
            _resultadosGlobais = ids.ToDictionary(x => x,
                _ => NovaFonte<ResultadoBuscaItinerario>(), StringComparer.Ordinal);
            _resultadosCombinados = ids.ToDictionary(x => x,
                _ => NovaFonte<ResultadoMatchingCombinado>(), StringComparer.Ordinal);
            _resultadosDirecionados = ids.ToDictionary(x => x,
                _ => NovaFonte<ResultadoBuscaItinerario>(), StringComparer.Ordinal);
        }

        public async Task<ResultadoBuscaItinerario> BuscarGlobalAsync(
            string inputId, string codigoLinha, double latitude, double longitude,
            double bearing, double distanciaMaximaMetros, CancellationToken ct)
        {
            RegistrarInicial(inputId, new EntradaMatchingGlobalLote(inputId, codigoLinha,
                latitude, longitude, bearing, distanciaMaximaMetros), null);
            return await _resultadosGlobais[inputId].Task.WaitAsync(ct);
        }

        public async Task<ResultadoMatchingCombinado> BuscarCombinadoAsync(
            string inputId, string codigoLinha, Guid? itinerarioAnteriorId,
            double latitude, double longitude, double bearing, double distanciaMaximaMetros,
            FaixaProjecao? faixa, SolicitacaoProjecaoOperacional? projecaoOperacional,
            CancellationToken ct)
        {
            RegistrarInicial(inputId, null, new EntradaMatchingCombinadoLote(inputId,
                codigoLinha, itinerarioAnteriorId, latitude, longitude, bearing,
                distanciaMaximaMetros, faixa, projecaoOperacional));
            return await _resultadosCombinados[inputId].Task.WaitAsync(ct);
        }

        public Task SemConsultaInicialAsync(string inputId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RegistrarInicial(inputId, null, null);
            return Task.CompletedTask;
        }

        public async Task<ResultadoBuscaItinerario> BuscarDirecionadoAsync(
            string inputId, string codigoLinha, Guid itinerarioId,
            double latitude, double longitude, double bearing, double distanciaMaximaMetros,
            CancellationToken ct, FaixaProjecao? faixa)
        {
            RegistrarDirecionado(inputId, new EntradaMatchingDirecionadoLote(inputId,
                codigoLinha, itinerarioId, latitude, longitude, bearing,
                distanciaMaximaMetros, faixa));
            return await _resultadosDirecionados[inputId].Task.WaitAsync(ct);
        }

        public Task SemConsultaDirecionadaAsync(string inputId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RegistrarDirecionado(inputId, null);
            return Task.CompletedTask;
        }

        private void RegistrarInicial(
            string inputId,
            EntradaMatchingGlobalLote? global,
            EntradaMatchingCombinadoLote? combinado)
        {
            var disparar = false;
            lock (_sync)
            {
                if (global is not null) _globais.Add(inputId, global);
                if (combinado is not null) _combinados.Add(inputId, combinado);
                _iniciaisRegistradas++;
                if (_iniciaisRegistradas == _quantidade && !_inicialDisparado)
                {
                    _inicialDisparado = true;
                    disparar = true;
                }
            }
            if (disparar) _ = ExecutarIniciaisAsync();
        }

        private async Task ExecutarIniciaisAsync()
        {
            try
            {
                // BLOCKER PARA FLAG ON: o fallback interno do repository ainda pode
                // gerar uma tentativa individual por entrada quando o PostgreSQL
                // inteiro está indisponível. Definir circuit breaker/limite explícito
                // antes de habilitar em staging; não há limite arbitrário aqui.
                if (_globais.Count > 0)
                {
                    var lote = await _repositorio.BuscarGlobaisEmLoteAsync(
                        _globais.Values.ToArray(), _tamanhoChunk,
                        _ct, _protecao);
                    _performance?.RegistrarMatchingLote(lote.Metricas);
                    foreach (var resultado in lote.Resultados)
                        _resultadosGlobais[resultado.InputId].TrySetResult(resultado.Global);
                }

                if (_combinados.Count > 0)
                {
                    var lote = await _repositorio.BuscarCombinadosEmLoteAsync(
                        _combinados.Values.ToArray(), _tamanhoChunk,
                        _ct, _protecao);
                    _performance?.RegistrarMatchingLote(lote.Metricas);
                    foreach (var resultado in lote.Resultados)
                        _resultadosCombinados[resultado.InputId].TrySetResult(resultado.Resultado);
                }
            }
            catch (Exception ex)
            {
                Falhar(_resultadosGlobais, _globais.Keys, ex);
                Falhar(_resultadosCombinados, _combinados.Keys, ex);
            }
        }

        private void RegistrarDirecionado(string inputId, EntradaMatchingDirecionadoLote? entrada)
        {
            var disparar = false;
            lock (_sync)
            {
                if (entrada is not null) _direcionados.Add(inputId, entrada);
                _direcionadasRegistradas++;
                if (_direcionadasRegistradas == _quantidade && !_direcionadoDisparado)
                {
                    _direcionadoDisparado = true;
                    disparar = true;
                }
            }
            if (disparar) _ = ExecutarDirecionadosAsync();
        }

        private async Task ExecutarDirecionadosAsync()
        {
            if (_direcionados.Count == 0) return;
            try
            {
                var lote = await _repositorio.BuscarDirecionadosEmLoteAsync(
                    _direcionados.Values.ToArray(), _tamanhoChunk,
                    _ct, _protecao);
                _performance?.RegistrarMatchingLote(lote.Metricas);
                foreach (var resultado in lote.Resultados)
                    _resultadosDirecionados[resultado.InputId].TrySetResult(resultado.Direcionado);
            }
            catch (Exception ex)
            {
                Falhar(_resultadosDirecionados, _direcionados.Keys, ex);
            }
        }

        private static TaskCompletionSource<T> NovaFonte<T>() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Abortar(Exception ex)
        {
            Falhar(_resultadosGlobais, _resultadosGlobais.Keys, ex);
            Falhar(_resultadosCombinados, _resultadosCombinados.Keys, ex);
            Falhar(_resultadosDirecionados, _resultadosDirecionados.Keys, ex);
        }

        private static void Falhar<T>(
            IReadOnlyDictionary<string, TaskCompletionSource<T>> fontes,
            IEnumerable<string> ids,
            Exception ex)
        {
            foreach (var id in ids) fontes[id].TrySetException(ex);
        }
    }
}
