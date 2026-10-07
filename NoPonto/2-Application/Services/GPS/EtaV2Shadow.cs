using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed class EtaV2Options
{
    public bool Enabled { get; set; }
    public bool ShadowEnabled { get; set; }
    public int CanaryPercent { get; set; }
    public int SamplingSeconds { get; set; } = 15;
    public double MinSpeedKmh { get; set; } = 3;
    public int PendingExpirationMinutes { get; set; } = 60;
    public int QueueCapacity { get; set; } = 5000;
    public int BatchSize { get; set; } = 250;
    public int BatchMaxDelayMs { get; set; } = 500;
    public int PersistenceRetryDelayMs { get; set; } = 1000;
    public int ShutdownDrainSeconds { get; set; } = 10;
    public int ExpirationBatchSize { get; set; } = 5000;
    public int PendingCountIntervalMinutes { get; set; } = 5;

    public bool Valid() => CanaryPercent is >= 0 and <= 100 && SamplingSeconds > 0
        && double.IsFinite(MinSpeedKmh) && MinSpeedKmh > 0 && PendingExpirationMinutes > 0
        && QueueCapacity > 0 && BatchSize > 0 && BatchSize <= QueueCapacity
        && BatchMaxDelayMs > 0 && PersistenceRetryDelayMs > 0 && ShutdownDrainSeconds > 0
        && ExpirationBatchSize > 0 && PendingCountIntervalMinutes > 0;
}

public readonly record struct EtaV2Prediction(double? EtaSegundos, string? MotivoSemPrevisao);

public static class EtaV2LongitudinalSpeedV0
{
    public const string Predictor = "LONGITUDINAL_SPEED";
    public const string Version = "V0";
    public static EtaV2Prediction Predict(double distance, double speed, double minimum)
    {
        if (!double.IsFinite(distance) || distance < 0) return new(null, "DISTANCIA_INVALIDA");
        if (!double.IsFinite(speed)) return new(null, "VELOCIDADE_INVALIDA");
        if (!double.IsFinite(minimum) || minimum <= 0) return new(null, "CONFIGURACAO_VELOCIDADE_INVALIDA");
        if (speed < minimum) return new(null, speed <= 0 ? "VELOCIDADE_ZERO" : "VELOCIDADE_ABAIXO_MINIMO");
        var eta = distance / (speed / 3.6);
        return double.IsFinite(eta) && eta >= 0 ? new(eta, null) : new(null, "ETA_INVALIDO");
    }
}

public sealed record EtaV2PredictionRequest(Guid Id, string OrdemVeiculo, Guid ViagemId,
    DateTimeOffset TimestampGps, DateTimeOffset TimestampPrevisao, Guid LinhaId, Guid SentidoId,
    Guid PadraoOperacionalId, Guid PadraoVersaoId, Guid OcorrenciaParadaPadraoId,
    int OrdemOcorrencia, int Volta, double PosicaoNaRota, double DistanciaRestanteRotaMetros,
    double VelocidadeAtualKmh, double? Bearing, string? Modal, string? Provedor,
    double? EtaPrevistoSegundos, string Preditor, string VersaoPreditor,
    string? MotivoSemPrevisao, int SamplingSeconds);

public readonly record struct EtaV2BatchPersistResult(int Requested, int Persisted,
    int WithoutEta, int Invalidated);

public interface IEtaV2Repository
{
    Task<EtaV2BatchPersistResult> PersistBatchAsync(IReadOnlyList<EtaV2PredictionRequest> requests, CancellationToken ct);
    Task<int> ClosePassageAsync(EventoViagem passage, Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction, CancellationToken ct);
    Task<int> ExpireBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct);
    Task<long> CountPendingAsync(CancellationToken ct);
}

public interface IEtaV2Ingress { bool TryWrite(EtaV2PredictionRequest request); }

public sealed class EtaV2Metrics
{
    private long _eligible, _ineligible, _skippedCanary, _enqueued, _dropped, _dequeued,
        _batches, _batchItems, _persisted, _withoutEta, _realized, _expired, _invalidated,
        _failures, _batchLatencyTicks, _persistenceLatencyTicks;
    public long Eligible => Interlocked.Read(ref _eligible);
    public long Ineligible => Interlocked.Read(ref _ineligible);
    public long SkippedCanary => Interlocked.Read(ref _skippedCanary);
    public long Enqueued => Interlocked.Read(ref _enqueued);
    public long DroppedQueueFull => Interlocked.Read(ref _dropped);
    public long QueueDepth => Math.Max(0, Enqueued - Interlocked.Read(ref _dequeued));
    public long Batches => Interlocked.Read(ref _batches);
    public long BatchItems => Interlocked.Read(ref _batchItems);
    public long Persisted => Interlocked.Read(ref _persisted);
    public long WithoutEta => Interlocked.Read(ref _withoutEta);
    public long Realized => Interlocked.Read(ref _realized);
    public long Expired => Interlocked.Read(ref _expired);
    public long Invalidated => Interlocked.Read(ref _invalidated);
    public long Failures => Interlocked.Read(ref _failures);
    public double BatchLatencyMs => TimeSpan.FromTicks(Interlocked.Read(ref _batchLatencyTicks)).TotalMilliseconds;
    public double PersistenceLatencyMs => TimeSpan.FromTicks(Interlocked.Read(ref _persistenceLatencyTicks)).TotalMilliseconds;
    public void EligibleCandidate() => Interlocked.Increment(ref _eligible);
    public void IneligibleCandidate() => Interlocked.Increment(ref _ineligible);
    public void SkipCanary() => Interlocked.Increment(ref _skippedCanary);
    public void Enqueue() => Interlocked.Increment(ref _enqueued);
    public void DropQueueFull() => Interlocked.Increment(ref _dropped);
    public void Dequeue(int count) => Interlocked.Add(ref _dequeued, count);
    public void Batch(int count, TimeSpan latency) { Interlocked.Increment(ref _batches); Interlocked.Add(ref _batchItems, count); Interlocked.Add(ref _batchLatencyTicks, latency.Ticks); }
    public void Persist(EtaV2BatchPersistResult result, TimeSpan latency) { Interlocked.Add(ref _persisted, result.Persisted); Interlocked.Add(ref _withoutEta, result.WithoutEta); Interlocked.Add(ref _invalidated, result.Invalidated); Interlocked.Add(ref _persistenceLatencyTicks, latency.Ticks); }
    public void Realize(int count) => Interlocked.Add(ref _realized, count);
    public void Expire(int count) => Interlocked.Add(ref _expired, count);
    public void Failure() => Interlocked.Increment(ref _failures);
}

public sealed class EtaV2Channel : IEtaV2Ingress
{
    private readonly Channel<EtaV2PredictionRequest> _channel;
    private readonly EtaV2Metrics _metrics;
    private int _completed;
    public EtaV2Channel(IOptions<EtaV2Options> options, EtaV2Metrics metrics)
    {
        _metrics = metrics;
        _channel = Channel.CreateBounded<EtaV2PredictionRequest>(new BoundedChannelOptions(options.Value.QueueCapacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    }
    public ChannelReader<EtaV2PredictionRequest> Reader => _channel.Reader;
    public bool TryWrite(EtaV2PredictionRequest request)
    {
        var accepted = Volatile.Read(ref _completed) == 0 && _channel.Writer.TryWrite(request);
        if (accepted) _metrics.Enqueue(); else _metrics.DropQueueFull();
        return accepted;
    }
    public void Complete() { if (Interlocked.Exchange(ref _completed, 1) == 0) _channel.Writer.TryComplete(); }
    public void RecordRead(int count) => _metrics.Dequeue(count);
}

public sealed class EtaV2ShadowService(IEtaV2Ingress ingress, IOptions<EtaV2Options> options,
    EtaV2Metrics metrics)
{
    internal bool TryRecord(ResultadoEnriquecimentoGps enrichment, ViagemObservadaResultado? trip)
    {
        var settings = options.Value;
        if (!settings.Enabled || !settings.ShadowEnabled || trip?.Estado is not { } state
            || trip.Value.Status is not (ViagemObservadaStatus.Created or ViagemObservadaStatus.Updated)
            || (trip.Value.EstadoOperacional is { } operacional && !ViagemOperacionalRegra.IdentidadeConfiavel(operacional))
            || trip.Value.ProximaOcorrenciaOperacional is not { } target)
        { metrics.IneligibleCandidate(); return false; }
        var position = enrichment.Posicao;
        if (state.ViagemId == Guid.Empty || state.OrdemVeiculo != position.Ordem
            || state.TimestampUltimaAtualizacao != position.TimestampGps
            || state.PadraoOperacionalId == Guid.Empty || position.PadraoOperacionalId != state.PadraoOperacionalId
            || target.Id == Guid.Empty || target.PadraoVersaoId != state.PadraoVersaoId
            || (trip.Value.EstadoOperacional is { } identity
                && (identity.Observada != state
                    || identity.Estado is not (EstadoViagem.Ativa or EstadoViagem.PossivelFim)
                    || identity.CodigoLinha != position.CodigoLinha
                    || identity.LinhaId != position.LinhaId || identity.SentidoId != position.SentidoId
                    || state.Topologia != position.TopologiaPadrao))
            || position.PadraoVersaoId != state.PadraoVersaoId
            || position.ProximaOcorrenciaParadaPadraoId != target.Id
            || position.LinhaId is not { } line || line == Guid.Empty
            || position.SentidoId is not { } direction || direction == Guid.Empty
            || position.PadraoOperacionalId is not { } pattern || pattern == Guid.Empty
            || position.PosicaoNaRota is not { } fraction || !double.IsFinite(fraction)
            || position.DistanciaRestanteRotaMetros is not { } distance
            || !double.IsFinite(distance) || distance < 0)
        { metrics.IneligibleCandidate(); return false; }
        metrics.EligibleCandidate();
        if (!EtaV2Canary.Includes(position.Ordem, settings.CanaryPercent))
        { metrics.SkipCanary(); return false; }
        var prediction = EtaV2LongitudinalSpeedV0.Predict(distance, position.Velocidade, settings.MinSpeedKmh);
        return ingress.TryWrite(new EtaV2PredictionRequest(Guid.NewGuid(), position.Ordem, state.ViagemId,
            position.TimestampGps.ToUniversalTime(), DateTimeOffset.UtcNow, line, direction, pattern,
            state.PadraoVersaoId, target.Id, target.Ordem, state.Volta, fraction, distance,
            position.Velocidade, position.Bearing, position.ModalFonte, position.ProvedorFonte,
            prediction.EtaSegundos, EtaV2LongitudinalSpeedV0.Predictor,
            EtaV2LongitudinalSpeedV0.Version, prediction.MotivoSemPrevisao,
            Math.Max(1, settings.SamplingSeconds)));
    }
}

public static class EtaV2Canary
{
    public static bool Includes(string vehicleId, int percent)
    {
        if (percent <= 0) return false;
        if (percent >= 100) return true;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(vehicleId.Trim().ToUpperInvariant()));
        return BinaryPrimitives.ReadUInt32BigEndian(hash) % 10_000 < percent * 100;
    }
}
