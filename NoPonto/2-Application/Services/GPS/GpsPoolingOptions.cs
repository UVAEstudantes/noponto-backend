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
    /// Abaixo disso o veículo é tratado como parado: bearing é herdado do ciclo
    /// anterior e não é usado para trocar de itinerário.
    /// Padrão: 3 km/h (captura GPS lento, não "zero puro").
    /// </summary>
    public double VelocidadeMinimaBearingKmh { get; set; } = 3;

    /// <summary>
    /// Número de ciclos consecutivos sem encontrar uma rota antes de descartar
    /// o último itinerário confirmado. Útil para terminais onde o veículo fica
    /// parado e às vezes sai da geometria da rota.
    /// Padrão: 3 ciclos (~60s).
    /// </summary>
    public int MaxCiclosSemRota { get; set; } = 3;

    public int TtlSegundos
    {
        get => TtlAtivoSegundos;
        set => TtlAtivoSegundos = value;
    }
}