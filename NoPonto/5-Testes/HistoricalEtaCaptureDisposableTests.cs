using Microsoft.Extensions.DependencyInjection;
using NoPonto.Data.Configuration;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

// Intentionally NOT in .ci/offline-tests.json. Invoked only by the guarded D8 runner.
public sealed class HistoricalEtaCaptureDisposableTests
{
    [Fact]
    public async Task D8_PrepareSchemaExplicitly()
    {
        var lab = HistoricalEtaCaptureLab.FromEnvironment();
        if (Environment.GetEnvironmentVariable("NOPONTO_SHADOW_FIXTURE_D8_PREPARE") != lab.Guard)
            throw new InvalidOperationException("D8 schema preparation requires separate explicit authorization.");
        using var services = new ServiceCollection().AdicionarPostgresCompartilhado(lab.Postgres).BuildServiceProvider();
        var source = services.GetRequiredService<NpgsqlDataSource>();
        using var redis = await ConnectionMultiplexer.ConnectAsync(lab.Redis);
        await lab.VerifyOwnership(source, redis);
        await using var c = await source.OpenConnectionAsync();
        var count = await new NpgsqlCommand("""
            SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE'
              AND table_name NOT IN ('spatial_ref_sys','noponto_shadow_fixture_guard')
            """, c).ExecuteScalarAsync();
        Assert.Equal(0L, count);
        await using (var command = new NpgsqlCommand(HistoricalEtaCaptureLab.GenerateSchema(), c) { CommandTimeout = 120 })
            await command.ExecuteNonQueryAsync();
        using var scope = services.CreateScope();
        await HistoricalEtaCaptureLab.Seed(scope.ServiceProvider.GetRequiredService<TransporteDbContext>());
        await using var marker = new NpgsqlCommand("CREATE TABLE noponto_d8_ready(token text NOT NULL); INSERT INTO noponto_d8_ready VALUES (@token)", c);
        marker.Parameters.AddWithValue("token", lab.Guard);
        await marker.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task D8_CaptureThroughActualGpsCasAndShadow()
    {
        var lab = HistoricalEtaCaptureLab.FromEnvironment();
        if (Environment.GetEnvironmentVariable("NOPONTO_SHADOW_FIXTURE_D8_PREPARE") is not null)
            throw new InvalidOperationException("D8 capture cannot authorize schema preparation.");
        using var services = new ServiceCollection().AdicionarPostgresCompartilhado(lab.Postgres).BuildServiceProvider();
        var source = services.GetRequiredService<NpgsqlDataSource>();
        using var redis = await ConnectionMultiplexer.ConnectAsync(lab.Redis);
        await lab.VerifyOwnership(source, redis);
        await using var command = source.CreateCommand("SELECT token FROM noponto_d8_ready");
        Assert.Equal(lab.Guard, await command.ExecuteScalarAsync());
        await HistoricalEtaGtfsPollingTests.RunD8Capture(lab, source, redis);
    }
}
