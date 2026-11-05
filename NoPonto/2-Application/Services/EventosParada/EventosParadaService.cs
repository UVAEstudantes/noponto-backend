using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.TremRealtime.RailRuntime;
using NoPonto.Application.TremSchedule;

namespace NoPonto.Application.Services.EventosParada;

public sealed class EventosParadaService(TransporteDbContext db, IVeiculosLinhaRuntimeReader roadRuntime,
    IExpectedRunService expectedRuns, IRailPublishedSnapshotProvider railSnapshot,
    IOptions<PhysicalEtaOptions>? physicalEtaOptions = null,
    IRoadEtaDistances? roadEtaDistances = null,
    EtaCausalTestDiagnostics? testDiagnostics = null, AdaptiveEtaShadow? adaptiveShadow = null)
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
        return await ProjectRoadEventsAsync(road, positions, now, physicalEtaOptions?.Value, roadEtaDistances, ct, testDiagnostics, adaptiveShadow);
    }

    internal static async Task<IReadOnlyList<EventoParadaDto>> ProjectRoadEventsAsync(
        StopContext[] road, IEnumerable<PosicaoVeiculoDto> positions, DateTimeOffset now,
        PhysicalEtaOptions? options, IRoadEtaDistances? resolver, CancellationToken ct,
        EtaCausalTestDiagnostics? testDiagnostics = null, AdaptiveEtaShadow? adaptiveShadow = null)
    {
        if (options?.Enabled != true) return ProjectRoadEvents(road, positions, now, options);
        var snapshot = positions.ToArray();
        var distances = new Dictionary<(string, Guid), double>();
        var outcomes = testDiagnostics is null ? null : new Dictionary<(string, Guid), EtaGeometryOutcome>();
        var requests = snapshot.Where(p => PhysicalEta.CapturedSpeed(p, now, options).HasValue)
            .Select(p => (Position:p, Stop:SelectRoadTarget(road, p)))
            .Where(x => x.Stop is not null && Compatible(x.Position, x.Stop))
            .Take(1000).Select(x => new RoadEtaDistanceRequest(x.Position, x.Stop!.Id, x.Stop.ParadaId, x.Stop.Ordem)).ToArray();
        if (resolver is not null)
            foreach (var chunk in requests.Chunk(200))
            {
                try {
                    var values = await resolver.ResolveAsync(chunk, ct);
                    if (values.Count != chunk.Length)
                    {
                        if (outcomes is not null) foreach (var request in chunk)
                            outcomes[(request.Position.Ordem, request.TargetId)] = EtaGeometryOutcome.count_mismatch;
                        continue;
                    }
                    for (var i=0; i<chunk.Length; i++)
                    {
                        if (values[i] is { } metres && double.IsFinite(metres) && metres > 0)
                            distances[(chunk[i].Position.Ordem,chunk[i].TargetId)] = metres;
                        if (outcomes is not null) outcomes[(chunk[i].Position.Ordem,chunk[i].TargetId)] =
                            distances.ContainsKey((chunk[i].Position.Ordem,chunk[i].TargetId))
                                ? EtaGeometryOutcome.success : EtaGeometryOutcome.absent;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (outcomes is not null) foreach (var request in chunk)
                        outcomes[(request.Position.Ordem, request.TargetId)] =
                            ex is TimeoutException or OperationCanceledException
                            || ex is Npgsql.NpgsqlException { InnerException: TimeoutException }
                            || ex is Npgsql.PostgresException { SqlState: "57014" or "55P03" }
                                ? EtaGeometryOutcome.timeout : EtaGeometryOutcome.error;
                }
            }
        var events = ProjectRoadEvents(road, snapshot, now, options, distances).ToList();
        if ((options.AdaptiveShadowEnabled || options.HybridShadowEnabled || options.HybridPublicEnabled) && adaptiveShadow is not null)
            foreach (var p in snapshot)
            {
                var target = SelectRoadTarget(road,p);
                var evt = target is null ? null : events.FirstOrDefault(x => x.EventId == $"road:{p.Ordem}:{target.Id:D}");
                var comparison = adaptiveShadow.Compare(p, target is not null && distances.TryGetValue((p.Ordem,target.Id),out var metres) ? metres : null,
                    evt?.EstimatedAt is { } at ? (at-now).TotalSeconds : null, now,options,target?.Id);
                if (options.HybridPublicEnabled && evt is not null)
                {
                    var candidate = comparison.Hybrid;
                    // Only replace an already eligible physical event. All capture/target/trip gates remain upstream.
                    if (evt.EstimatedAt.HasValue && candidate is { Seconds: { } seconds, EstimatedAt: { } estimated })
                    {
                        events[events.IndexOf(evt)] = evt with { EstimatedAt = estimated,
                            SecondsUntilEvent = (long)Math.Round(seconds),
                            Source = candidate.Source == "fixed20" ? "HybridFixed20" : "HybridRecentEma", Quality = candidate.Quality };
                        HybridEtaPublication.Record(true, false, "available");
                    }
                    else HybridEtaPublication.Record(false, evt.EstimatedAt.HasValue, candidate?.Reason ?? "history_absent");
                }
            }
        if (testDiagnostics is not null)
            foreach (var position in snapshot)
            {
                var stop = SelectRoadTarget(road, position);
                var outcome = stop is null ? EtaGeometryOutcome.target_absent
                    : !Compatible(position, stop) ? EtaGeometryOutcome.incompatible
                    : !PhysicalEta.CapturedSpeed(position, now, options).HasValue ? EtaGeometryOutcome.ineligible
                    : resolver is null ? EtaGeometryOutcome.resolver_absent
                    : outcomes!.GetValueOrDefault((position.Ordem, stop.Id), EtaGeometryOutcome.limit);
                var evt = stop is null ? null : events.FirstOrDefault(x =>
                    x.EventId == $"road:{position.Ordem}:{stop.Id:D}");
                testDiagnostics.Record(position, stop?.Id, stop?.DistanciaAcumuladaMetros,
                    stop is not null && distances.TryGetValue((position.Ordem, stop.Id), out var distance) ? distance : null,
                    now, options, outcome, evt?.EstimatedAt is { } at ? (at - now).TotalSeconds : null);
            }
        return events;
    }

    private static bool Compatible(PosicaoVeiculoDto p, StopContext s) =>
        p.LinhaId == s.LinhaId && p.SentidoId == s.SentidoId && p.CodigoLinha == s.CodigoLinha
        && p.PadraoOperacionalId == s.PadraoOperacionalId && p.PadraoVersaoId == s.PadraoVersaoId;

    private static StopContext? SelectRoadTarget(StopContext[] road, PosicaoVeiculoDto p) =>
        p.PosicaoNaRota is not { } progress ? null : road
            .Where(x => x.PadraoVersaoId == p.PadraoVersaoId && x.PosicaoTracado + 1e-7 >= progress)
            .OrderBy(x => x.PosicaoTracado).ThenBy(x => x.Ordem).FirstOrDefault();

    internal static IReadOnlyList<EventoParadaDto> ProjectRoadEvents(
        StopContext[] road, IEnumerable<PosicaoVeiculoDto> positions, DateTimeOffset now,
        PhysicalEtaOptions? physicalOptions = null,
        IReadOnlyDictionary<(string, Guid), double>? resolvedDistances = null)
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
            var legacyRemaining = Math.Max(0, (stop.PosicaoTracado - progress) * stop.ComprimentoMetros);
            double? remaining = physicalOptions?.Enabled == true ? null : legacyRemaining;
            var seconds = physicalOptions?.Enabled == true ? null : EstimateRoadSeconds(position, stop, legacyRemaining);
            var quality = position.EtaConfianca ?? "OperationalEstimate";
            var source = "RealtimeEstimated";
            if (physicalOptions?.Enabled == true)
            {
                seconds = null;
                quality = "previsao_indisponivel";
                source = "PhysicalCausal";
                var age = (now - position.TimestampGps).TotalSeconds;
                if (physicalOptions.Valid() && age > physicalOptions.MaxAgeSeconds)
                    quality = "gps_desatualizado";
                else if (physicalOptions.Valid() && age >= 0
                    && PhysicalEta.CapturedSpeed(position, now, physicalOptions).HasValue
                    && position.LinhaId == stop.LinhaId && position.SentidoId == stop.SentidoId
                    && position.PadraoOperacionalId == stop.PadraoOperacionalId
                    && position.CodigoLinha == stop.CodigoLinha
                    && position.ProximaOcorrenciaParadaPadraoId == stop.Id
                    && position.EtaConfianca == PhysicalEta.Prefix + "estimativa_fisica"
                    && position.EtaProximaParadaSegundos is { } eta && double.IsFinite(eta) && eta >= age
                    && position.DistanciaRestanteRotaMetros is { } metres && double.IsFinite(metres) && metres > 0)
                {
                    seconds = eta - age;
                    remaining = metres;
                    quality = "estimativa_fisica";
                }
                if (resolvedDistances is not null)
                {
                    seconds = null;
                    remaining = null;
                    if (Compatible(position, stop) && PhysicalEta.CapturedSpeed(position, now, physicalOptions) is { } speed
                        && resolvedDistances.TryGetValue((position.Ordem,stop.Id), out var routeDistance))
                    {
                        var targetEta = EtaV2LongitudinalSpeedV0.Predict(routeDistance, speed, 1).EtaSegundos;
                        if (targetEta is { } value && value >= age)
                        {
                            seconds = value - age;
                            remaining = routeDistance;
                        }
                    }
                    quality = seconds.HasValue ? "estimativa_fisica"
                        : physicalOptions.Valid() && age > physicalOptions.MaxAgeSeconds
                            ? "gps_desatualizado" : "previsao_indisponivel";
                }
            }
            DateTimeOffset? estimated = seconds is null ? null : now.AddSeconds(seconds.Value);
            var routeType = string.Equals(stop.TipoRota, "brt", StringComparison.OrdinalIgnoreCase) ? "brt" : "onibus";
            events.Add(new($"road:{position.Ordem}:{stop.Id:D}", stop.Ordem == 1 ? TiposEventoParada.Departure : TiposEventoParada.Arrival,
                stop.LinhaId, stop.CodigoLinha, "onibus", routeType, stop.SentidoId, stop.PadraoOperacionalId,
                stop.PadraoVersaoId, stop.ParadaId, stop.Id, position.Ordem, null, null, position.Ordem, null,
                null, estimated, seconds is null ? null : (long?)Math.Round(seconds.Value), source,
                quality, position.TimestampGps, true, remaining,
                stop.Ordem == 1 ? PapeisEstacaoEvento.Origin : PapeisEstacaoEvento.Intermediate,
                stop.Ordem == 1 ? ModosProximosVeiculos.Departures : ModosProximosVeiculos.Arrivals));
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
                var stationRole = ResolveStationRole(scheduledStop.StopSequence,
                    run.Stops.Max(x => x.StopSequence));
                // A chegada ao destino encerra este run; ela não é uma futura
                // passagem nem uma saída deste mesmo serviço.
                if (!ShouldExposeRailEvent(stationRole)) continue;
                events.Add(new($"rail:{run.ExpectedRunId:D}:{scheduledStop.ScheduledStopId:D}",
                    scheduledStop.StopSequence == 1 ? TiposEventoParada.Departure : TiposEventoParada.Arrival,
                    structural.LinhaId, structural.CodigoLinha, "trem", "trem", structural.SentidoId,
                    structural.PadraoOperacionalId, structural.PadraoVersaoId, structural.ParadaId, structural.Id,
                    null, snapshot?.RailVehicleId, run.ExpectedRunId, null,
                    string.IsNullOrWhiteSpace(snapshot?.TrainCode) ? null : snapshot.TrainCode,
                    scheduledStop.ExpectedAt, estimated, (long)Math.Round((estimated - now).TotalSeconds),
                    source, quality, evidence, true, null, stationRole,
                    stationRole == PapeisEstacaoEvento.Origin
                        ? ModosProximosVeiculos.Departures : ModosProximosVeiculos.Arrivals));
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

    internal static string ResolveStationRole(int stopSequence, int lastStopSequence) =>
        stopSequence == 1 ? PapeisEstacaoEvento.Origin
        : stopSequence == lastStopSequence ? PapeisEstacaoEvento.Destination
        : PapeisEstacaoEvento.Intermediate;

    internal static bool ShouldExposeRailEvent(string stationRole) =>
        stationRole != PapeisEstacaoEvento.Destination;

    private static bool IsRail(string modal) => modal.Contains("trem", StringComparison.OrdinalIgnoreCase)
        || modal.Contains("ferro", StringComparison.OrdinalIgnoreCase);

    internal sealed record StopContext(Guid Id, Guid ParadaId, int Ordem, double PosicaoTracado,
        double DistanciaAcumuladaMetros, Guid PadraoVersaoId, double ComprimentoMetros,
        Guid PadraoOperacionalId, Guid SentidoId, Guid LinhaId, string CodigoLinha, string Modal, string TipoRota);
}
