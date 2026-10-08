using System.Diagnostics;
using NoPonto.Data.Repositories;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class GtfsRealtimeCrosswalkPostgresTests(ITestOutputHelper output)
{
    [GtfsFixtureFact]
    public async Task SelectiveCrosswalkIsReadOnlyAndPreservesAmbiguity()
    {
        var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("GTFSRT_FIXTURE_CONNECTION"));
        Assert.Contains(cs.Host, new[] { "localhost", "127.0.0.1", "::1" });
        Assert.StartsWith("gtfsrt_fixture_", cs.Database);
        Assert.NotEqual(5432, cs.Port);
        await using var ds = NpgsqlDataSource.Create(cs.ConnectionString);
        await using (var setup = ds.CreateCommand("""
            CREATE TABLE "FontesEstruturais" ("Id" uuid PRIMARY KEY, "Codigo" text NOT NULL);
            CREATE TABLE "LineFixture" ("Id" uuid PRIMARY KEY, "Codigo" text NOT NULL, "TipoRota" text NOT NULL);
            CREATE VIEW "Linhas" AS SELECT * FROM "LineFixture"
              WHERE current_setting('transaction_read_only') = 'on';
            CREATE TABLE "LinhasIdentidadesExternas" ("ExternalId" text NOT NULL, "LinhaId" uuid REFERENCES "LineFixture", "FonteEstruturalId" uuid REFERENCES "FontesEstruturais", "Tipo" text NOT NULL);
            INSERT INTO "FontesEstruturais" VALUES ('00000000-0000-0000-0000-000000000001', 'DATARIO_GTFS');
            INSERT INTO "LineFixture" SELECT md5(i::text)::uuid,
              CASE i WHEN 1 THEN '006' WHEN 2 THEN 'SV006' ELSE 'SN' || i::text END, 'regular'
              FROM generate_series(1,200) i;
            INSERT INTO "LinhasIdentidadesExternas" SELECT 'route-' || i::text, md5(i::text)::uuid,
              '00000000-0000-0000-0000-000000000001', 'ROUTE_ID' FROM generate_series(1,200) i;
            INSERT INTO "LinhasIdentidadesExternas" VALUES ('ambiguous', md5('1')::uuid,
              '00000000-0000-0000-0000-000000000001','ROUTE_ID'), ('ambiguous', md5('2')::uuid,
              '00000000-0000-0000-0000-000000000001','ROUTE_ID');
            """)) await setup.ExecuteNonQueryAsync();
        var lookup = new GtfsRealtimeRouteLookup(ds);
        foreach (var size in new[] { 1, 10, 50, 200 })
        {
            var before = GC.GetTotalAllocatedBytes(); var watch = Stopwatch.StartNew();
            var result = await lookup.FindAsync(Enumerable.Range(1, size).Select(i => "route-" + i).ToArray(), default);
            watch.Stop();
            output.WriteLine($"batch={size} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} allocated_process_bytes={GC.GetTotalAllocatedBytes()-before}");
            Assert.Equal(size, result.Count); Assert.Contains(result, x => x.Codigo == "006");
        }
        Assert.Equal(2, (await lookup.FindAsync(["ambiguous"], default)).Count);
        Assert.Empty(await lookup.FindAsync(["unknown"], default));
    }

    private sealed class GtfsFixtureFactAttribute : FactAttribute
    {
        public GtfsFixtureFactAttribute()
        { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GTFSRT_FIXTURE_CONNECTION"))) Skip = "Requires explicit exclusive GTFSRT fixture."; }
    }
}
