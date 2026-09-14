using System.Globalization;
using System.Text.Json.Serialization;

namespace NoPonto.Application.GPS;

public enum EstadoViagem { Ativa, PossivelFim, Finalizada }

public sealed record CandidatoViagem(Guid ItinerarioId, Guid SentidoId, Guid LinhaId,
    DateTimeOffset Timestamp, double Posicao);

public sealed record ViagemOperacionalState(ViagemObservadaState Observada, string CodigoLinha,
    Guid LinhaId, Guid SentidoId, EstadoViagem Estado = EstadoViagem.Ativa,
    int ConfirmacoesPosTerminal = 0, DateTimeOffset? TimestampFim = null,
    CandidatoViagem? Candidato = null);

public sealed record EstruturaViagem(Guid ItinerarioId, Guid LinhaId, Guid SentidoId,
    string CodigoLinha, bool SentidoInequivoco);

public sealed record EventoViagem(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("tipo")] string Tipo,
    [property: JsonPropertyName("viagem_id")] Guid ViagemId,
    [property: JsonPropertyName("ordem_veiculo")] string OrdemVeiculo,
    [property: JsonPropertyName("codigo_linha")] string CodigoLinha,
    [property: JsonPropertyName("sentido_id")] Guid SentidoId,
    [property: JsonPropertyName("itinerario_id")] Guid ItinerarioId,
    [property: JsonPropertyName("timestamp_evento")] DateTimeOffset TimestampEvento,
    [property: JsonPropertyName("parada_itinerario_id")] Guid? ParadaItinerarioId = null,
    [property: JsonPropertyName("parada_id")] Guid? ParadaId = null,
    [property: JsonPropertyName("ordem")] int? Ordem = null,
    [property: JsonPropertyName("posicao_linha")] double? PosicaoLinha = null,
    [property: JsonPropertyName("timestamp_passagem")] DateTimeOffset? TimestampPassagem = null,
    [property: JsonPropertyName("timestamp_gps")] DateTimeOffset? TimestampGps = null,
    [property: JsonPropertyName("velocidade_instantanea")] double? VelocidadeInstantanea = null,
    [property: JsonPropertyName("velocidade_media")] double? VelocidadeMedia = null);

public sealed record DecisaoViagem(ViagemOperacionalState Estado, IReadOnlyList<EventoViagem> Eventos);

/// <summary>Decisão pura para itinerários não circulares; nunca reinicia cursor por regressão.</summary>
public static class ViagemOperacionalRegra
{
    public static DecisaoViagem Decidir(ViagemOperacionalState? anterior, EstruturaViagem estrutura,
        PosicaoVeiculoDto gps, TransicaoParadas transicao, Guid novaId, bool adocaoLegado = false)
    {
        if (transicao.Status != ViagemObservadaStatus.Updated || gps.ItinerarioId != estrutura.ItinerarioId
            || gps.PosicaoNaRota is not { } p || !double.IsFinite(p) || p is < 0 or > 1)
            throw new InvalidOperationException("Matching/estrutura inválidos.");
        var eventos = new List<EventoViagem>();
        if (anterior is null)
        {
            var inicial = new ViagemOperacionalState(new(novaId, gps.Ordem, estrutura.ItinerarioId,
                gps.TimestampGps, gps.TimestampGps, p, transicao.UltimaId, transicao.UltimaOrdem),
                estrutura.CodigoLinha, estrutura.LinhaId, estrutura.SentidoId);
            eventos.Add(Evento(inicial, "ViagemIniciada", gps.TimestampGps));
            return new(inicial, eventos);
        }
        var obs = anterior.Observada;
        if (gps.TimestampGps <= obs.TimestampUltimaAtualizacao)
            throw new InvalidOperationException("Timestamp não crescente.");
        if (anterior.Estado == EstadoViagem.PossivelFim
            && (estrutura.LinhaId != anterior.LinhaId || estrutura.ItinerarioId != obs.ItinerarioId))
            return new(anterior with { Estado = EstadoViagem.Ativa, ConfirmacoesPosTerminal = 0,
                Observada = obs with { TimestampUltimaAtualizacao = gps.TimestampGps }, Candidato = null }, eventos);
        if (estrutura.LinhaId != anterior.LinhaId)
            return new(anterior with { Observada = obs with { TimestampUltimaAtualizacao = gps.TimestampGps }, Candidato = null }, eventos);
        if (estrutura.ItinerarioId != obs.ItinerarioId)
        {
            if (anterior.Estado == EstadoViagem.Ativa)
                return new(anterior with { Observada = obs with { TimestampUltimaAtualizacao = gps.TimestampGps }, Candidato = null }, eventos);
            var final = anterior with { Observada = obs with { TimestampUltimaAtualizacao = gps.TimestampGps } };
            if (!estrutura.SentidoInequivoco || estrutura.SentidoId == anterior.SentidoId)
                return new(final with { Candidato = null }, eventos);
            if (anterior.Candidato is { } candidato && candidato.ItinerarioId == estrutura.ItinerarioId
                && candidato.SentidoId == estrutura.SentidoId && candidato.LinhaId == estrutura.LinhaId
                && gps.TimestampGps > candidato.Timestamp
                && (gps.TimestampGps - candidato.Timestamp).TotalSeconds <= 180)
            {
                var inicio = new ViagemOperacionalState(new(novaId, gps.Ordem, estrutura.ItinerarioId,
                    gps.TimestampGps, gps.TimestampGps, p, transicao.UltimaId, transicao.UltimaOrdem),
                    anterior.CodigoLinha, anterior.LinhaId, estrutura.SentidoId);
                eventos.Add(Evento(inicio, "ViagemIniciada", gps.TimestampGps));
                return new(inicio, eventos);
            }
            return new(final with { Candidato = new(estrutura.ItinerarioId, estrutura.SentidoId,
                estrutura.LinhaId, gps.TimestampGps, p) }, eventos);
        }
        if (anterior.Estado == EstadoViagem.Finalizada)
            return new(anterior with { Observada = obs with { TimestampUltimaAtualizacao = gps.TimestampGps }, Candidato = null }, eventos);
        var atual = anterior with { Observada = obs with { TimestampUltimaAtualizacao = gps.TimestampGps,
            PosicaoNaRotaConfirmada = p, UltimaParadaItinerarioId = transicao.UltimaId,
            UltimaParadaOrdem = transicao.UltimaOrdem }, Candidato = null };
        if (!adocaoLegado)
            foreach (var parada in transicao.Ultrapassadas.OrderBy(o => o.Ordem).ThenBy(o => o.Id))
            {
                var passagem = TimestampPassagem(obs, gps, parada.PosicaoLinha);
                eventos.Add(Evento(atual, "PassagemParada", passagem) with {
                    EventId = $"passagem:{obs.ViagemId:D}:{parada.Id:D}", ParadaItinerarioId = parada.Id,
                    ParadaId = parada.ParadaId, Ordem = parada.Ordem, PosicaoLinha = parada.PosicaoLinha,
                    TimestampPassagem = passagem, TimestampGps = gps.TimestampGps.ToUniversalTime(),
                    VelocidadeInstantanea = gps.Velocidade, VelocidadeMedia = gps.VelocidadeMedia });
            }
        var permaneceTerminal = transicao.Terminal is { } ultima
            && transicao.UltimaId == ultima.Id && transicao.Proxima is null && transicao.Ultrapassadas.Count == 0;
        if (anterior.Estado == EstadoViagem.Ativa && !adocaoLegado && transicao.Terminal is { } terminal
            && (transicao.Ultrapassadas.Any(o => o.Id == terminal.Id) || permaneceTerminal))
            atual = atual with { Estado = EstadoViagem.PossivelFim, ConfirmacoesPosTerminal = 0 };
        else if (anterior.Estado == EstadoViagem.PossivelFim && permaneceTerminal)
        {
            var contador = anterior.ConfirmacoesPosTerminal + 1;
            atual = atual with { ConfirmacoesPosTerminal = contador };
            if (contador == 2)
            {
                atual = atual with { Estado = EstadoViagem.Finalizada, TimestampFim = gps.TimestampGps };
                eventos.Add(Evento(atual, "ViagemFinalizada", gps.TimestampGps));
            }
        }
        else if (anterior.Estado == EstadoViagem.PossivelFim)
            atual = atual with { Estado = EstadoViagem.Ativa, ConfirmacoesPosTerminal = 0 };
        return new(atual, eventos);
    }

    public static DateTimeOffset TimestampPassagem(ViagemObservadaState anterior, PosicaoVeiculoDto gps, double parada)
    {
        var p = gps.PosicaoNaRota;
        var segundos = (gps.TimestampGps - anterior.TimestampUltimaAtualizacao).TotalSeconds;
        if (gps.ItinerarioId != anterior.ItinerarioId || p is null || !double.IsFinite(p.Value)
            || !double.IsFinite(anterior.PosicaoNaRotaConfirmada) || !double.IsFinite(parada)
            || segundos <= 0 || segundos > 180 || p <= anterior.PosicaoNaRotaConfirmada
            || parada < anterior.PosicaoNaRotaConfirmada || parada > p)
            return gps.TimestampGps;
        var fracao = (parada - anterior.PosicaoNaRotaConfirmada) / (p.Value - anterior.PosicaoNaRotaConfirmada);
        var ticks = (long)Math.Round(fracao * (gps.TimestampGps.UtcTicks - anterior.TimestampUltimaAtualizacao.UtcTicks));
        return anterior.TimestampUltimaAtualizacao.AddTicks(ticks);
    }

    private static EventoViagem Evento(ViagemOperacionalState state, string tipo, DateTimeOffset ts) => new(
        $"{(tipo == "ViagemIniciada" ? "inicio" : tipo == "ViagemFinalizada" ? "fim" : "passagem")}:{state.Observada.ViagemId:D}",
        tipo, state.Observada.ViagemId, state.Observada.OrdemVeiculo, state.CodigoLinha,
        state.SentidoId, state.Observada.ItinerarioId, ts.ToUniversalTime());
}
