using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class PositionCorrectionShadowPostgresTests
{
    [Fact]
    public async Task Migration_batch_idempotency_rollback_and_concurrent_ordered_batches()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("POSTGIS_TEST_CONNECTION must point to disposable PostGIS.");
        var schema = "shadowc_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = "public" };
        await using var admin = NpgsqlDataSource.Create(builder.ConnectionString);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA \"{schema}\""))
            await create.ExecuteNonQueryAsync();
        try
        {
            builder.SearchPath = $"{schema},public";
            await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var db = new TransporteDbContext(new DbContextOptionsBuilder<TransporteDbContext>()
                .UseNpgsql(builder.ConnectionString, o => o.UseNetTopologySuite()).Options);
            var migrator = db.GetService<IMigrator>();
            var script = migrator.GenerateScript("20260916002042_TelemetriaMlContinua",
                "20260921144201_PositionCorrectionShadowOrigins");
            Assert.Contains("CREATE TABLE \"PositionCorrectionShadowOrigins\"", script);
            Assert.DoesNotContain("ALTER TABLE \"TelemetriasVeiculoMl\"", script);
            await using (var history = source.CreateCommand("""
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL)
                """))
                await history.ExecuteNonQueryAsync();
            await using (var command = source.CreateCommand(script))
                await command.ExecuteNonQueryAsync();
            // A migration estrutural da Etapa 1/2 adiciona estes campos no banco completo.
            // Esta fixture aplica deliberadamente apenas a migration Shadow original.
            await using (var v2 = source.CreateCommand("""
                ALTER TABLE "PositionCorrectionShadowOrigins"
                    DROP COLUMN "ItinerarioId",
                    ADD COLUMN "PadraoVersaoId" uuid NOT NULL,
                    ADD COLUMN "OcorrenciaParadaPadraoId" uuid NULL,
                    ADD COLUMN "Volta" integer NULL
                """))
                await v2.ExecuteNonQueryAsync();

            await using (var verify = source.CreateCommand("""
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = 'PositionCorrectionShadowOrigins'
                  AND column_name IN ('AmostrasCausais','SinaisParada','CandidateResults')
                  AND data_type = 'jsonb'
                """))
                Assert.Equal(3L, await verify.ExecuteScalarAsync());
            await using (var indexes = source.CreateCommand("""
                SELECT count(*) FROM pg_indexes WHERE schemaname = current_schema()
                  AND tablename = 'PositionCorrectionShadowOrigins'
                """))
                Assert.Equal(4L, await indexes.ExecuteScalarAsync());

            var repository = new PositionCorrectionShadowRepository(source,
                new PositionCorrectionShadowMetrics(), new PositionCorrectionShadowPipelineOptions());
            var now = DateTimeOffset.UtcNow;
            var a = new PositionCorrectionShadowReceipt(PositionCorrectionShadowInfrastructureTests.Origin('a'), now);
            var b = new PositionCorrectionShadowReceipt(PositionCorrectionShadowInfrastructureTests.Origin('b', 'd'), now);
            Assert.Equal(new PositionCorrectionShadowBatchResult(2, 2, 0), await repository.PersistBatchAsync([b, a], CancellationToken.None));
            Assert.Equal(new PositionCorrectionShadowBatchResult(2, 0, 2), await repository.PersistBatchAsync([a, b], CancellationToken.None));
            Assert.Equal(new PositionCorrectionShadowBatchResult(1, 1, 0), await repository.PersistBatchAsync(
                [new(PositionCorrectionShadowInfrastructureTests.Origin('c', 'e'), now)], CancellationToken.None));

            await using (var constraint = source.CreateCommand("""
                ALTER TABLE "PositionCorrectionShadowOrigins"
                ADD CONSTRAINT "shadow_test_line" CHECK ("CodigoLinha" <> 'BAD')
                """))
                await constraint.ExecuteNonQueryAsync();
            var good = new PositionCorrectionShadowReceipt(PositionCorrectionShadowInfrastructureTests.Origin('d'), now);
            var bad = new PositionCorrectionShadowReceipt(PositionCorrectionShadowInfrastructureTests.Origin('e') with
            { CausalContext = PositionCorrectionShadowInfrastructureTests.Origin('e').CausalContext with { CodigoLinha = "BAD" } }, now);
            await Assert.ThrowsAsync<PostgresException>(() => repository.PersistBatchAsync([good, bad], CancellationToken.None));
            await using (var count = source.CreateCommand("""
                SELECT count(*) FROM "PositionCorrectionShadowOrigins" WHERE "ShadowOriginId" = @id
                """))
            {
                count.Parameters.AddWithValue("id", good.Origin.ShadowOriginId);
                Assert.Equal(0L, await count.ExecuteScalarAsync());
            }

            var left = new[] { '1', '2', '3' }.Select(x => new PositionCorrectionShadowReceipt(
                PositionCorrectionShadowInfrastructureTests.Origin(x), now)).ToArray();
            var right = left.Reverse().ToArray();
            var results = await Task.WhenAll(
                repository.PersistBatchAsync(left, CancellationToken.None),
                repository.PersistBatchAsync(right, CancellationToken.None));
            Assert.Equal(3, results.Sum(x => x.Inserted));
            Assert.Equal(3, results.Sum(x => x.Duplicates));
            await using (var count = source.CreateCommand("SELECT count(*) FROM \"PositionCorrectionShadowOrigins\""))
                Assert.Equal(6L, await count.ExecuteScalarAsync());

            var down = migrator.GenerateScript("20260921144201_PositionCorrectionShadowOrigins",
                "20260916002042_TelemetriaMlContinua");
            Assert.Contains("DROP TABLE \"PositionCorrectionShadowOrigins\"", down);
            await using (var command = source.CreateCommand(down))
                await command.ExecuteNonQueryAsync();
            await using (var exists = source.CreateCommand("SELECT to_regclass('PositionCorrectionShadowOrigins') IS NULL"))
                Assert.True((bool)(await exists.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var cleanup = admin.CreateCommand($"DROP SCHEMA \"{schema}\" CASCADE");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
