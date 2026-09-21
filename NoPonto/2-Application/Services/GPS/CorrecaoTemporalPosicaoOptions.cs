namespace NoPonto.Application.GPS;

/// <summary>
/// Configuração experimental do candidato de correção temporal. O horizonte de
/// 120 segundos representa apenas a fronteira da evidência offline atual.
/// </summary>
public sealed class CorrecaoTemporalPosicaoOptions
{
    public const string Secao = "PositionCorrection";
    public const string PoliticaReferencia = "B3_ADAPTATIVO_v1";

    public bool Enabled { get; set; } = false;
    public string PolicyVersion { get; set; } = PoliticaReferencia;
    public double MaxProjectionAgeSeconds { get; set; } = 120;
    public double CausalWindowSeconds { get; set; } = 180;
    public int MaxCausalSamples { get; set; } = 32;
    public double AdaptiveB1LimitSeconds { get; set; } = 15;
    public double StopSpeedKmh { get; set; } = 3;
    public double StopDisplacementMeters { get; set; } = 10;
    public int StopConfirmationObservations { get; set; } = 2;
    public double FutureTimestampToleranceSeconds { get; set; } = 5;
    public double MaxSpeedKmh { get; set; } = 90;
    public double RouteLengthRelativeTolerance { get; set; } = 1e-6;
    public double RouteLengthAbsoluteTolerance { get; set; } = 0.01;

    public bool Valida() =>
        string.Equals(PolicyVersion, PoliticaReferencia, StringComparison.Ordinal)
        && double.IsFinite(MaxProjectionAgeSeconds) && MaxProjectionAgeSeconds >= 0
        && double.IsFinite(CausalWindowSeconds) && CausalWindowSeconds > 0
        && MaxCausalSamples > 0
        && double.IsFinite(AdaptiveB1LimitSeconds) && AdaptiveB1LimitSeconds >= 0
        && AdaptiveB1LimitSeconds <= MaxProjectionAgeSeconds
        && double.IsFinite(StopSpeedKmh) && StopSpeedKmh >= 0
        && double.IsFinite(StopDisplacementMeters) && StopDisplacementMeters >= 0
        && StopConfirmationObservations > 0
        && StopConfirmationObservations <= MaxCausalSamples
        && double.IsFinite(FutureTimestampToleranceSeconds) && FutureTimestampToleranceSeconds >= 0
        && double.IsFinite(MaxSpeedKmh) && MaxSpeedKmh > 0
        && StopSpeedKmh <= MaxSpeedKmh
        && double.IsFinite(RouteLengthRelativeTolerance) && RouteLengthRelativeTolerance >= 0
        && double.IsFinite(RouteLengthAbsoluteTolerance) && RouteLengthAbsoluteTolerance >= 0;
}
