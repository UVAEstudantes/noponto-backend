namespace NoPonto.Application.GPS;

public sealed class GpsHistoricoOptions
{
    public const string Secao = "GpsHistorico";

    /// <summary>
    /// Distância máxima em metros para considerar que o veículo
    /// "passou" pela parada e registrar no histórico.
    /// Padrão: 150m (próximo o suficiente para ser uma passagem real).
    /// </summary>
    public double DistanciaRegistroMetros { get; set; } = 150;

    /// <summary>
    /// Tempo máximo plausível entre duas paradas consecutivas em segundos.
    /// Acima disso considera que é uma viagem nova ou dado inválido.
    /// Padrão: 1800s (30 minutos).
    /// </summary>
    public double MaxTempoEntreParadasSegundos { get; set; } = 1800;

    /// <summary>
    /// Se false, desativa completamente a coleta de histórico.
    /// Útil para ambientes de teste onde não quer acumular dados.
    /// </summary>
    public bool Habilitado { get; set; } = true;
}