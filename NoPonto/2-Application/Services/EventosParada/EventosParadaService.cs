using Microsoft.EntityFrameworkCore;
using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremSchedule;

namespace NoPonto.Application.Services.EventosParada;

public sealed class EventosParadaService(TransporteDbContext db, IVeiculosLinhaRuntimeReader roadRuntime,
    IExpectedRunService expectedRuns, IRailPublishedSnapshotProvider railSnapshot)
    : IEventosParadaService
{
    public async Task<IReadOnlyList<EventoParadaDto>?> ListarAsync(Guid paradaId,
        DateTimeOffset now, CancellationToken ct = default)
    {
        if (!await db.Paradas.AsNoTracking().AnyAsync(x => x.Id == paradaId, ct)) return null;

        var occurrences = await db.OcorrenciasParadasPadroes.AsNoTracking()
            // VersaoAtualId é a fonte de verdade do contrato estrutural V2. Importações
            // existentes podem ter PublicadoEmUtc nulo mesmo quando a versão é a atual.
            .Where(x => x.ParadaId == paradaId
                && (x.PadraoVersao.PadraoOperacional.VersaoAtualId == x.PadraoVersaoId
                    || x.PadraoVersao.PublicadoEmUtc != null))
            .Select(x => new StopContext(x.Id, x.ParadaId, x.Ordem, x.PosicaoTracado,
                x.DistanciaAcumuladaMetros, x.PadraoVersaoId, x.PadraoVersao.ComprimentoMetros,
                x.PadraoVersao.PadraoOperacionalId, x.PadraoVersao.PadraoOperacional.SentidoId,
                x.PadraoVersao.PadraoOperacional.Sentido.LinhaId,
                x.PadraoVersao.PadraoOperacional.Sentido.Linha.Codigo,
                x.PadraoVersao.PadraoOperacional.Sentido.Linha.Modal.Nome,
                x.PadraoVersao.PadraoOperacional.Sentido.Linha.TipoRota))
            .ToArrayAsync(ct);

        var result = new List<EventoParadaDto>();
        result.AddRange(await RoadEvents(occurrences, now, ct));
        result.AddRange(await RailEvents(occurrences, now, ct));
        return Order(result);
    }

    private async Task<IReadOnlyList<EventoParadaDto>> RoadEvents(
        StopContext[] stops, DateTimeOffset now, CancellationToken ct)
    {
        var road = stops.Where(x => !IsRail(x.Modal)).ToArray();
        if (road.Length == 0) return [];
        var lineCodes = road.Select(x => x.CodigoLinha.ToUpperInvariant()).Distinct().ToArray();
        var positions = (await roadRuntime.ListarAsync(lineCodes, ct)).Posicoes;
        return ProjectRoadEvents(road, positions, now);
    }

    internal static IReadOnlyList<EventoParadaDto> ProjectRoadEvents(
        StopContext[] road, IEnumerable<PosicaoVeiculoDto> positions, DateTimeOffset now)
    {
        var events = new List<EventoParadaDto>();
        foreach (var position in positions)
        {
            if (position.PadraoVersaoId is not { } version || position.PosicaoNaRota is not { } progress) continue;
            var candidates = road.Where(x => x.PadraoVersaoId == version && x.PosicaoTracado + 1e-7 >= progress)
                .OrderBy(x => x.PosicaoTracado).ThenBy(x => x.Ordem).ToArray();
            // Repetições/circulares: somente a primeira ocorrência futura desta parada.
            var stop = candidates.FirstOrDefault();
            if (stop is null) continue;
            var remaining = Math.Max(0, (stop.PosicaoTracado - progress) * stop.ComprimentoMetros);
            var seconds = EstimateRoadSeconds(position, stop, remaining);
            DateTimeOffset? estimated = seconds is null ? null : now.AddSeconds(seconds.Value);
            var routeType = string.Equals(stop.TipoRota, "brt", StringComparison.OrdinalIgnoreCase) ? "brt" : "onibus";
            events.Add(new($"road:{position.Ordem}:{stop.Id:D}", stop.Ordem == 1 ? TiposEventoParada.Departure : TiposEventoParada.Arrival,
                stop.LinhaId, stop.CodigoLinha, "onibus", routeType, stop.SentidoId, stop.PadraoOperacionalId,
                stop.PadraoVersaoId, stop.ParadaId, stop.Id, position.Ordem, null, null, position.Ordem, null,
                null, estimated, seconds is null ? null : (long?)Math.Round(seconds.Value), "RealtimeEstimated",
                position.EtaConfianca ?? "OperationalEstimate", position.TimestampGps, true, remaining));
        }
        return events;
    }

    private async Task<IReadOnlyList<EventoParadaDto>> RailEvents(StopContext[] stops,
        DateTimeOffset now, CancellationToken ct)
    {
        var railStops = stops.Where(x => IsRail(x.Modal)).ToArray();
        if (railStops.Length == 0) return [];
        var snapshots = railSnapshot.CaptureSnapshot().PublicVehicles
            .ToDictionary(x => x.RailRunId, x => x);
        var nextOccurrenceIds = snapshots.Values.Where(x => x.NextOccurrenceId.HasValue)
            .Select(x => x.NextOccurrenceId!.Value).Distinct().ToArray();
        var nextStops = await db.OcorrenciasParadasPadroes.AsNoTracking()
            .Where(x => nextOccurrenceIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.ParadaId, ct);
        var events = new List<EventoParadaDto>();
        foreach (var line in railStops.GroupBy(x => x.LinhaId))
        {
            var runs = await expectedRuns.InWindowAsync(line.Key, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(4), ct);
            foreach (var run in runs.Where(x => x.State == ExpectedRunService.ExpectedState))
            foreach (var scheduledStop in run.Stops.Where(x => x.ParadaId == line.First().ParadaId))
            {
                var structural = line.Where(x => x.PadraoVersaoId == run.MappedPadraoVersaoId)
                    .OrderBy(x => Math.Abs(x.Ordem - scheduledStop.StopSequence)).FirstOrDefault();
                if (structural is null) continue;
                snapshots.TryGetValue(run.ExpectedRunId, out var snapshot);
                var delay = ResolveDelay(run, snapshot, nextStops);
                var estimated = scheduledStop.ExpectedAt.Add(delay);
                if (estimated < now.AddMinutes(-2)) continue;
                var source = snapshot?.PositionSource.ToString() ?? "ScheduledEstimated";
                var quality = snapshot?.PositionQuality.ToString() ?? "ScheduleOnly";
                var evidence = snapshot is null || snapshot.LastRealtimeEvidenceUtc == DateTimeOffset.MinValue
                    ? null : (DateTimeOffset?)snapshot.LastRealtimeEvidenceUtc;
                events.Add(new($"rail:{run.ExpectedRunId:D}:{scheduledStop.ScheduledStopId:D}",
                    scheduledStop.StopSequence == 1 ? TiposEventoParada.Departure : TiposEventoParada.Arrival,
                    structural.LinhaId, structural.CodigoLinha, "trem", "trem", structural.SentidoId,
                    structural.PadraoOperacionalId, structural.PadraoVersaoId, structural.ParadaId, structural.Id,
                    null, snapshot?.RailVehicleId, run.ExpectedRunId, null,
                    string.IsNullOrWhiteSpace(snapshot?.TrainCode) ? null : snapshot.TrainCode,
                    scheduledStop.ExpectedAt, estimated, (long)Math.Round((estimated - now).TotalSeconds),
                    source, quality, evidence, true));
            }
        }
        return events;
    }

    internal static double? EstimateRoadSeconds(PosicaoVeiculoDto position, StopContext stop, double remaining)
    {
        if (position.ProximaOcorrenciaParadaPadraoId == stop.Id && position.EtaProximaParadaSegundos is >= 0)
            return position.EtaProximaParadaSegundos;
        var speed = position.VelocidadeMedia is > 2 ? position.VelocidadeMedia : position.Velocidade is > 2 ? position.Velocidade : 15;
        return remaining / (speed / 3.6);
    }

    private static TimeSpan ResolveDelay(ExpectedRun run, RailVehiclePublicSnapshot? snapshot,
        IReadOnlyDictionary<Guid, Guid> paradaByOccurrence)
    {
        if (snapshot?.NextOccurrenceId is not { } nextId) return TimeSpan.Zero;
        var expected = paradaByOccurrence.TryGetValue(nextId, out var paradaId)
            ? run.Stops.FirstOrDefault(x => x.ParadaId == paradaId) : null;
        return expected is null ? TimeSpan.Zero : snapshot.TargetTimeUtc - expected.ExpectedAt;
    }

    internal static IReadOnlyList<EventoParadaDto> Order(IEnumerable<EventoParadaDto> values) => values
        .OrderBy(x => x.EstimatedAt ?? x.ScheduledAt ?? DateTimeOffset.MaxValue)
        .ThenBy(x => x.SecondsUntilEvent ?? long.MaxValue).ThenBy(x => x.EventId, StringComparer.Ordinal).ToArray();

    private static bool IsRail(string modal) => modal.Contains("trem", StringComparison.OrdinalIgnoreCase)
        || modal.Contains("ferro", StringComparison.OrdinalIgnoreCase);

    internal sealed record StopContext(Guid Id, Guid ParadaId, int Ordem, double PosicaoTracado,
        double DistanciaAcumuladaMetros, Guid PadraoVersaoId, double ComprimentoMetros,
        Guid PadraoOperacionalId, Guid SentidoId, Guid LinhaId, string CodigoLinha, string Modal, string TipoRota);
}
