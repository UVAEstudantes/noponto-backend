using Microsoft.EntityFrameworkCore;
using NoPonto.Application.GTFS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GtfsLinhaModalTests
{
    [Theory]
    [InlineData("702", "brt")]
    [InlineData("200", "frescao")]
    [InlineData("700", "regular")]
    [InlineData("3", "regular")]
    [InlineData("", "regular")]
    [InlineData("desconhecido", "regular")]
    public void Classificacao_MantemMapeamentoExistente(string routeType, string expected)
        => Assert.Equal(expected, GtfsLinhaModal.TipoRota(routeType));

    [Theory]
    [InlineData("700")]
    [InlineData("200")]
    [InlineData("3")]
    [InlineData("")]
    [InlineData("desconhecido")]
    public async Task SemEvidencia702_NaoConsultaNemAssociaModalBrt(string routeType)
    {
        // Contexto descartado como sentinela: acessar o EF deve falhar antes de qualquer consulta.
        await using var db = new TransporteDbContext(new DbContextOptionsBuilder<TransporteDbContext>().Options);
        await db.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => db.Modais);
        Assert.Throws<ObjectDisposedException>(() => db.ChangeTracker);
        var busId = Guid.NewGuid();
        Assert.Equal(busId, await GtfsLinhaModal.ParaNovaLinhaAsync(db, routeType, busId, default));
    }
}
