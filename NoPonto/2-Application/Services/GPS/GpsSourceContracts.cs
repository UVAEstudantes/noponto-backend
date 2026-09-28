using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public static class GpsSourceNames
{
    public const string ZirixDirect = "ZIRIX_DIRECT";
    public const string Datario = "DATARIO";
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

public sealed class GpsSourcesOptions
{
    public const string Section = "GpsSources";
    public string BusPrimarySource { get; init; } = GpsSourceNames.ZirixDirect;
    public string BrtPrimarySource { get; init; } = GpsSourceNames.BrtCurrent;
    public string[] BusShadowSources { get; init; } = [GpsSourceNames.Datario];
    public string[] BrtShadowSources { get; init; } = [GpsSourceNames.Datario];
}

public interface IGpsSourceResolver
{
    IGpsSource GetPrimary(string modal);
    IReadOnlyList<IGpsSource> GetShadows(string modal);
}

public sealed class GpsSourceResolver : IGpsSourceResolver
{
    private readonly IReadOnlyDictionary<string, IGpsSource> _sources;
    private readonly GpsSourcesOptions _options;

    public GpsSourceResolver(IEnumerable<IGpsSource> sources, IOptions<GpsSourcesOptions> options)
    {
        _sources = sources.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        _options = options.Value;
    }

    public IGpsSource GetPrimary(string modal) => Resolve(PrimaryName(modal), modal, "primária");

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
