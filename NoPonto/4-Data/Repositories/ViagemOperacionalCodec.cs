using System.Globalization;
using NoPonto.Application.GPS;

namespace NoPonto.Data.Repositories;

internal static class ViagemOperacionalCodec
{
    internal static readonly string[] Names = ["ViagemId", "OrdemVeiculo", "ItinerarioId",
        "TimestampObservacaoInicial", "TimestampUltimaAtualizacao", "PosicaoNaRotaConfirmada",
        "UltimaParadaItinerarioId", "UltimaParadaOrdem", "CodigoLinha", "LinhaId", "SentidoId",
        "EstadoViagem", "ConfirmacoesPosTerminal", "TimestampFim", "CandidatoItinerarioId",
        "CandidatoSentidoId", "CandidatoTimestamp", "CandidatoPosicao", "CandidatoLinhaId",
        "CandidatoLatitudeInicial", "CandidatoLongitudeInicial"];
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    internal static string Tick(DateTimeOffset t) => t.UtcTicks.ToString("D19", Culture);
    internal static string[] Encode(ViagemOperacionalState state)
    {
        var s = state.Observada;
        var c = state.Candidato;
        return [s.ViagemId.ToString("N"), s.OrdemVeiculo, s.ItinerarioId.ToString("N"),
            Tick(s.TimestampObservacaoInicial), Tick(s.TimestampUltimaAtualizacao), s.PosicaoNaRotaConfirmada.ToString("R", Culture),
            s.UltimaParadaItinerarioId.ToString("N"), s.UltimaParadaOrdem.ToString(Culture), state.CodigoLinha,
            state.LinhaId.ToString("N"), state.SentidoId.ToString("N"), state.Estado.ToString(),
            state.ConfirmacoesPosTerminal.ToString(Culture), state.TimestampFim is { } fim ? Tick(fim) : "",
            c?.ItinerarioId.ToString("N") ?? "", c?.SentidoId.ToString("N") ?? "",
            c is null ? "" : Tick(c.Timestamp), c?.Posicao.ToString("R", Culture) ?? "", c?.LinhaId.ToString("N") ?? "",
            c?.LatitudeInicial?.ToString("R", Culture) ?? "",
            c?.LongitudeInicial?.ToString("R", Culture) ?? ""];
    }
    internal static ViagemObservadaState Observada(IReadOnlyDictionary<string, string> values, string ordem)
    {
        if (values.Count is not (6 or 8 or 19 or 21) || values.Keys.Any(k => !Names.Contains(k)))
            throw new FormatException("Hash parcial ou versão desconhecida.");
        string V(int i) => values[Names[i]];
        var initial = Time(V(3));
        var last = Time(V(4));
        if (V(1) != ordem || initial > last) throw new FormatException("Snapshot incompatível.");
        var id = values.Count == 6 || V(6) == "" ? Guid.Empty : Guid.ParseExact(V(6), "N");
        var order = values.Count == 6 || V(7) == "" ? 0 : Int(V(7));
        if ((order == 0) != (id == Guid.Empty)) throw new FormatException("Cursor inválido.");
        return new(Id(V(0)), ordem, Id(V(2)), initial, last, Progress(V(5)), id, order);
    }
    internal static ViagemOperacionalState Decode(IReadOnlyDictionary<string, string> values, string ordem)
    {
        if (values.Count == 23 && values.ContainsKey(ViagemOperacionalRedisScript.DurableVersion)
            && values.ContainsKey(ViagemOperacionalRedisScript.DurableCheckpoint))
            values = Names.ToDictionary(n => n, n => values[n]);
        if (values.Count is not (19 or 21)) throw new FormatException("Hash operacional parcial.");
        var obs = Observada(values, ordem);
        string V(int i) => values[Names[i]];
        if (string.IsNullOrWhiteSpace(V(8)) || !Enum.TryParse<EstadoViagem>(V(11), out var phase)
            || phase.ToString() != V(11)) throw new FormatException("Estado inválido.");
        var count = Int(V(12));
        DateTimeOffset? end = V(13) == "" ? null : Time(V(13));
        if (count > 2 || (phase == EstadoViagem.Ativa && count != 0)
            || (phase == EstadoViagem.PossivelFim && count > 1)
            || (phase == EstadoViagem.Finalizada) != end.HasValue
            || (end is not null && (end < obs.TimestampObservacaoInicial || end > obs.TimestampUltimaAtualizacao))
            || (phase != EstadoViagem.Ativa && obs.UltimaParadaOrdem == 0))
            throw new FormatException("Máquina inconsistente.");
        CandidatoViagem? candidate = null;
        if (Enumerable.Range(14, 5).Any(i => V(i) != ""))
        {
            double? latitude = values.Count == 21 ? Coordinate(V(19), -90, 90) : null;
            double? longitude = values.Count == 21 ? Coordinate(V(20), -180, 180) : null;
            candidate = new(Id(V(14)), Id(V(15)), Id(V(18)), Time(V(16)), Progress(V(17)),
                latitude, longitude);
            if (phase != EstadoViagem.Finalizada || candidate.Timestamp > obs.TimestampUltimaAtualizacao)
                throw new FormatException("Candidato inconsistente.");
        }
        else if (values.Count == 21 && (V(19) != "" || V(20) != ""))
            throw new FormatException("Coordenada de candidato órfã.");
        return new(obs, V(8), Id(V(9)), Id(V(10)), phase, count, end, candidate);
    }
    private static Guid Id(string v) => Guid.TryParseExact(v, "N", out var id) && id != Guid.Empty
        ? id : throw new FormatException("GUID inválido.");
    private static int Int(string v) => int.TryParse(v, NumberStyles.None, Culture, out var n) && n >= 0
        ? n : throw new FormatException("Inteiro inválido.");
    private static double Progress(string v) => double.TryParse(v, NumberStyles.Float, Culture, out var p)
        && double.IsFinite(p) && p is >= 0 and <= 1 ? p : throw new FormatException("Progresso inválido.");
    private static double Coordinate(string v, double min, double max) =>
        double.TryParse(v, NumberStyles.Float, Culture, out var coordinate)
        && double.IsFinite(coordinate) && coordinate >= min && coordinate <= max
            ? coordinate : throw new FormatException("Coordenada inválida.");
    private static DateTimeOffset Time(string v)
    {
        if (v.Length != 19 || !long.TryParse(v, NumberStyles.None, Culture, out var t)) throw new FormatException("Timestamp inválido.");
        var result = new DateTimeOffset(t, TimeSpan.Zero);
        if (!GpsLeituraValidator.TimestampValido(result, DateTimeOffset.UtcNow, out _)) throw new FormatException("Timestamp inválido.");
        return result;
    }
}
