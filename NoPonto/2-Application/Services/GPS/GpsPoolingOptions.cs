namespace NoPonto.Application.GPS;

public sealed class GpsPollingOptions
{
    public const string Secao = "GpsPolling";

    public int    IntervaloSegundos              { get; set; } = 20;
    public int    IntervaloBrtSegundos           { get; set; } = 20;
    public int    TtlAtivoSegundos               { get; set; } = 40;
    public int    TtlRecenteSegundos             { get; set; } = 180;
    public int    TtlLinhaSegundos               { get; set; } = 180;
    public double VelocidadeMaximaKmh            { get; set; } = 90;
    /// <summary>Margem de instabilidade da projeção, não deslocamento do veículo.</summary>
    public double ToleranciaProjecaoMetros       { get; set; } = 50;
    public int    JanelaVelocidadeLeituras        { get; set; } = 3;
    public double DistanciaMaximaRotaMetros       { get; set; } = 250;
    public int    GrauParalelismoEnriquecimento   { get; set; } = 20;
    public int    GrauParalelismoViagemObservada  { get; set; } = 20;
    public int    MaxIdadeGpsSegundos             { get; set; } = 300;
    /// <summary>
    /// Intervalo máximo entre snapshots duráveis da viagem. Valor menor ou igual a zero
    /// ativa o modo conservador (persiste toda posição elegível).
    /// </summary>
    public int    CheckpointViagemSegundos        { get; set; } = 60;

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

/// <summary>
/// Feature flag isolada do ambiente. Somente o literal booleano "true" habilita
/// o matching em lote; valor ausente, inválido ou false mantém o fluxo individual.
/// </summary>
public sealed class GpsMatchingBatchOptions
{
    public bool Enabled { get; init; }
    internal int TamanhoChunk { get; init; } = 100;

    public static GpsMatchingBatchOptions FromConfiguration(string? value) =>
        new() { Enabled = bool.TryParse(value, out var enabled) && enabled };
}
