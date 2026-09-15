namespace NoPonto.Application.GPS;

internal enum StatusFonteGps
{
    Sucesso,
    Vazio,
    Falha,
}

internal sealed record ResultadoFonteGps(
    StatusFonteGps Status,
    IReadOnlyList<PosicaoVeiculoDto> Posicoes,
    TimeSpan Duracao,
    string? MotivoFalha = null,
    DateTimeOffset? WatermarkFonte = null)
{
    public static ResultadoFonteGps Sucesso(
        IReadOnlyList<PosicaoVeiculoDto> posicoes,
        TimeSpan duracao,
        DateTimeOffset? watermarkFonte = null)
        => new(StatusFonteGps.Sucesso, posicoes, duracao, WatermarkFonte: watermarkFonte);

    public static ResultadoFonteGps Vazio(TimeSpan duracao)
        => new(StatusFonteGps.Vazio, [], duracao);

    public static ResultadoFonteGps Falha(string motivo, TimeSpan duracao)
        => new(StatusFonteGps.Falha, [], duracao, motivo);
}
