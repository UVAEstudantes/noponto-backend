namespace NoPonto.Application.GPS;

/// <summary>
/// Configurações do ciclo de polling GPS.
/// Todas as propriedades são lidas da seção "GpsPolling" do appsettings / .env.
/// </summary>
public sealed class GpsPollingOptions
{
    public const string Secao = "GpsPolling";

    // ── Ciclo ─────────────────────────────────────────────────────────────────

    /// <summary>Intervalo entre cada ciclo de polling em segundos. Padrão: 20.</summary>
    public int IntervaloSegundos { get; set; } = 20;

    // ── TTL duplo ─────────────────────────────────────────────────────────────

    /// <summary>
    /// TTL da chave "ativa" do veículo em segundos.
    /// Um veículo é considerado SemSinal quando esta chave expira mas a chave
    /// "recente" ainda existe. Padrão: 40s (≈ 2 ciclos).
    /// </summary>
    public int TtlAtivoSegundos { get; set; } = 40;

    /// <summary>
    /// TTL da chave "recente" do veículo em segundos.
    /// Enquanto esta chave existir, o veículo aparece no mapa (status SemSinal).
    /// Padrão: 180s (≈ 9 ciclos / 3 minutos).
    /// </summary>
    public int TtlRecenteSegundos { get; set; } = 180;

    /// <summary>
    /// TTL dos sets de linha no Redis em segundos.
    /// Deve ser ≥ TtlRecenteSegundos. Padrão: 180s.
    /// </summary>
    public int TtlLinhaSegundos { get; set; } = 180;

    // ── Velocidade ────────────────────────────────────────────────────────────

    /// <summary>
    /// Velocidade máxima válida em km/h.
    /// Leituras acima deste valor são descartadas como espúrias antes de entrar
    /// na janela de média. Ônibus urbanos raramente passam de 80 km/h.
    /// Padrão: 90 km/h.
    /// </summary>
    public double VelocidadeMaximaKmh { get; set; } = 90;

    /// <summary>
    /// Quantidade de leituras anteriores usadas para calcular a velocidade média.
    /// Valores maiores suavizam mais mas reagem mais devagar a mudanças reais.
    /// Padrão: 3 leituras (≈ 60s de janela).
    /// </summary>
    public int JanelaVelocidadeLeituras { get; set; } = 3;

    // ── Itinerário ────────────────────────────────────────────────────────────

    /// <summary>
    /// Distância máxima em metros entre o veículo e a rota para que o
    /// ST_LineLocatePoint seja considerado válido. Veículos fora desta faixa
    /// provavelmente estão em outra via e não devem ser projetados na rota.
    /// Padrão: 150m.
    /// </summary>
    public double DistanciaMaximaRotaMetros { get; set; } = 150;

    // ── Retrocompatibilidade ──────────────────────────────────────────────────

    /// <summary>
    /// Propriedade legada mantida para não quebrar configs existentes.
    /// Quando definida, sobrescreve TtlAtivoSegundos.
    /// </summary>
    public int TtlSegundos
    {
        get => TtlAtivoSegundos;
        set => TtlAtivoSegundos = value;
    }
}