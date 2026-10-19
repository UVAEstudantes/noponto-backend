using Microsoft.Extensions.Caching.Distributed;

namespace NoPonto.Application.GPS;

// Historical evidence has a different role from visual liveness. Reuse the
// CAS-written recent payload, without promoting it to active or renewing TTLs.
internal static class GpsPhysicalPredecessor
{
    // Same maximum GPS interval already accepted by PhysicalEta. Cache retention
    // alone is insufficient: an old provider timestamp can outlive this window.
    internal const int MaxIntervalSeconds = 180;

    internal static async Task<(PosicaoVeiculoDto?[] Positions, int Reads)> ReadAsync(
        IReadOnlyList<PosicaoVeiculoDto> incoming, PosicaoVeiculoDto?[] active,
        long?[] watermarks, IDistributedCache cache, CancellationToken ct)
    {
        var predecessors = active.ToArray();
        var missing = Enumerable.Range(0, active.Length).Where(i => active[i] is null
            && watermarks[i].HasValue
            && !GpsTimestampDeduplication.ShouldIgnore(incoming[i].TimestampGps, null, watermarks[i])).ToArray();
        var payloads = await Task.WhenAll(missing.Select(i =>
            cache.GetStringAsync(GpsPollingService.ChaveVeiculoRecente(incoming[i].Ordem), ct)));
        for (var j = 0; j < missing.Length; j++)
        {
            var i = missing[j];
            var previous = GpsTimestampDeduplication.ParseActive(payloads[j]);
            var current = incoming[i];
            if (previous is null || previous.Status != StatusVeiculo.Ativo
                || previous.TimestampGps <= DateTimeOffset.UnixEpoch
                || previous.TimestampGps.ToUnixTimeMilliseconds() != watermarks[i]
                || !string.Equals(previous.Ordem, current.Ordem, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(previous.CodigoLinha)
                || !string.Equals(previous.CodigoLinha, current.CodigoLinha, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previous.TipoRota, current.TipoRota, StringComparison.OrdinalIgnoreCase)
                || (current.TimestampGps - previous.TimestampGps).TotalSeconds is not (> 0 and <= MaxIntervalSeconds)
                || !GpsLeituraValidator.CoordenadaValida(previous.Latitude, previous.Longitude))
                continue;
            predecessors[i] = previous;
        }
        return (predecessors, missing.Length);
    }

    // A retained coordinate pair cannot attest route continuity by itself.
    // Bind it to the previous projection actually used by the live matcher;
    // restart, failed/missing matching and itinerary changes require warm-up.
    internal static bool Continuous(ResultadoEnriquecimentoGps result, PosicaoVeiculoDto previous)
    {
        var p = result.Posicao;
        var d = result.Diagnostico;
        return previous.LinhaId is { } line && line != Guid.Empty && line == p.LinhaId
            && previous.SentidoId is { } direction && direction != Guid.Empty && direction == p.SentidoId
            && previous.PadraoOperacionalId is { } pattern && pattern != Guid.Empty && pattern == p.PadraoOperacionalId
            && previous.PadraoVersaoId is { } version && version != Guid.Empty && version == p.PadraoVersaoId
            && previous.TopologiaPadrao == p.TopologiaPadrao
            && previous.ComprimentoRotaMetros == p.ComprimentoRotaMetros
            && d?.TimestampAnterior == previous.TimestampGps
            && d.PosicaoAnterior == previous.PosicaoNaRota
            && p.TimestampAnterior == previous.TimestampGps;
    }
}
