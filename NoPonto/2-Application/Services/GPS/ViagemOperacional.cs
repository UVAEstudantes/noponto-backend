using System.Globalization;
using System.Text.Json.Serialization;

namespace NoPonto.Application.GPS;

public enum EstadoViagem { Ativa, PossivelFim, Finalizada }

public sealed record CandidatoViagem(Guid ItinerarioId, Guid SentidoId, Guid LinhaId,
    DateTimeOffset Timestamp, double Posicao, double? LatitudeInicial = null,
    double? LongitudeInicial = null);

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

internal enum MotivoPersistenciaViagem { Nenhum, Semantica, Checkpoint }

internal static class PersistenciaViagemOperacional
{
    internal static MotivoPersistenciaViagem DevePersistirDuravelmente(
        ViagemOperacionalState? anterior, ViagemOperacionalState atual,
        IReadOnlyList<EventoViagem> eventos, DateTimeOffset? ultimoCheckpointUtc,
        DateTimeOffset agoraUtc, TimeSpan intervaloCheckpoint)
    {
        if (anterior is null || eventos.Count != 0 || MudouSemanticamente(anterior, atual))
            return MotivoPersistenciaViagem.Semantica;
        var decorrido = ultimoCheckpointUtc is null
            ? TimeSpan.Zero
            : agoraUtc.ToUniversalTime() - ultimoCheckpointUtc.Value.ToUniversalTime();
        if (intervaloCheckpoint <= TimeSpan.Zero || ultimoCheckpointUtc is null
            || decorrido < TimeSpan.Zero || decorrido >= intervaloCheckpoint)
            return MotivoPersistenciaViagem.Checkpoint;
        return MotivoPersistenciaViagem.Nenhum;
    }

    private static bool MudouSemanticamente(ViagemOperacionalState anterior, ViagemOperacionalState atual) =>
        anterior.Observada.ViagemId != atual.Observada.ViagemId
        || anterior.Observada.ItinerarioId != atual.Observada.ItinerarioId
        || anterior.Observada.UltimaParadaItinerarioId != atual.Observada.UltimaParadaItinerarioId
        || anterior.Observada.UltimaParadaOrdem != atual.Observada.UltimaParadaOrdem
        || anterior.CodigoLinha != atual.CodigoLinha
        || anterior.LinhaId != atual.LinhaId
        || anterior.SentidoId != atual.SentidoId
        || anterior.Estado != atual.Estado
        || anterior.ConfirmacoesPosTerminal != atual.ConfirmacoesPosTerminal
        || anterior.TimestampFim != atual.TimestampFim
        || anterior.Candidato != atual.Candidato;
}

/// <summary>Decisão pura para itinerários não circulares; nunca reinicia cursor por regressão.</summary>
public static class ViagemOperacionalRegra
{
    // Mesma semantica do deslocamento historicamente considerado confiavel para
    // bearing: tolerancia espacial contra jitter GPS, nao regra temporal de negocio.
    internal const double MovimentoMinimoMetros = 10.0;
    internal const double JanelaCandidatoSegundos = 180.0;

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
            && (estrutura.LinhaId != anterior.LinhaId || estrutura.ItinerarioId != obs.ItinerarioId
                || estrutura.SentidoId != anterior.SentidoId))
            return new(anterior with {
                Observada = obs with { TimestampUltimaAtualizacao = gps.TimestampGps },
                Candidato = null }, eventos);
        if (anterior.Estado == EstadoViagem.Finalizada)
            return DecidirAposFinalizada(anterior, estrutura, gps, transicao, novaId);
        if (estrutura.LinhaId != anterior.LinhaId)
            return new(anterior with { Observada = obs with { TimestampUltimaAtualizacao = gps.TimestampGps }, Candidato = null }, eventos);
        if (estrutura.ItinerarioId != obs.ItinerarioId)
        {
            return new(anterior with {
                Observada = obs with { TimestampUltimaAtualizacao = gps.TimestampGps },
                Candidato = null }, eventos);
        }
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

    private static DecisaoViagem DecidirAposFinalizada(ViagemOperacionalState anterior,
        EstruturaViagem estrutura, PosicaoVeiculoDto gps, TransicaoParadas transicao, Guid novaId)
    {
        var obs = anterior.Observada;
        var semInicio = anterior with {
            Observada = obs with { TimestampUltimaAtualizacao = gps.TimestampGps }
        };
        var candidato = anterior.Candidato;
        var compativel = candidato is not null
            && candidato.ItinerarioId == estrutura.ItinerarioId
            && candidato.SentidoId == estrutura.SentidoId
            && candidato.LinhaId == estrutura.LinhaId;
        var dentroDaJanela = compativel
            && candidato!.LatitudeInicial.HasValue
            && candidato.LongitudeInicial.HasValue
            && (gps.TimestampGps - candidato!.Timestamp).TotalSeconds <= JanelaCandidatoSegundos;

        if (!estrutura.SentidoInequivoco)
            return new(semInicio with { Candidato = null }, []);

        if (dentroDaJanela && EvidenciaInicioSuficiente(candidato!, gps))
        {
            var inicio = new ViagemOperacionalState(new(novaId, gps.Ordem, estrutura.ItinerarioId,
                gps.TimestampGps, gps.TimestampGps, gps.PosicaoNaRota!.Value,
                transicao.UltimaId, transicao.UltimaOrdem),
                estrutura.CodigoLinha, estrutura.LinhaId, estrutura.SentidoId);
            return new(inicio, [Evento(inicio, "ViagemIniciada", gps.TimestampGps)]);
        }

        if (dentroDaJanela)
            return new(semInicio, []); // Mantem a primeira evidencia para acumular deslocamento.

        if (!GpsLeituraValidator.CoordenadaValida(gps.Latitude, gps.Longitude))
            return new(semInicio with { Candidato = null }, []);

        return new(semInicio with { Candidato = new(estrutura.ItinerarioId,
            estrutura.SentidoId, estrutura.LinhaId, gps.TimestampGps,
            gps.PosicaoNaRota!.Value, gps.Latitude, gps.Longitude) }, []);
    }

    private static bool EvidenciaInicioSuficiente(CandidatoViagem candidato, PosicaoVeiculoDto gps)
    {
        if (candidato.LatitudeInicial is not { } latitudeInicial
            || candidato.LongitudeInicial is not { } longitudeInicial
            || !GpsLeituraValidator.CoordenadaValida(latitudeInicial, longitudeInicial)
            || !GpsLeituraValidator.CoordenadaValida(gps.Latitude, gps.Longitude)
            || gps.ComprimentoRotaMetros is not { } comprimento
            || !double.IsFinite(comprimento) || comprimento <= 0
            || gps.PosicaoNaRota is not { } posicaoAtual)
            return false;

        var deslocamento = HaversineMetros(latitudeInicial, longitudeInicial,
            gps.Latitude, gps.Longitude);
        var progressoMetros = (posicaoAtual - candidato.Posicao) * comprimento;
        return deslocamento >= MovimentoMinimoMetros
            && progressoMetros >= MovimentoMinimoMetros;
    }

    private static double HaversineMetros(double lat1, double lon1, double lat2, double lon2)
    {
        const double raioTerraMetros = 6_371_000;
        static double Rad(double graus) => graus * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2))
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return raioTerraMetros * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
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
