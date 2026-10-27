using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public static class GpsSourceNames
{
    public const string ZirixDirect = "ZIRIX_DIRECT";
    public const string Datario = "DATARIO";
    public const string ItsCurrent = "ITS_CURRENT";
    public const string GtfsRealtimeBus = "GTFSRT_BUS";
    public const string BrtCurrent = "BRT_CURRENT";
}

public static class GpsModalNames
{
    public const string Bus = "BUS";
    public const string Brt = "BRT";
}

public sealed record GpsQuality(
    string? SignalQuality = null,
    int? Satellites = null,
    double? Hdop = null,
    double? Vdop = null,
    double? Pdop = null,
    string? PositionSource = null,
    string? SpeedSource = null);

public sealed record GpsObservation
{
    public required string VehicleId { get; init; }
    public string? OriginalVehicleId { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required DateTimeOffset GpsTimestamp { get; init; }
    public double SpeedKmh { get; init; }
    public double? Bearing { get; init; }
    public string? ServiceCode { get; init; }
    public string? RouteId { get; init; }
    public string? DirectionId { get; init; }
    public string? ShapeId { get; init; }
    public string? TripId { get; init; }
    public required string Source { get; init; }
    public required string Provider { get; init; }
    public string? Modal { get; init; }
    public string? SourceRecordId { get; init; }
    public GpsQuality? Quality { get; init; }
    public DateTimeOffset? SourceSentAt { get; init; }
    public DateTimeOffset? SourceServerTimestamp { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
}

public interface IGpsSource
{
    string Name { get; }
    Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken cancellationToken);
}

internal interface IWindowedGpsSource : IGpsSource
{
    Task<GpsSourceReadResult> GetPositionsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

internal interface IStatusGpsSource : IGpsSource
{
    Task<GpsSourceReadResult> GetResultAsync(CancellationToken cancellationToken);
}

internal sealed record GpsSourceReadResult(
    StatusFonteGps Status,
    IReadOnlyList<GpsObservation> Observations,
    TimeSpan Duration,
    string? FailureReason = null,
    DateTimeOffset? SourceWatermark = null);

public enum GpsBusSupportMode { FullFallback, Hybrid }

public sealed class GpsSourcesOptions
{
    public const string Section = "GpsSources";
    public string BusPrimarySource { get; init; } = GpsSourceNames.ZirixDirect;
    // Shadows are diagnostic only; fallback is an explicit operational opt-in.
    public string? BusFallbackSource { get; init; }
    public GpsBusSupportMode BusSupportMode { get; init; } = GpsBusSupportMode.FullFallback;
    public int BusFallbackProbeIntervalSeconds { get; init; } = 60;
    public int BusFallbackTimeoutSeconds { get; init; } = 10;
    public string BrtPrimarySource { get; init; } = GpsSourceNames.BrtCurrent;
    public string[] BusShadowSources { get; init; } = [GpsSourceNames.Datario];
    public string[] BrtShadowSources { get; init; } = [GpsSourceNames.Datario];
}

public interface IGpsSourceResolver
{
    IGpsSource GetPrimary(string modal);
    IReadOnlyList<IGpsSource> GetShadows(string modal);
}

public sealed class GpsSourceResolver : IGpsSourceResolver, IDisposable
{
    private readonly IReadOnlyDictionary<string, IGpsSource> _sources;
    private readonly GpsSourcesOptions _options;
    private readonly IGpsSource? _busWithFallback;

    public GpsSourceResolver(IEnumerable<IGpsSource> sources, IOptions<GpsSourcesOptions> options,
        TimeProvider? clock = null, ILogger<GpsSourceResolver>? logger = null,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null, IOptions<ItsCurrentOptions>? itsOptions = null)
    {
        _sources = sources.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        _options = options.Value;
        if (_options.BusSupportMode == GpsBusSupportMode.Hybrid && itsOptions is not null && !itsOptions.Value.IsValid())
            throw new InvalidOperationException("Invalid ITS acquisition limits.");
        if (!Enum.IsDefined(_options.BusSupportMode)
            || (_options.BusSupportMode == GpsBusSupportMode.Hybrid
                && (!string.Equals(_options.BusPrimarySource, GpsSourceNames.ItsCurrent, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_options.BusFallbackSource, GpsSourceNames.GtfsRealtimeBus, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Hybrid BUS support requires ITS_CURRENT primary and GTFSRT_BUS support.");
        if (string.Equals(_options.BusPrimarySource, GpsSourceNames.ItsCurrent, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_options.BusFallbackSource))
        {
            var fallback = Resolve(_options.BusFallbackSource, GpsModalNames.Bus, "fallback");
            var supported = (string.Equals(fallback.Name, GpsSourceNames.GtfsRealtimeBus, StringComparison.OrdinalIgnoreCase)
                    && fallback is ISnapshotGpsSource)
                || (string.Equals(fallback.Name, GpsSourceNames.ZirixDirect, StringComparison.OrdinalIgnoreCase)
                    && fallback is IWindowedGpsSource); // Retained only for existing explicit legacy consumers.
            if (!supported || _options.BusFallbackProbeIntervalSeconds is < 10 or > 600
                || _options.BusFallbackTimeoutSeconds is < 1 or > 90
                || Resolve(_options.BusPrimarySource, GpsModalNames.Bus, "primária") is not ISnapshotGpsSource primary)
                throw new InvalidOperationException("ITS BUS fallback requires GTFSRT_BUS snapshot (or explicit legacy ZIRIX_DIRECT window) and valid acquisition limits.");
            // Validate opt-in, speed unit and crosswalk before starting ITS. A configured
            // but unusable backup must fail selection, not remain hidden until an outage.
            if (fallback is GtfsRealtimeGpsSource gtfs) gtfs.RequireOperational(GpsModalNames.Bus);
            _busWithFallback = new ItsFallbackGpsSource(primary, fallback, _options, clock ?? TimeProvider.System, logger,
                lifetime?.ApplicationStopping ?? default, itsOptions?.Value);
        }
    }

    // The wrapper is constructed here, not registered separately in DI. Its lifetime
    // must end with the resolver; registered primary/support sources remain DI-owned.
    public void Dispose() => (_busWithFallback as IDisposable)?.Dispose();

    public IGpsSource GetPrimary(string modal)
    {
        if (string.Equals(modal, GpsModalNames.Bus, StringComparison.OrdinalIgnoreCase) && _busWithFallback is not null)
            return _busWithFallback;
        var source = Resolve(PrimaryName(modal), modal, "primária");
        if (source is GtfsRealtimeGpsSource gtfs) gtfs.RequireOperational(modal.ToUpperInvariant());
        return source;
    }

    public IReadOnlyList<IGpsSource> GetShadows(string modal) => ShadowNames(modal)
        .Select(name => Resolve(name, modal, "shadow"))
        .ToArray();

    private string PrimaryName(string modal) => modal.ToUpperInvariant() switch
    {
        GpsModalNames.Bus => _options.BusPrimarySource,
        GpsModalNames.Brt => _options.BrtPrimarySource,
        _ => throw new InvalidOperationException($"Modal GPS desconhecido: '{modal}'.")
    };

    private IEnumerable<string> ShadowNames(string modal) => modal.ToUpperInvariant() switch
    {
        GpsModalNames.Bus => _options.BusShadowSources,
        GpsModalNames.Brt => _options.BrtShadowSources,
        _ => throw new InvalidOperationException($"Modal GPS desconhecido: '{modal}'.")
    };

    private IGpsSource Resolve(string name, string modal, string role) =>
        _sources.TryGetValue(name, out var source)
            ? source
            : throw new InvalidOperationException(
                $"Fonte GPS {role} '{name}' configurada para o modal '{modal}' não está registrada.");
}

internal static class GpsObservationMapper
{
    public static GpsObservation FromPosition(PosicaoVeiculoDto position, string source, string provider) => new()
    {
        VehicleId = position.Ordem,
        Latitude = position.Latitude,
        Longitude = position.Longitude,
        GpsTimestamp = position.TimestampGps,
        SpeedKmh = position.Velocidade,
        Bearing = position.Bearing,
        ServiceCode = position.CodigoLinha,
        Source = source,
        Provider = provider,
        SourceSentAt = position.TimestampEnvioFonte,
        SourceServerTimestamp = position.TimestampServidorFonte,
        ReceivedAtUtc = position.RecebidoEmUtc,
    };

    public static PosicaoVeiculoDto ToPosition(GpsObservation observation, string modal) => new()
    {
        Ordem = observation.VehicleId,
        // Public DTO tag denotes the source family, not Linhas.TipoRota's commercial service.
        // Keep legacy source mapping unchanged; the new BRT adapter must not inherit "onibus".
        TipoRota = observation.Source == "GTFSRT_BRT" ? "brt" : "onibus",
        CodigoLinha = observation.ServiceCode ?? observation.RouteId ?? string.Empty,
        Latitude = observation.Latitude,
        Longitude = observation.Longitude,
        Velocidade = observation.SpeedKmh,
        Bearing = observation.Bearing,
        TimestampGps = observation.GpsTimestamp,
        TimestampServidor = observation.SourceServerTimestamp ?? observation.GpsTimestamp,
        TimestampEnvioFonte = observation.SourceSentAt,
        TimestampServidorFonte = observation.SourceServerTimestamp,
        RecebidoEmUtc = observation.ReceivedAtUtc,
        ModalFonte = observation.Modal ?? modal,
        ProvedorFonte = observation.Provider,
        ObservacaoEstrutural = observation,
    };
}
