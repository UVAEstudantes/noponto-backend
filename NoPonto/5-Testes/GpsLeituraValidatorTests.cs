using NoPonto.Application.GPS;
using Xunit;

public class GpsLeituraValidatorTests
{
    [Theory]
    [InlineData(-22.9, -43.2, true)]
    [InlineData(0, 0, false)]
    [InlineData(91, 0, false)]
    [InlineData(0, -181, false)]
    [InlineData(double.NaN, 0, false)]
    public void CoordenadaValida_ClassificaCorretamente(double lat, double lon, bool esperado)
        => Assert.Equal(esperado, GpsLeituraValidator.CoordenadaValida(lat, lon));

    [Fact]
    public void TimestampValido_RejeitaAusente()
        => Assert.False(GpsLeituraValidator.TimestampValido(null, DateTimeOffset.UtcNow, out _));

    [Fact]
    public void TimestampValido_RejeitaEpoch()
        => Assert.False(GpsLeituraValidator.TimestampValido(
            DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, out _));

    [Fact]
    public void TimestampValido_AceitaAgora()
        => Assert.True(GpsLeituraValidator.TimestampValido(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, out _));

    [Fact]
    public void TimestampValido_AceitaPassadoRecente()
        => Assert.True(GpsLeituraValidator.TimestampValido(
            DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow, out _));

    [Fact]
    public void TimestampValido_RejeitaFuturoAlemDaTolerancia()
        => Assert.False(GpsLeituraValidator.TimestampValido(
            DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow, out _));

    [Fact]
    public void TimestampValido_AceitaFuturoDentroDaTolerancia()
        => Assert.True(GpsLeituraValidator.TimestampValido(
            DateTimeOffset.UtcNow.AddSeconds(30), DateTimeOffset.UtcNow, out _));
}