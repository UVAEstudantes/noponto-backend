namespace NoPonto.Application.GPS;

public sealed class GpsPollingOptions
{
    public const string Secao = "GpsPolling";

    public int    IntervaloSegundos              { get; set; } = 20;
    public int    TtlAtivoSegundos               { get; set; } = 40;
    public int    TtlRecenteSegundos             { get; set; } = 180;
    public int    TtlLinhaSegundos               { get; set; } = 180;
    public double VelocidadeMaximaKmh            { get; set; } = 90;
    public int    JanelaVelocidadeLeituras        { get; set; } = 3;
    public int    JanelaRetroativaSegundos        { get; set; } = 60;
    public double DistanciaMaximaRotaMetros       { get; set; } = 250;
    public int    GrauParalelismoEnriquecimento   { get; set; } = 20;
    public int    MaxIdadeGpsSegundos             { get; set; } = 300;

    /// <summary>
    /// Velocidade mínima em km/h para considerar o bearing do veículo confiável.
    /// </summary>
    public double VelocidadeMinimaBearingKmh { get; set; } = 3;

    /// <summary>
    /// Número de ciclos consecutivos sem encontrar uma rota antes de descartar
    /// o último itinerário confirmado.
    /// </summary>
    public int MaxCiclosSemRota { get; set; } = 3;

    public int TtlSegundos
    {
        get => TtlAtivoSegundos;
        set => TtlAtivoSegundos = value;
    }

    /// <summary>
    /// Habilita a coleta de histórico de passagens para ML.
    /// </summary>
    public bool HistoricoHabilitado { get; set; } = true;

    /// <summary>
    /// Se true, enriquece todos os veículos via PostGIS independente de
    /// terem assinantes no SignalR. Aumenta carga no banco mas coleta
    /// histórico de todas as linhas para o ML.
    /// </summary>
    public bool EnriquecerTodasLinhas { get; set; } = false;
}