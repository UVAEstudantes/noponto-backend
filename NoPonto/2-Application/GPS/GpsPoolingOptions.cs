namespace NoPonto.Application.GPS;

public sealed class GpsPollingOptions
{
    public const string Secao = "GpsPolling";

    public int IntervaloSegundos      { get; set; } = 20;
    public int TtlAtivoSegundos       { get; set; } = 40;
    public int TtlRecenteSegundos     { get; set; } = 180;
    public int TtlLinhaSegundos       { get; set; } = 180;
    public double VelocidadeMaximaKmh  { get; set; } = 90;
    public int JanelaVelocidadeLeituras { get; set; } = 3;
    public int JanelaRetroativaSegundos { get; set; } = 60;
    public double DistanciaMaximaRotaMetros { get; set; } = 250;
    public int GrauParalelismoEnriquecimento { get; set; } = 20;

    /// <summary>
    /// Idade máxima do timestamp GPS (datahora) para aceitar a posição.
    /// Posições mais antigas que isso são descartadas como dados acumulados
    /// de veículos que ficaram sem sinal (a central às vezes despeja horas
    /// de posições de uma vez quando o ônibus reconecta).
    /// Padrão: 300s (5 minutos)
    /// </summary>
    public int MaxIdadeGpsSegundos { get; set; } = 300;

    public int TtlSegundos
    {
        get => TtlAtivoSegundos;
        set => TtlAtivoSegundos = value;
    }
}