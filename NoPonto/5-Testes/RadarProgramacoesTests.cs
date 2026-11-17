using NoPonto.Application.Radar;
using Xunit;

namespace NoPonto.Tests;

public sealed class RadarProgramacoesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-16T00:00:00Z");
    private static RadarAgendaHorario Weekly(int hour, int minute = 0, DayOfWeek day = DayOfWeek.Monday,
        string zone = "America/Sao_Paulo") => new(zone, new(hour, minute), null, [day]);

    [Fact]
    public void RecorrenciaPreservaHoraLocalEPulaOcorrenciaJaExecutada()
    {
        var agenda = Weekly(8);
        var first = agenda.Proxima(Now);
        Assert.Equal(DateTimeOffset.Parse("2026-11-16T11:00:00Z"), first);
        Assert.Equal(DateTimeOffset.Parse("2026-11-23T11:00:00Z"), agenda.Proxima(first!.Value));
    }

    [Fact]
    public void DataUnicaNaoRepete()
    {
        var agenda = new RadarAgendaHorario("America/Sao_Paulo", new(8, 0), new(2026, 11, 16), []);
        Assert.Equal(DateTimeOffset.Parse("2026-11-16T11:00:00Z"), agenda.Proxima(Now));
        Assert.Null(agenda.Proxima(DateTimeOffset.Parse("2026-11-16T11:00:00Z")));
    }

    [Theory]
    [InlineData("2027-03-14", "02:30", null)]
    [InlineData("2027-11-07", "01:30", "2027-11-07T05:30:00Z")]
    public void HorarioDeVeraoPulaInexistenteEEscolhePrimeiraOcorrencia(string day, string hour, string? expected)
    {
        var agenda = new RadarAgendaHorario("America/New_York", TimeOnly.Parse(hour), DateOnly.Parse(day), []);
        Assert.Equal(expected is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(expected), agenda.Proxima(Now));
    }

    [Fact]
    public void CalendarioRejeitaFusoEDiasInvalidos()
    {
        Assert.Equal(400, Assert.Throws<RadarException>(() => Weekly(8, zone: "inexistente").Proxima(Now)).Status);
        Assert.Throws<RadarException>(() => (Weekly(8) with { Dias = [] }).Proxima(Now));
        Assert.Throws<RadarException>(() => (Weekly(8) with { Dias = [(DayOfWeek)7] }).Proxima(Now));
        Assert.Throws<RadarException>(() => (Weekly(8) with { Dias = [DayOfWeek.Monday, DayOfWeek.Monday] }).Proxima(Now));
    }

    [Theory]
    [InlineData(8, 59, true)]
    [InlineData(9, 0, false)]
    [InlineData(7, 1, true)]
    [InlineData(7, 0, false)]
    public void SobreposicaoRespeitaJanelaAbertaDeUmaHora(int hour, int minute, bool overlaps)
        => Assert.Equal(overlaps, RadarAgendaConflitos.Sobrepoe(Weekly(8), Weekly(hour, minute), Now));

    [Fact]
    public void SobreposicaoCruzaMeiaNoiteESemana()
    {
        Assert.True(RadarAgendaConflitos.Sobrepoe(Weekly(23, 45, DayOfWeek.Sunday), Weekly(0, 15), Now));
        Assert.False(RadarAgendaConflitos.Sobrepoe(Weekly(23, 45, DayOfWeek.Sunday), Weekly(0, 45), Now));
    }

    [Fact]
    public void DataUnicaComparaInstantesEntreFusos()
    {
        var unica = new RadarAgendaHorario("Etc/UTC", new(11, 30), new(2026, 11, 16), []);
        Assert.True(RadarAgendaConflitos.Sobrepoe(unica, Weekly(8), Now));
        Assert.False(RadarAgendaConflitos.Sobrepoe(unica with { Hora = new(12, 0) }, Weekly(8), Now));
        Assert.False(RadarAgendaConflitos.Sobrepoe(unica, Weekly(8, day: DayOfWeek.Tuesday), Now));
    }

    [Fact]
    public void IdentidadeDaOcorrenciaEstavelEIndependenteDoOffset()
    {
        var programacao = Guid.NewGuid();
        var first = RadarProgramacoesExecutor.SessaoId(programacao, Now);
        Assert.Equal(first, RadarProgramacoesExecutor.SessaoId(programacao, Now.ToOffset(TimeSpan.FromHours(-3))));
        Assert.NotEqual(first, RadarProgramacoesExecutor.SessaoId(programacao, Now.AddDays(7)));
        Assert.NotEqual(first, RadarProgramacoesExecutor.SessaoId(Guid.NewGuid(), Now));
    }
}
