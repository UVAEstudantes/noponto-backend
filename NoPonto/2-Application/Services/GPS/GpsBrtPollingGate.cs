using System.Diagnostics;

namespace NoPonto.Application.GPS;

internal sealed record ResultadoBrtPolling(
    ResultadoFonteGps ResultadoEfetivo,
    bool ConsultaHttpReal,
    bool CacheReutilizado,
    TimeSpan? IdadeCache,
    StatusFonteGps? ResultadoConsulta,
    string? MotivoFalhaConsulta);

/// <summary>
/// Gate em memÃ³ria para a cadÃªncia de aquisiÃ§Ã£o BRT. O cache guarda somente
/// sucesso com posiÃ§Ãµes; validade individual continua pertencendo ao pipeline GPS.
/// </summary>
internal sealed class GpsBrtPollingGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ResultadoFonteGps? _ultimoValido;
    private long? _timestampUltimaConsulta;
    private long? _timestampCache;

    public async Task<ResultadoBrtPolling> ObterAsync(
        TimeSpan intervalo,
        Func<CancellationToken, Task<ResultadoFonteGps>> consultar,
        CancellationToken ct,
        long? timestampAtual = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var agora = timestampAtual ?? Stopwatch.GetTimestamp();
            var deveConsultar = _ultimoValido is null
                || !_timestampUltimaConsulta.HasValue
                || Stopwatch.GetElapsedTime(_timestampUltimaConsulta.Value, agora) >= intervalo;

            if (!deveConsultar)
                return ReutilizarCache(agora, resultadoConsulta: null);

            _timestampUltimaConsulta = agora;
            var consulta = await consultar(ct).ConfigureAwait(false);
            if (consulta.Status == StatusFonteGps.Sucesso && consulta.Posicoes.Count > 0)
            {
                _ultimoValido = consulta;
                _timestampCache = agora;
                return new ResultadoBrtPolling(
                    consulta, ConsultaHttpReal: true, CacheReutilizado: false,
                    IdadeCache: TimeSpan.Zero, ResultadoConsulta: consulta.Status,
                    MotivoFalhaConsulta: consulta.MotivoFalha);
            }

            if (_ultimoValido is not null)
            {
                var reutilizado = ReutilizarCache(agora, consulta.Status, consulta.MotivoFalha);
                return reutilizado with { ConsultaHttpReal = true };
            }

            return new ResultadoBrtPolling(
                consulta, ConsultaHttpReal: true, CacheReutilizado: false,
                IdadeCache: null, ResultadoConsulta: consulta.Status,
                MotivoFalhaConsulta: consulta.MotivoFalha);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ResultadoBrtPolling ReutilizarCache(
        long agora, StatusFonteGps? resultadoConsulta, string? motivoFalhaConsulta = null)
    {
        var idade = _timestampCache.HasValue
            ? Stopwatch.GetElapsedTime(_timestampCache.Value, agora)
            : TimeSpan.Zero;
        return new ResultadoBrtPolling(
            _ultimoValido!, ConsultaHttpReal: false, CacheReutilizado: true,
            IdadeCache: idade, ResultadoConsulta: resultadoConsulta,
            MotivoFalhaConsulta: motivoFalhaConsulta);
    }
}
