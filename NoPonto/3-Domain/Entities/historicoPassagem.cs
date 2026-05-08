namespace NoPonto.Domain.Entities;

/// <summary>
/// Registro de uma passagem de veículo por uma parada.
/// Usado como dataset para treinar modelos de predição de ETA.
///
/// Uma passagem é registrada quando o veículo está dentro de
/// DistanciaRegistroMetros da parada e ainda não foi registrado
/// nessa parada nessa viagem.
/// </summary>
public class HistoricoPassagem : BaseEntity
{
    /// <summary>Código do veículo (campo "ordem" da API GPS).</summary>
    public string Ordem { get; set; } = null!;

    /// <summary>Código da linha operada no momento da passagem.</summary>
    public string CodigoLinha { get; set; } = null!;

    public Guid ItinerarioId { get; set; }
    public Guid ParadaId     { get; set; }

    // ── Posição e tempo ───────────────────────────────────────────────────────

    /// <summary>Posição na rota no momento do registro (0.0 → 1.0).</summary>
    public double PosicaoNaRota { get; set; }

    /// <summary>Distância real ao centro da parada em metros.</summary>
    public double DistanciaParadaMetros { get; set; }

    /// <summary>Timestamp GPS do veículo no momento da passagem.</summary>
    public DateTimeOffset TimestampGps { get; set; }

    /// <summary>Timestamp do servidor quando o registro foi inserido.</summary>
    public DateTimeOffset TimestampRegistro { get; set; }

    // ── Features para ML ─────────────────────────────────────────────────────

    /// <summary>Velocidade instantânea reportada pela API (km/h).</summary>
    public double VelocidadeInstantanea { get; set; }

    /// <summary>
    /// Velocidade média da janela deslizante no momento da passagem (km/h).
    /// Null se ainda não havia leituras suficientes.
    /// </summary>
    public double? VelocidadeMedia { get; set; }

    /// <summary>Hora do dia (0-23) — feature de período do dia.</summary>
    public int HoraDia { get; set; }

    /// <summary>Dia da semana (0=Dom, 1=Seg, …, 6=Sáb).</summary>
    public int DiaSemana { get; set; }

    /// <summary>
    /// Tempo em segundos entre esta passagem e a passagem anterior
    /// do mesmo veículo na parada anterior do mesmo itinerário.
    /// Null na primeira parada da viagem ou quando não há registro anterior.
    /// Esse é o target principal do modelo de ETA entre paradas.
    /// </summary>
    public double? TempoDesdeParadaAnteriorSegundos { get; set; }

    /// <summary>
    /// Distância em metros entre esta parada e a parada anterior
    /// no itinerário (comprimento do trecho).
    /// Null na primeira parada.
    /// </summary>
    public double? DistanciaTrechoMetros { get; set; }

    // ── Navegação ─────────────────────────────────────────────────────────────

    public Itinerario Itinerario { get; set; } = null!;
    public Parada     Parada     { get; set; } = null!;
}