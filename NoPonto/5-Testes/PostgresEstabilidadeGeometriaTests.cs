using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NoPonto.Data.Configuration;
using NoPonto.Data.Repositories;
using NoPonto.Application.GPS;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class PostgresEstabilidadeGeometriaTests : IClassFixture<PostgisGpsFixture>
{
    private readonly PostgisGpsFixture _fixture;
    public PostgresEstabilidadeGeometriaTests(PostgisGpsFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task EfDataSourceCompartilhado_PointLineString_PostgisReal_PoolPequeno()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")!)
            { SearchPath = _fixture.Schema + ",public", MaxPoolSize = 4 };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AdicionarPostgresCompartilhado(connection.ConnectionString);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var source = provider.GetRequiredService<NpgsqlDataSource>();
        var matching = await new GpsItinerarioRepository(source, NullLogger<GpsItinerarioRepository>.Instance)
            .BuscarEnriquecimentoDoItinerarioAsync("GPS23", _fixture.R1, -22.9, -43.2, 90, 250);
        Assert.Equal(StatusBuscaItinerario.Found, matching.Status);
        Assert.Equal(.5, matching.Rota!.PosicaoNaRota, 6);
        var point = new Point(-43.2, -22.9) { SRID = 4326 };
        var line = new LineString([new Coordinate(-43.2, -22.9), new Coordinate(-43.19, -22.89)]) { SRID = 4326 };
        var stop = Guid.NewGuid();
        var itinerary = Guid.NewGuid();
        await using (var command = source.CreateCommand("""
            INSERT INTO "Paradas" ("Id", "Nome", "Localizacao") VALUES ($1, 'NTS321', $2);
            """))
        {
            command.Parameters.AddWithValue(stop);
            command.Parameters.AddWithValue(point);
            await command.ExecuteNonQueryAsync();
        }
        await using (var command = source.CreateCommand("""
            INSERT INTO "Itinerarios" ("Id", "SentidoId", "Geometria")
            SELECT $1, "SentidoId", $2 FROM "Itinerarios" LIMIT 1;
            """))
        {
            command.Parameters.AddWithValue(itinerary);
            command.Parameters.AddWithValue(line);
            await command.ExecuteNonQueryAsync();
        }
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
            var readPoint = await context.Paradas.Where(p => p.Id == stop).Select(p => p.Localizacao).SingleAsync();
            var readLine = await context.Itinerarios.Where(i => i.Id == itinerary).Select(i => i.Geometria).SingleAsync();
            Assert.True(point.EqualsExact(readPoint));
            Assert.True(line.EqualsExact(readLine));
            Assert.Equal(4326, readPoint.SRID);
            Assert.Equal(4326, readLine.SRID);
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Paradas\" SET \"Localizacao\" = {point} WHERE \"Id\" = {stop}");
        }
        // 120 consultas pelo mesmo pool de quatro conexões, sem ampliar orçamento do servidor.
        await Task.WhenAll(Enumerable.Range(0, 120).Select(async _ =>
        {
            await using var command = source.CreateCommand("SELECT pg_sleep(0.01)");
            await command.ExecuteNonQueryAsync();
        }));
    }
}
