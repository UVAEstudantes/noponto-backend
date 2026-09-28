namespace NoPonto.Application.GPS;

public sealed class ZirixGpsSource(GpsSppoClient client) : IWindowedGpsSource
{
    public string Name => GpsSourceNames.ZirixDirect;

    public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken cancellationToken)
        => (await GetPositionsAsync(DateTimeOffset.UtcNow.AddSeconds(-60), DateTimeOffset.UtcNow,
            cancellationToken)).Observations;

    async Task<GpsSourceReadResult> IWindowedGpsSource.GetPositionsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        => await GetPositionsAsync(from, to, cancellationToken);

    private async Task<GpsSourceReadResult> GetPositionsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var result = await client.BuscarResultadoPorIntervaloAsync(from, to, cancellationToken);
        return new(result.Status,
            result.Posicoes.Select(x => GpsObservationMapper.FromPosition(x, Name, "ZIRIX")).ToArray(),
            result.Duracao, result.MotivoFalha, result.WatermarkFonte);
    }
}

public sealed class BrtCurrentGpsSource(GpsBrtClient client) : IStatusGpsSource
{
    public string Name => GpsSourceNames.BrtCurrent;

    public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken cancellationToken)
        => (await ReadResultAsync(cancellationToken)).Observations;

    Task<GpsSourceReadResult> IStatusGpsSource.GetResultAsync(CancellationToken cancellationToken)
        => ReadResultAsync(cancellationToken);

    private async Task<GpsSourceReadResult> ReadResultAsync(CancellationToken cancellationToken)
    {
        var result = await client.BuscarResultadoAsync(cancellationToken);
        return new(result.Status,
            result.Posicoes.Select(x => GpsObservationMapper.FromPosition(x, Name, "BRT_RIO")).ToArray(),
            result.Duracao, result.MotivoFalha, result.WatermarkFonte);
    }
}

public sealed class DatarioGpsSource(GpsDatarioClient client) : IGpsSource
{
    public string Name => GpsSourceNames.Datario;

    public async Task<IReadOnlyList<GpsObservation>> GetPositionsAsync(CancellationToken cancellationToken)
    {
        var collection = await client.BuscarTodasPaginasAsync(new GpsDatarioQuery(), cancellationToken);
        return collection.Data.Select(Map).Where(x => x is not null).Cast<GpsObservation>().ToArray();
    }

    internal static GpsObservation? Map(GpsDatarioVehicleDto value)
    {
        if (string.IsNullOrWhiteSpace(value.IdVeiculo) || value.Latitude is null || value.Longitude is null
            || value.TimestampGps is null || !GpsLeituraValidator.CoordenadaValida(value.Latitude.Value, value.Longitude.Value))
            return null;

        var provider = string.IsNullOrWhiteSpace(value.Fornecedor)
            ? "UNKNOWN"
            : value.Fornecedor.Trim().ToUpperInvariant();
        return new GpsObservation
        {
            VehicleId = value.IdVeiculo.Trim(),
            Latitude = value.Latitude.Value,
            Longitude = value.Longitude.Value,
            GpsTimestamp = value.TimestampGps.Value,
            SpeedKmh = value.Velocidade ?? 0,
            Bearing = NormalizeBearing(value.Direcao),
            ServiceCode = Empty(value.Servico),
            RouteId = Empty(value.RouteId),
            DirectionId = Empty(value.DirectionId),
            ShapeId = Empty(value.ShapeId),
            TripId = Empty(value.TripId),
            Source = GpsSourceNames.Datario,
            Provider = provider,
            Modal = Empty(value.Modo)?.ToUpperInvariant(),
            SourceRecordId = Empty(value.IdRegistro),
            SourceSentAt = value.TimestampEnvio,
            SourceServerTimestamp = value.TimestampServidor,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            Quality = new GpsQuality(Empty(value.QualidadeSinal), value.QuantidadeSatelites,
                value.Hdop, value.Vdop, value.Pdop, Empty(value.FontePosicao), Empty(value.FonteVelocidade)),
        };
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static double? NormalizeBearing(double? value) => value is >= 0 and <= 360 && double.IsFinite(value.Value)
        ? value == 360 ? 0 : value
        : null;
}
