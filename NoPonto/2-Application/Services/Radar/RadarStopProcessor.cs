using Microsoft.Extensions.Options;
using NoPonto.Application.DTOs.EstruturaV2;
using NoPonto.Application.EventosParada;

namespace NoPonto.Application.Radar;

/// <summary>Um escopo/DbContext por parada. Não paralelizar operações deste escopo.</summary>
public sealed class RadarStopProcessor(IRadarMonitorStore store, IRadarCandidateStore candidates,
    IEventosParadaService events, IEstruturaLeituraV2Repository catalog,
    RadarSelector selector, IOptions<RadarOptions> options, TimeProvider clock) : IRadarStopProcessor
{
    public async Task<RadarStopResult> ProcessarAsync(Guid parada, int limiteSessoes, CancellationToken ct)
    {
        var limits = options.Value;
        if (!limits.Enabled || !limits.WorkerEnabled) return new(0, 0, 0, 0, 0);
        var sessions = await store.SessoesAsync(parada, limiteSessoes, ct);
        var accepted = new List<RadarSessaoResponse>();
        var versions = new HashSet<Guid>();
        foreach (var s in sessions)
        {
            if (s.ParadaId != parada || s.Estado != "ativa" || s.ExpiraEm <= clock.GetUtcNow()
                || s.Contextos.Length == 0 || s.Contextos.Length > limits.MaxContextos
                || s.LinhasSelecionadas.Length == 0 || s.LinhasSelecionadas.Length > limits.MaxLinhas
                || s.LinhasPrioritarias.Except(s.LinhasSelecionadas).Any()) continue;
            var added = s.Contextos.Select(c => c.PadraoVersaoId).Except(versions).ToArray();
            if (versions.Count + added.Length > limits.MaxContextosPorParada) continue;
            versions.UnionWith(added);
            accepted.Add(s);
        }
        if (accepted.Count == 0) return new(sessions.Count, 0, 0, 0, sessions.Count);
        var queriedAt = clock.GetUtcNow();
        // Mesmo serviço interno do GET /paradas/{id}/eventos, com todos os contextos da parada.
        var snapshot = await events.ListarAsync(parada, queriedAt, ct);
        if (snapshot is null) return new(sessions.Count, 1, 0, 0, sessions.Count);
        // Truncar a lista poderia esconder a única linha prioritária ou selecionada.
        // Em saturação, não emitir uma decisão parcial: aplicar backpressure e manter apenas o TTL anterior.
        if (snapshot.Count > limits.MaxEventosPorParada) return new(sessions.Count, 1, 0, 0, sessions.Count, true);
        var stop = await catalog.BuscarParadaAsync(parada, ct);
        if (stop is null) return new(sessions.Count, 1, 1, 0, sessions.Count);
        var valid = new HashSet<RadarContexto>();
        foreach (var id in versions)
        {
            ct.ThrowIfCancellationRequested();
            var route = await catalog.BuscarItinerarioAsync(id, ct);
            // Conservar somente o vínculo, não geometria/itinerários potencialmente grandes.
            if (route is { EhVersaoAtual: true } && route.PadraoVersaoId == id
                && route.Ocorrencias.Any(p => p.ParadaId == parada))
                valid.Add(new(route.LinhaId, route.SentidoId, route.PadraoOperacionalId, route.PadraoVersaoId));
        }
        var produced = 0;
        foreach (var s in accepted)
        {
            ct.ThrowIfCancellationRequested();
            if (!options.Value.Enabled || !options.Value.WorkerEnabled) break;
            if (s.Contextos.Any(c => !valid.Contains(c)) || !s.Contextos.Select(c => c.LinhaId).ToHashSet().SetEquals(s.LinhasSelecionadas)) continue;
            var content = selector.Selecionar(s, stop.Nome, snapshot, queriedAt, clock.GetUtcNow(), limits.CandidateTtlSeconds);
            // O script revalida proprietário, instalação, estado, identidade, prazo e revisão.
            if (await candidates.TentarGravarAsync(content, ct)) produced++;
        }
        return new(sessions.Count, 1, versions.Count + 1, produced, sessions.Count - produced);
    }
}
