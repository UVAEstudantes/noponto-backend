namespace NoPonto.Application.GPS;

public sealed class GpsSppoCollectorOptions
{
    public const string Secao = "GpsSppoCollector";

    public int TimeoutSegundos { get; set; } = 90;
    public int JanelaInicialSegundos { get; set; } = 20;
    public int OverlapSegundos { get; set; } = 10;
    public int CatchupChunkSegundos { get; set; } = 60;
    public int MaxLagRecuperavelSegundos { get; set; } = 300;
    public int IntervaloEntreColetasSegundos { get; set; } = 10;
}
