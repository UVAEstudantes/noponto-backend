using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed class EtaV2Options
{
    public bool Enabled { get; set; }
    public bool ShadowEnabled { get; set; }
    public int SamplingSeconds { get; set; } = 15;
    public double MinSpeedKmh { get; set; } = 3;
    public int PendingExpirationMinutes { get; set; } = 60;
}

public readonly record struct EtaV2Prediction(double? EtaSegundos, string? MotivoSemPrevisao);

public static class EtaV2LongitudinalSpeedV0
{
    public const string Predictor = "LONGITUDINAL_SPEED";
    public const string Version = "V0";

    public static EtaV2Prediction Predict(double distanciaMetros, double velocidadeKmh, double minSpeedKmh)
    {
        if (!double.IsFinite(distanciaMetros) || distanciaMetros < 0)
            return new(null, "DISTANCIA_INVALIDA");
        if (!double.IsFinite(velocidadeKmh))
            return new(null, "VELOCIDADE_INVALIDA");
        if (!double.IsFinite(minSpeedKmh) || minSpeedKmh <= 0)
            return new(null, "CONFIGURACAO_VELOCIDADE_INVALIDA");
        if (velocidadeKmh < minSpeedKmh)
            return new(null, velocidadeKmh <= 0 ? "VELOCIDADE_ZERO" : "VELOCIDADE_ABAIXO_MINIMO");
        var eta = distanciaMetros / (velocidadeKmh / 3.6);
        return double.IsFinite(eta) && eta >= 0
            ? new(eta, null)
            : new(null, "ETA_INVALIDO");
    }
}

public sealed record EtaV2PredictionRequest(
    Guid Id, string OrdemVeiculo, Guid ViagemId, DateTimeOffset TimestampGps,
    DateTimeOffset TimestampPrevisao, Guid LinhaId, Guid SentidoId,
    Guid PadraoOperacionalId, Guid PadraoVersaoId, Guid OcorrenciaParadaPadraoId,
    int OrdemOcorrencia, int Volta, double PosicaoNaRota,
    double DistanciaRestanteRotaMetros, double VelocidadeAtualKmh, double? Bearing,
    string? Modal, string? Provedor, double? EtaPrevistoSegundos,
    string Preditor, string VersaoPreditor, string? MotivoSemPrevisao,
    int SamplingSeconds);

public interface IEtaV2Repository
{
    Task<bool> TryInsertAsync(EtaV2PredictionRequest request, CancellationToken ct);
    Task<int> ClosePassageAsync(EventoViagem passage, Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction, CancellationToken ct);
    Task<int> ExpireAsync(DateTimeOffset cutoff, CancellationToken ct);
    Task<long> CountPendingAsync(CancellationToken ct);
}

public sealed class EtaV2Metrics
{
    private long _attempted, _persisted, _withoutEta, _realized, _expired,
        _invalidated, _failures, _latencyTicks;
    public long Attempted => Interlocked.Read(ref _attempted);
    public long Persisted => Interlocked.Read(ref _persisted);
    public long WithoutEta => Interlocked.Read(ref _withoutEta);
    public long Realized => Interlocked.Read(ref _realized);
    public long Expired => Interlocked.Read(ref _expired);
    public long Invalidated => Interlocked.Read(ref _invalidated);
    public long Failures => Interlocked.Read(ref _failures);
    public double LatencyMs => TimeSpan.FromTicks(Interlocked.Read(ref _latencyTicks)).TotalMilliseconds;
    public void Attempt() => Interlocked.Increment(ref _attempted);
    public void Persist(bool withoutEta, TimeSpan latency)
    {
        Interlocked.Increment(ref _persisted);
        if (withoutEta) Interlocked.Increment(ref _withoutEta);
        Interlocked.Add(ref _latencyTicks, latency.Ticks);
    }
    public void Realize(int count) => Interlocked.Add(ref _realized, count);
    public void Expire(int count) => Interlocked.Add(ref _expired, count);
    public void Invalidate(int count) => Interlocked.Add(ref _invalidated, count);
    public void Failure() => Interlocked.Increment(ref _failures);
}

public sealed class EtaV2ShadowService(IEtaV2Repository repository, IOptions<EtaV2Options> options,
    EtaV2Metrics metrics, ILogger<EtaV2ShadowService> logger)
{
    internal async Task TryRecordAsync(ResultadoEnriquecimentoGps enrichment,
        ViagemObservadaResultado? trip, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled || !settings.ShadowEnabled || trip?.Estado is not { } state
            || trip.Value.Status is not (ViagemObservadaStatus.Created or ViagemObservadaStatus.Updated)
            || trip.Value.ProximaOcorrenciaOperacional is not { } target)
            return;

        var position = enrichment.Posicao;
        if (position.PadraoVersaoId != state.PadraoVersaoId
            || position.ProximaOcorrenciaParadaPadraoId != target.Id
            || position.LinhaId is not { } line || line == Guid.Empty
            || position.SentidoId is not { } direction || direction == Guid.Empty
            || position.PadraoOperacionalId is not { } pattern || pattern == Guid.Empty
            || position.PosicaoNaRota is not { } fraction || !double.IsFinite(fraction)
            || position.DistanciaRestanteRotaMetros is not { } distance
            || !double.IsFinite(distance) || distance < 0)
            return;

        metrics.Attempt();
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var prediction = EtaV2LongitudinalSpeedV0.Predict(distance, position.Velocidade,
                settings.MinSpeedKmh);
            var now = DateTimeOffset.UtcNow;
            var request = new EtaV2PredictionRequest(Guid.NewGuid(), position.Ordem, state.ViagemId,
                position.TimestampGps.ToUniversalTime(), now, line, direction, pattern,
                state.PadraoVersaoId, target.Id, target.Ordem, state.Volta, fraction, distance,
                position.Velocidade, position.Bearing, position.ModalFonte, position.ProvedorFonte,
                prediction.EtaSegundos, EtaV2LongitudinalSpeedV0.Predictor,
                EtaV2LongitudinalSpeedV0.Version, prediction.MotivoSemPrevisao,
                Math.Max(1, settings.SamplingSeconds));
            if (await repository.TryInsertAsync(request, ct).ConfigureAwait(false))
                metrics.Persist(prediction.EtaSegundos is null,
                    System.Diagnostics.Stopwatch.GetElapsedTime(start));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            metrics.Failure();
            logger.LogWarning(ex,
                "ETA V2 shadow falhou para {Ordem}; GPS, viagem e ETA operacional permanecem inalterados.",
                position.Ordem);
        }
    }
}
