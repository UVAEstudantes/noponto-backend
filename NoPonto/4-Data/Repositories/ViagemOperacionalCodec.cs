using System.Globalization;
using NoPonto.Application.GPS;

namespace NoPonto.Data.Repositories;

internal static class ViagemOperacionalCodec
{
    internal static readonly string[] Names = ["ViagemId", "OrdemVeiculo", "PadraoVersaoId",
        "TimestampObservacaoInicial", "TimestampUltimaAtualizacao", "PosicaoNaRotaConfirmada",
        "UltimaOcorrenciaParadaPadraoId", "UltimaParadaOrdem", "CodigoLinha", "LinhaId", "SentidoId",
        "EstadoViagem", "ConfirmacoesPosTerminal", "TimestampFim", "CandidatoPadraoVersaoId",
        "CandidatoSentidoId", "CandidatoTimestamp", "CandidatoPosicao", "CandidatoLinhaId",
        "CandidatoLatitudeInicial", "CandidatoLongitudeInicial", "PadraoOperacionalId",
        "OcorrenciaCursorId", "OrdemCursor", "Volta", "ProgressoAbsolutoMetros", "Topologia"];
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    internal static string Tick(DateTimeOffset t) => t.UtcTicks.ToString("D19", Culture);

    internal static string[] Encode(ViagemOperacionalState state)
    {
        var s = state.Observada;
        var c = state.Candidato;
        return [s.ViagemId.ToString("N"), s.OrdemVeiculo, s.PadraoVersaoId.ToString("N"),
            Tick(s.TimestampObservacaoInicial), Tick(s.TimestampUltimaAtualizacao), s.PosicaoNaRotaConfirmada.ToString("R", Culture),
            s.UltimaOcorrenciaParadaPadraoId == Guid.Empty ? "" : s.UltimaOcorrenciaParadaPadraoId.ToString("N"),
            s.UltimaParadaOrdem.ToString(Culture), state.CodigoLinha, state.LinhaId.ToString("N"),
            state.SentidoId.ToString("N"), state.Estado.ToString(), state.ConfirmacoesPosTerminal.ToString(Culture),
            state.TimestampFim is { } fim ? Tick(fim) : "", c?.PadraoVersaoId.ToString("N") ?? "",
            c?.SentidoId.ToString("N") ?? "", c is null ? "" : Tick(c.Timestamp),
            c?.Posicao.ToString("R", Culture) ?? "", c?.LinhaId.ToString("N") ?? "",
            c?.LatitudeInicial?.ToString("R", Culture) ?? "", c?.LongitudeInicial?.ToString("R", Culture) ?? "",
            s.PadraoOperacionalId.ToString("N"), s.OcorrenciaCursorId == Guid.Empty ? "" : s.OcorrenciaCursorId.ToString("N"),
            s.OrdemCursor?.ToString(Culture) ?? "", s.Volta.ToString(Culture),
            s.ProgressoAbsolutoMetros.ToString("R", Culture), s.Topologia];
    }

    internal static ViagemObservadaState Observada(IReadOnlyDictionary<string, string> values, string ordem)
    {
        if (values.Count != Names.Length || values.Keys.Any(k => !Names.Contains(k)))
            throw new FormatException("Hash operacional não corresponde ao contrato V2.");
        string V(int i) => values[Names[i]];
        var initial = Time(V(3));
        var last = Time(V(4));
        if (V(1) != ordem || initial > last) throw new FormatException("Snapshot incompatível.");
        var occurrence = OptionalId(V(22));
        int? occurrenceOrder = V(23) == "" ? null : Int(V(23));
        if ((occurrence == Guid.Empty) != (occurrenceOrder is null)) throw new FormatException("Cursor estrutural inválido.");
        var lastOccurrence = OptionalId(V(6));
        var lastOrder = Int(V(7));
        if ((lastOccurrence == Guid.Empty) != (lastOrder == 0)) throw new FormatException("Última ocorrência inválida.");
        var topology = V(26);
        if (topology is not ("LINEAR" or "CIRCULAR")) throw new FormatException("Topologia inválida.");
        return new(Id(V(0)), ordem, Id(V(2)), initial, last, Progress(V(5)), lastOccurrence,
            lastOrder, Id(V(21)), occurrence, occurrenceOrder, Int(V(24)), NonNegative(V(25)), topology);
    }

    internal static ViagemOperacionalState Decode(IReadOnlyDictionary<string, string> values, string ordem)
    {
        if (values.Count == Names.Length + 2 && values.ContainsKey(ViagemOperacionalRedisScript.DurableVersion)
            && values.ContainsKey(ViagemOperacionalRedisScript.DurableCheckpoint))
            values = Names.ToDictionary(n => n, n => values[n]);
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
            candidate = new(Id(V(14)), Id(V(15)), Id(V(18)), Time(V(16)), Progress(V(17)),
                Coordinate(V(19), -90, 90), Coordinate(V(20), -180, 180));
            if (phase != EstadoViagem.Finalizada || candidate.Timestamp > obs.TimestampUltimaAtualizacao)
                throw new FormatException("Candidato inconsistente.");
        }
        else if (V(19) != "" || V(20) != "") throw new FormatException("Coordenada de candidato órfã.");
        return new(obs, V(8), Id(V(9)), Id(V(10)), phase, count, end, candidate);
    }

    private static Guid Id(string v) => Guid.TryParseExact(v, "N", out var id) && id != Guid.Empty ? id : throw new FormatException("GUID inválido.");
    private static Guid OptionalId(string v) => v == "" ? Guid.Empty : Id(v);
    private static int Int(string v) => int.TryParse(v, NumberStyles.None, Culture, out var n) && n >= 0 ? n : throw new FormatException("Inteiro inválido.");
    private static double Progress(string v) => double.TryParse(v, NumberStyles.Float, Culture, out var p) && double.IsFinite(p) && p is >= 0 and <= 1 ? p : throw new FormatException("Progresso inválido.");
    private static double NonNegative(string v) => double.TryParse(v, NumberStyles.Float, Culture, out var p) && double.IsFinite(p) && p >= 0 ? p : throw new FormatException("Distância inválida.");
    private static double? Coordinate(string v, double min, double max) => v == "" ? null : double.TryParse(v, NumberStyles.Float, Culture, out var x) && double.IsFinite(x) && x >= min && x <= max ? x : throw new FormatException("Coordenada inválida.");
    private static DateTimeOffset Time(string v) => long.TryParse(v, NumberStyles.None, Culture, out var ticks) && ticks >= DateTimeOffset.UnixEpoch.UtcTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : throw new FormatException("Timestamp inválido.");
}
