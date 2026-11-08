using Microsoft.Extensions.Options;
using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Radar;

/// <summary>Seleção temporal equivalente às três vagas do frontend, sobre previsões já calculadas.</summary>
public sealed class RadarSelector(IOptions<PhysicalEtaOptions> physical)
{
    internal const int MaxEvidenceAgeSeconds = 300; // RADAR_PHYSICAL_MAX_MS do frontend

    public RadarConteudoCandidato Selecionar(RadarSessaoResponse session, string nome,
        IReadOnlyList<EventoParadaDto> events, DateTimeOffset consultadoEm, DateTimeOffset now, int ttlSeconds)
    {
        var eligible = new List<RadarItemCandidato>();
        var validity = new Dictionary<RadarItemCandidato, DateTimeOffset>();
        if (now >= consultadoEm && now - consultadoEm <= TimeSpan.FromSeconds(MaxEvidenceAgeSeconds))
        foreach (var e in events)
        {
            if (e.ParadaId != session.ParadaId || string.IsNullOrWhiteSpace(e.EventId)
                || !session.LinhasSelecionadas.Contains(e.LinhaId)
                || !session.Contextos.Any(c => c.LinhaId == e.LinhaId && c.SentidoId == e.SentidoId
                    && c.PadraoOperacionalId == e.PadraoOperacionalId && c.PadraoVersaoId == e.PadraoVersaoId)
                || e.EventType is not (TiposEventoParada.Arrival or TiposEventoParada.Departure)) continue;
            var isPhysical = e.Source is "PhysicalCausal" or "HybridFixed20" or "HybridRecentEma";
            if (string.IsNullOrWhiteSpace(e.Source) || e.Source == "Unknown" || string.IsNullOrWhiteSpace(e.Quality)) continue;
            var maxAge = isPhysical ? Math.Min(MaxEvidenceAgeSeconds, physical.Value.MaxAgeSeconds) : MaxEvidenceAgeSeconds;
            if (maxAge < 1 || isPhysical && !physical.Value.Valid() || e.Quality is "previsao_indisponivel" or "gps_desatualizado" or "Unknown"
                or "StalePrediction" or "HistoricalFallback") continue;
            if (isPhysical && e.Quality is not ("estimativa_fisica" or "estimativa_recente" or "hipotese_nominal")) continue;
            var freshUntil = consultadoEm.AddSeconds(MaxEvidenceAgeSeconds);
            if (e.LastRealtimeEvidenceUtc is { } evidence)
            {
                if (evidence > now || now - evidence > TimeSpan.FromSeconds(maxAge)) continue;
                freshUntil = Min(freshUntil, evidence.AddSeconds(maxAge));
            }
            else if (isPhysical || e.ScheduledAt is null || e.Quality is not ("ScheduleOnly" or "ScheduleAnchored")) continue;
            // Não estimar velocidade, distância ou saída. Somente instantes/duração do contrato existente.
            DateTimeOffset? at = e.EstimatedAt ?? e.ScheduledAt;
            if (at is null && e.SecondsUntilEvent is >= 0 and <= 900)
                at = consultadoEm.AddSeconds(e.SecondsUntilEvent.Value);
            if (at is null || at < now || at - now > TimeSpan.FromMinutes(session.AntecedenciaMinutos)) continue;
            var vehicle = e.VehicleId ?? e.RailVehicleId?.ToString("D") ?? e.ExpectedRunId?.ToString("D") ?? e.CodigoVeiculo;
            var item = new RadarItemCandidato(e.EventId, e.LinhaId, e.CodigoLinha, vehicle, e.EventType, at.Value,
                (long)Math.Ceiling((at.Value - now).TotalSeconds), session.LinhasPrioritarias.Contains(e.LinhaId),
                e.Source, e.Quality, e.LastRealtimeEvidenceUtc, e.SentidoId);
            eligible.Add(item);
            validity[item] = freshUntil;
        }
        var unique = new List<RadarItemCandidato>();
        foreach (var item in eligible.OrderBy(i => i.PrevistoEm).ThenBy(i => i.EventoId, StringComparer.Ordinal))
            if (!unique.Any(i => i.EventoId == item.EventoId || item.VeiculoId is not null
                && i.LinhaId == item.LinhaId && string.Equals(i.VeiculoId?.Trim(), item.VeiculoId.Trim(), StringComparison.OrdinalIgnoreCase)))
                unique.Add(item);
        var selected = unique.Take(1).ToList();
        var priority = unique.FirstOrDefault(i => i.Prioritaria);
        if (priority is not null && !selected.Contains(priority)) selected.Add(priority);
        // Várias prioridades não podem ocupar todas as vagas quando há uma alternativa.
        if (selected.All(i => i.Prioritaria) && unique.Where(i => i.Prioritaria).Select(i => i.LinhaId).Distinct().Skip(1).Any())
        {
            var alternative = unique.FirstOrDefault(i => !i.Prioritaria);
            if (alternative is not null) selected.Add(alternative);
        }
        while (selected.Count < 3)
        {
            var next = unique.Except(selected).OrderBy(i => i.PrevistoEm)
                .ThenBy(i => selected.Any(s => s.LinhaId == i.LinhaId))
                .ThenBy(i => i.EventoId, StringComparer.Ordinal).FirstOrDefault();
            if (next is null) break;
            selected.Add(next);
        }
        var until = Min(session.ExpiraEm, now.AddSeconds(ttlSeconds));
        foreach (var item in selected) until = Min(until, Min(item.PrevistoEm, validity[item]));
        var selectedLines = selected.Select(i => i.LinhaId).ToHashSet();
        var linhasAdicionais = unique.Where(i => !selected.Contains(i)).Select(i => i.LinhaId)
            .Where(id => !selectedLines.Contains(id)).Distinct().Count();
        return new(session.SessaoId, session.InstalacaoId, session.Revisao, session.ParadaId, nome, consultadoEm, now, until,
            selected.OrderBy(i => i.PrevistoEm).ThenBy(i => i.EventoId, StringComparer.Ordinal).ToArray(),
            unique.Count - selected.Count, LinhasAdicionais: linhasAdicionais);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
