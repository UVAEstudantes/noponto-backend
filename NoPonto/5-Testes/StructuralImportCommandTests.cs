using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NoPonto.Application.GTFS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed class StructuralImportCommandTests
{
    [Fact]
    public void Parser_AceitaAsQuatroFormasERejeitaArgumentosInvalidos()
    {
        Assert.True(StructuralImportCommand.TryParse(
            ["structural-import", "--gtfs", "feed.zip"], out var normal, out _));
        Assert.False(normal!.DryRun);
        Assert.Null(normal.CrosswalkPath);

        Assert.True(StructuralImportCommand.TryParse(
            ["structural-import", "--gtfs", "feed.zip", "--crosswalk", "map.json"], out var mapped, out _));
        Assert.Equal("map.json", mapped!.CrosswalkPath);

        Assert.True(StructuralImportCommand.TryParse(
            ["structural-import", "--gtfs", "feed.zip", "--dry-run"], out var dry, out _));
        Assert.True(dry!.DryRun);

        Assert.True(StructuralImportCommand.TryParse(
            ["structural-import", "--gtfs", "feed.zip", "--crosswalk", "map.json", "--dry-run"],
            out var mappedDry, out _));
        Assert.True(mappedDry!.DryRun);
        Assert.False(StructuralImportCommand.TryParse(["structural-import"], out _, out _));
        Assert.False(StructuralImportCommand.TryParse(
            ["structural-import", "--gtfs", "feed.zip", "--unknown"], out _, out _));
    }

    [Fact]
    public async Task DryRun_ValidaPlanoSemConsultarConfiguracaoPostgres()
    {
        var zip = CreateFeed();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var reads = 0;
            var exitCode = await StructuralImportCommand.ExecuteAsync(
                ["structural-import", "--gtfs", zip, "--dry-run"],
                _ => { reads++; throw new InvalidOperationException("PostgreSQL não pode ser consultado no dry-run."); },
                stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(0, reads);
            Assert.Empty(stderr.ToString());
            Assert.Contains("Routes: 1", stdout.ToString());
            Assert.Contains("BlockingErrors: 0", stdout.ToString());
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task ArquivoAusenteEGtfsInvalido_RetornamExitCodeNaoZero()
    {
        var missing = await StructuralImportCommand.ExecuteAsync(
            ["structural-import", "--gtfs", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip"), "--dry-run"],
            _ => throw new InvalidOperationException(), new StringWriter(), new StringWriter());
        Assert.NotEqual(0, missing);

        var invalid = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(invalid, "não é zip");
            var result = await StructuralImportCommand.ExecuteAsync(
                ["structural-import", "--gtfs", invalid, "--dry-run"],
                _ => throw new InvalidOperationException(), new StringWriter(), new StringWriter());
            Assert.NotEqual(0, result);
        }
        finally { File.Delete(invalid); }
    }

    [Fact]
    public async Task PostgisVazio_MigraImportaPublicaERepeteSemDuplicar_QuandoConfiguradoExplicitamente()
    {
        var connectionString = Environment.GetEnvironmentVariable("GTFS_STRUCTURAL_IMPORT_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var zip = Environment.GetEnvironmentVariable("GTFS_TEST_ZIP");
        Assert.False(string.IsNullOrWhiteSpace(zip), "GTFS_TEST_ZIP é obrigatório para o gate de integração.");

        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var environment = new Dictionary<string, string?>
        {
            ["POSTGRES_HOST"] = connection.Host,
            ["POSTGRES_PORT"] = connection.Port.ToString(),
            ["POSTGRES_DB"] = connection.Database,
            ["POSTGRES_USER"] = connection.Username,
            ["POSTGRES_PASSWORD"] = connection.Password
        };
        string? ReadEnvironment(string key) => environment.GetValueOrDefault(key);
        await using (var emptyConnection = new NpgsqlConnection(connectionString))
        {
            await emptyConnection.OpenAsync();
            await using var emptyCommand = new NpgsqlCommand(
                "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NULL", emptyConnection);
            Assert.True((bool)(await emptyCommand.ExecuteScalarAsync() ?? false),
                "O gate exige PostgreSQL/PostGIS completamente vazio.");
        }

        var firstOutput = new StringWriter();
        var firstError = new StringWriter();
        var firstExit = await StructuralImportCommand.ExecuteAsync(
            ["structural-import", "--gtfs", zip!], ReadEnvironment, firstOutput, firstError);
        Assert.True(firstExit == 0, $"exit={firstExit}; stderr={firstError}");
        Assert.Empty(firstError.ToString());
        Assert.Contains("Routes: 494", firstOutput.ToString());
        Assert.Contains("RouteDirections: 935", firstOutput.ToString());
        Assert.Contains("Patterns: 961", firstOutput.ToString());
        Assert.Contains("Occurrences: 54514", firstOutput.ToString());
        Assert.Contains("CircularPatterns: 41", firstOutput.ToString());
        Assert.Contains("BrtRoutes: 30", firstOutput.ToString());
        Assert.Contains("BrtDirections: 60", firstOutput.ToString());
        Assert.Contains("BrtPatterns: 78", firstOutput.ToString());
        Assert.Contains("BrtDistinctStops: 341", firstOutput.ToString());
        Assert.Contains("PadroesPublicados: 961", firstOutput.ToString());
        Assert.Contains("BrtPublishedPatterns: 78", firstOutput.ToString());

        var options = new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql(connectionString, x => x.UseNetTopologySuite()).Options;
        await using var db = new TransporteDbContext(options);
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Equal(db.Database.GetMigrations().ToArray(), applied.ToArray());
        Assert.Equal("20260927220700_EtaV2Foundation", applied.Last());
        var source = await db.FontesEstruturais.SingleAsync(x => x.Codigo == GtfsDatarioPlanPersister.SourceCode);
        var importId = await db.ImportacoesEstruturais.Where(x => x.FonteEstruturalId == source.Id
                && x.AlgoritmoVersao == GtfsDatarioPlanPersister.AlgorithmVersion)
            .Select(x => x.Id).SingleAsync();
        var before = await CountsAsync(db);
        Assert.Equal((494, 935, 7_694, 961, 961, 54_514, 961, 78), before);
        Assert.Equal(0, await db.OcorrenciasParadasPadroes.CountAsync(x =>
            !db.PadroesVersoes.Any(v => v.Id == x.PadraoVersaoId)
            || !db.Paradas.Any(p => p.Id == x.ParadaId)));
        Assert.Equal(0, await db.PadroesOperacionais.CountAsync(x => x.VersaoAtualId != null
            && !x.Versoes.Any(v => v.Id == x.VersaoAtualId)));

        var pointers = await db.PadroesOperacionais.OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.VersaoAtualId }).ToArrayAsync();
        var secondOutput = new StringWriter();
        var secondError = new StringWriter();
        var secondExit = await StructuralImportCommand.ExecuteAsync(
            ["structural-import", "--gtfs", zip!], ReadEnvironment, secondOutput, secondError);
        Assert.True(secondExit == 0, $"exit={secondExit}; stderr={secondError}");
        Assert.Empty(secondError.ToString());
        db.ChangeTracker.Clear();
        Assert.Equal(importId, await db.ImportacoesEstruturais.Where(x => x.FonteEstruturalId == source.Id
                && x.AlgoritmoVersao == GtfsDatarioPlanPersister.AlgorithmVersion)
            .Select(x => x.Id).SingleAsync());
        Assert.Equal(before, await CountsAsync(db));
        Assert.Equal(pointers, await db.PadroesOperacionais.OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.VersaoAtualId }).ToArrayAsync());
        Assert.Contains("Lines: created=0", secondOutput.ToString());
        Assert.Contains("Directions: created=0", secondOutput.ToString());
        Assert.Contains("Patterns: created=0", secondOutput.ToString());
        Assert.Contains("Versions: created=0", secondOutput.ToString());
        Assert.Contains("Occurrences: created=0", secondOutput.ToString());
    }

    private static async Task<(int Lines, int Directions, int Stops, int Patterns, int Versions,
        int Occurrences, int Published, int BrtPublished)> CountsAsync(TransporteDbContext db) =>
        (await db.Linhas.CountAsync(), await db.Sentidos.CountAsync(), await db.Paradas.CountAsync(),
            await db.PadroesOperacionais.CountAsync(), await db.PadroesVersoes.CountAsync(),
            await db.OcorrenciasParadasPadroes.CountAsync(),
            await db.PadroesOperacionais.CountAsync(x => x.VersaoAtualId != null),
            await db.PadroesOperacionais.CountAsync(x => x.VersaoAtualId != null
                && x.Sentido.Linha.TipoRota == "brt"));

    private static string CreateFeed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"noponto-structural-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, "routes.txt", "route_id,route_short_name,route_long_name,route_type\nR1,001,Linha 1,700\n");
        Add(archive, "trips.txt", "route_id,service_id,trip_id,direction_id,shape_id,trip_headsign\nR1,S,T1,0,SH1,Centro\n");
        Add(archive, "stop_times.txt", "trip_id,stop_id,stop_sequence,shape_dist_traveled\nT1,P1,1,0\nT1,P2,2,1000\n");
        Add(archive, "stops.txt", "stop_id,stop_name,stop_lat,stop_lon\nP1,Parada 1,-22.90,-43.20\nP2,Parada 2,-22.91,-43.21\n");
        Add(archive, "shapes.txt", "shape_id,shape_pt_sequence,shape_pt_lat,shape_pt_lon,shape_dist_traveled\nSH1,1,-22.90,-43.20,0\nSH1,2,-22.91,-43.21,1000\n");
        return path;
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
