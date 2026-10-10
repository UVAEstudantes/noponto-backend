using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NoPonto.Application.GTFS;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class GtfsDatarioPersistenceIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public GtfsDatarioPersistenceIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PostgresDescartavel_RerunIdempotente_QuandoConfiguradoExplicitamente()
    {
        var connection = Environment.GetEnvironmentVariable("GTFS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return; // gate seguro: jamais usa configuração da aplicação

        var options = new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql(connection, x => x.UseNetTopologySuite()).Options;
        await using var db = new TransporteDbContext(options);
        await db.Database.MigrateAsync();

        var brtModalId = Guid.Parse("b47b0000-0000-4000-8000-000000000001");
        if (!await db.Modais.AnyAsync(x => x.Nome == "BRT"))
        {
            db.Modais.Add(new Modal { Id = brtModalId, Nome = "BRT" });
            await db.SaveChangesAsync();
        }

        var zip = Environment.GetEnvironmentVariable("GTFS_TEST_ZIP");
        Assert.False(string.IsNullOrWhiteSpace(zip), "GTFS_TEST_ZIP é obrigatório quando GTFS_TEST_CONNECTION existe.");
        var service = new GtfsDatarioImportService(new GtfsFeedParser(), new GtfsDatarioPlanPersister(db));
        var watch = Stopwatch.StartNew();
        var first = await service.ExecuteAsync(new(zip!, GtfsDatarioMode.Persist));
        watch.Stop(); var firstDuration = watch.Elapsed;
        db.ChangeTracker.Clear();

        Assert.Equal(494, first.Feed.Routes.Count);
        Assert.Equal(935, first.Feed.Trips.Select(x => (x.RouteId, x.DirectionId)).Distinct().Count());
        Assert.Equal(961, first.Patterns.Count);
        Assert.Equal(54_514, first.Patterns.Sum(x => x.Occurrences.Count));
        Assert.Equal(41, first.Patterns.Count(x => x.Circular));
        Assert.Equal(24, first.Patterns.GroupBy(x => (x.RouteId, x.DirectionId)).Count(x => x.Count() > 1));

        var source = await db.FontesEstruturais.SingleAsync(x => x.Codigo == GtfsDatarioPlanPersister.SourceCode);
        Assert.Equal(494, await db.LinhasIdentidadesExternas.CountAsync(x =>
            x.FonteEstruturalId == source.Id && x.Tipo == "ROUTE_ID"));
        Assert.Equal(935, await db.SentidosIdentidadesExternas.CountAsync(x =>
            x.FonteEstruturalId == source.Id && x.Tipo == "GTFS_DIRECTION"));
        var patternIdentities = await db.PadroesIdentidadesExternas
            .Where(x => x.FonteEstruturalId == source.Id && x.Tipo == "STRUCTURAL_KEY").ToArrayAsync();
        Assert.Equal(961, patternIdentities.Length);
        var shapeIdentities = await db.PadroesIdentidadesExternas
            .Where(x => x.FonteEstruturalId == source.Id && x.Tipo == "SHAPE_ID").ToArrayAsync();
        Assert.Equal(961, shapeIdentities.Length);
        Assert.Equal(961, shapeIdentities.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).Count());
        var patternByStructuralKey = patternIdentities.ToDictionary(
            x => x.ExternalId, x => x.PadraoOperacionalId, StringComparer.OrdinalIgnoreCase);
        Assert.All(first.Patterns, item => Assert.Equal(
            patternByStructuralKey[item.StructuralKey],
            shapeIdentities.Single(x => x.ExternalId == item.ShapeId).PadraoOperacionalId));
        var patternIds = patternIdentities.Select(x => x.PadraoOperacionalId).ToArray();
        var versions = await db.PadroesVersoes.Where(x => patternIds.Contains(x.PadraoOperacionalId)).ToArrayAsync();
        Assert.Equal(961, versions.Length);
        Assert.Equal(41, versions.Count(x => x.Topologia == "CIRCULAR"));
        Assert.All(versions, x => { Assert.Equal(GtfsDatarioPlanPersister.AlgorithmVersion, x.AlgoritmoVersao); Assert.False(string.IsNullOrWhiteSpace(x.HashEstrutural)); });
        var versionIds = versions.Select(x => x.Id).ToArray();
        Assert.Equal(54_514, await db.OcorrenciasParadasPadroes.CountAsync(x => versionIds.Contains(x.PadraoVersaoId)));

        var brtRoutes = first.Feed.Routes.Where(x => x.RouteType == "702").Select(x => x.RouteId).ToHashSet();
        var brtPatterns = first.Patterns.Where(x => brtRoutes.Contains(x.RouteId)).ToArray();
        Assert.Equal(30, brtRoutes.Count);
        var brtLines = await db.LinhasIdentidadesExternas.Include(x => x.Linha)
            .Where(x => x.FonteEstruturalId == source.Id && x.Tipo == "ROUTE_ID"
                && brtRoutes.Contains(x.ExternalId)).ToArrayAsync();
        Assert.All(brtLines, x => { Assert.Equal(brtModalId, x.Linha.ModalId); Assert.Equal("brt", x.Linha.TipoRota); });
        var classificationBefore = await db.Linhas.OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.ModalId, x.TipoRota }).ToArrayAsync();
        Assert.Equal(60, brtPatterns.Select(x => (x.RouteId, x.DirectionId)).Distinct().Count());
        Assert.Equal(78, brtPatterns.Length);
        Assert.Equal(341, brtPatterns.SelectMany(x => x.Occurrences).Select(x => x.StopId).Distinct().Count());

        var route006 = first.Feed.Routes.Single(x => x.RouteShortName == "006").RouteId;
        var patterns006 = first.Patterns.Where(x => x.RouteId == route006).ToArray();
        Assert.Equal(31, patterns006.Single(x => x.DirectionId == "0").Occurrences.Count);
        Assert.Equal(36, patterns006.Single(x => x.DirectionId == "1").Occurrences.Count);
        var shared006 = new[] { "1014O00110C0", "1014O00043C0", "1014O00080C0" };
        foreach (var code in shared006)
        {
            Assert.Contains(patterns006.Single(x => x.DirectionId == "0").Occurrences, x => x.StopId == code);
            Assert.Contains(patterns006.Single(x => x.DirectionId == "1").Occurrences, x => x.StopId == code);
        }
        Assert.Equal(new[] { 9, 10, 11 }, shared006.Select(code => patterns006.Single(x => x.DirectionId == "0").Occurrences.Single(x => x.StopId == code).StopSequence));
        Assert.Equal(new[] { 29, 28, 27 }, shared006.Select(code => patterns006.Single(x => x.DirectionId == "1").Occurrences.Single(x => x.StopId == code).StopSequence));

        var parents = first.Feed.Stops.Where(x => x.LocationType == "1").ToArray();
        Assert.Equal(201, parents.Length);
        var stopMap = await db.Paradas.Where(x => first.Feed.Stops.Select(y => y.StopId).Contains(x.Codigo))
            .ToDictionaryAsync(x => x.Codigo, StringComparer.OrdinalIgnoreCase);
        var parentIds = parents.Select(x => stopMap[x.StopId].Id).ToArray();
        Assert.Equal(0, await db.OcorrenciasParadasPadroes.CountAsync(x => parentIds.Contains(x.ParadaId)));
        foreach (var child in first.Feed.Stops.Where(x => x.ParentStation.Length > 0))
            Assert.Equal(stopMap[child.ParentStation].Id, stopMap[child.StopId].ParadaPaiId);
        foreach (var platform in first.Feed.Stops.Where(x => x.PlatformCode.Length > 0))
            Assert.Equal(platform.PlatformCode, stopMap[platform.StopId].Plataforma);

        var before = new { Lines = await db.Linhas.CountAsync(), Directions = await db.Sentidos.CountAsync(),
            Patterns = await db.PadroesOperacionais.CountAsync(), Versions = await db.PadroesVersoes.CountAsync(),
            Stops = await db.Paradas.CountAsync(), Occurrences = await db.OcorrenciasParadasPadroes.CountAsync() };
        watch.Restart();
        _ = await service.ExecuteAsync(new(zip!, GtfsDatarioMode.Persist));
        watch.Stop(); var secondDuration = watch.Elapsed;
        db.ChangeTracker.Clear();
        var after = new { Lines = await db.Linhas.CountAsync(), Directions = await db.Sentidos.CountAsync(),
            Patterns = await db.PadroesOperacionais.CountAsync(), Versions = await db.PadroesVersoes.CountAsync(),
            Stops = await db.Paradas.CountAsync(), Occurrences = await db.OcorrenciasParadasPadroes.CountAsync() };
        Assert.Equal(before, after);
        Assert.Equal(classificationBefore, await db.Linhas.OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.ModalId, x.TipoRota }).ToArrayAsync());
        Assert.Equal(961, await db.PadroesIdentidadesExternas.CountAsync(x =>
            x.FonteEstruturalId == source.Id && x.Tipo == "STRUCTURAL_KEY"));
        Assert.Equal(961, await db.PadroesIdentidadesExternas.CountAsync(x =>
            x.FonteEstruturalId == source.Id && x.Tipo == "SHAPE_ID"));
        Assert.All(await db.PadroesOperacionais.Where(x => patternIds.Contains(x.Id)).ToArrayAsync(), x => Assert.Null(x.VersaoAtualId));
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            routes = 494, directions = 935, patterns = 961, occurrences = 54_514,
            circular_patterns = 41, multi_pattern_directions = 24,
            brt = new { routes = 30, directions = 60, patterns = 78, stops = 341 },
            second_run_created = new { lines = 0, directions = 0, patterns = 0, versions = 0, stops = 0, occurrences = 0 },
            versaoAtualAlterada = false,
            duration_first_ms = (long)firstDuration.TotalMilliseconds,
            duration_second_ms = (long)secondDuration.TotalMilliseconds
        }, new JsonSerializerOptions { WriteIndented = true }));
        var poisonedPattern = first.Patterns[0] with
        {
            StructuralHash = new string('f', 64),
            Occurrences = [first.Patterns[0].Occurrences[0] with { StopId = "STOP_INEXISTENTE_ROLLBACK" }]
        };
        var poisoned = first with { Patterns = [poisonedPattern] };
        await Assert.ThrowsAnyAsync<Exception>(() => new GtfsDatarioPlanPersister(db).PersistAsync(poisoned, default));
        db.ChangeTracker.Clear();
        Assert.Equal(before.Versions, await db.PadroesVersoes.CountAsync());
        Assert.Equal(before.Occurrences, await db.OcorrenciasParadasPadroes.CountAsync());
        Assert.NotEmpty(first.Patterns);
    }

    [Fact]
    public async Task PostgresDescartavel_RebuildRepresentativo_AplicaCrosswalkSemPublicar()
    {
        var connection = Environment.GetEnvironmentVariable("GTFS_REBUILD_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;

        var zip = Environment.GetEnvironmentVariable("GTFS_TEST_ZIP");
        var crosswalkPath = Environment.GetEnvironmentVariable("GTFS_TEST_CROSSWALK");
        Assert.False(string.IsNullOrWhiteSpace(zip), "GTFS_TEST_ZIP é obrigatório.");
        Assert.False(string.IsNullOrWhiteSpace(crosswalkPath), "GTFS_TEST_CROSSWALK é obrigatório.");

        var options = new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql(connection, x => x.UseNetTopologySuite()).Options;
        await using var db = new TransporteDbContext(options);
        await db.Database.MigrateAsync();
        Assert.Equal(0, await db.Linhas.CountAsync());

        var planner = new GtfsDatarioImportService(new GtfsFeedParser());
        var dryRun = await planner.ExecuteAsync(new(zip!, GtfsDatarioMode.DryRun, crosswalkPath));
        Assert.Equal(16, dryRun.Crosswalk.Count);
        Assert.Equal(16, dryRun.Crosswalk.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(16, dryRun.Crosswalk.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        string[] expectedNew = ["498", "SP329", "SR300", "SR342", "SV391", "LECD142"];
        string[] expectedPreserved = ["391", "LECD152", "SV774", "54", "LECD153", "LECD154", "LECD155", "LECD158"];
        var aliasTargets = dryRun.Crosswalk.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exactCodes = dryRun.Feed.Routes.Select(x => x.RouteShortName)
            .Where(x => !aliasTargets.Contains(x) && !expectedNew.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(472, exactCodes.Count);

        var initialCodes = exactCodes.Concat(dryRun.Crosswalk.Keys).Concat(expectedPreserved)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(496, initialCodes.Count);
        var modal = new Modal { Id = Guid.NewGuid(), Nome = "Ônibus" };
        db.Modais.Add(modal);
        var initialLines = initialCodes.ToDictionary(x => x, x => new Linha
        {
            Id = Guid.NewGuid(), Codigo = x, Nome = x, ModalId = modal.Id, TipoRota = "regular"
        }, StringComparer.OrdinalIgnoreCase);
        var brtModal = new Modal { Id = Guid.Parse("b47b0000-0000-4000-8000-000000000001"), Nome = "BRT" };
        db.Modais.Add(brtModal);
        var brtCodes = dryRun.Feed.Routes.Where(x => x.RouteType == "702")
            .Select(x => x.RouteShortName).ToArray();
        Assert.Equal(30, brtCodes.Length);
        foreach (var code in brtCodes)
        {
            initialLines[code].ModalId = brtModal.Id;
            initialLines[code].TipoRota = "brt";
        }
        initialLines[expectedPreserved[0]].TipoRota = "desconhecido";
        db.Linhas.AddRange(initialLines.Values);
        await db.SaveChangesAsync();

        var watch = Stopwatch.StartNew();
        var service = new GtfsDatarioImportService(new GtfsFeedParser(), new GtfsDatarioPlanPersister(db));
        var result = await service.ExecuteAsync(new(zip!, GtfsDatarioMode.Persist, crosswalkPath));
        watch.Stop();
        db.ChangeTracker.Clear();

        var source = await db.FontesEstruturais.SingleAsync(x => x.Codigo == GtfsDatarioPlanPersister.SourceCode);
        var identities = await db.LinhasIdentidadesExternas
            .Where(x => x.FonteEstruturalId == source.Id && x.Tipo == "ROUTE_ID").ToArrayAsync();
        Assert.Equal(494, identities.Length);
        Assert.Equal(488, identities.Count(x => initialLines.Values.Any(y => y.Id == x.LinhaId)));
        Assert.Equal(502, await db.Linhas.CountAsync());
        foreach (var original in initialLines.Values)
        {
            var persisted = await db.Linhas.SingleAsync(x => x.Id == original.Id);
            Assert.Equal(original.ModalId, persisted.ModalId);
            Assert.Equal(original.TipoRota, persisted.TipoRota);
        }

        foreach (var alias in dryRun.Crosswalk)
        {
            var routeId = result.Feed.Routes.Single(x =>
                string.Equals(x.RouteShortName, alias.Value, StringComparison.OrdinalIgnoreCase)).RouteId;
            Assert.Equal(initialLines[alias.Key].Id, identities.Single(x => x.ExternalId == routeId).LinhaId);
            Assert.False(await db.Linhas.AnyAsync(x => x.Codigo == alias.Value));
        }
        foreach (var code in expectedNew) Assert.True(await db.Linhas.AnyAsync(x => x.Codigo == code));
        foreach (var code in expectedPreserved)
        {
            Assert.True(await db.Linhas.AnyAsync(x => x.Id == initialLines[code].Id));
            Assert.DoesNotContain(identities, x => x.LinhaId == initialLines[code].Id);
        }

        Assert.Equal(961, result.Patterns.Count);
        Assert.Equal(54_514, result.Patterns.Sum(x => x.Occurrences.Count));
        Assert.Equal(41, result.Patterns.Count(x => x.Circular));
        Assert.All(await db.PadroesOperacionais.ToArrayAsync(), x => Assert.Null(x.VersaoAtualId));
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            initial_lines = 496,
            exact_reused = 472,
            aliases_applied = 16,
            gtfs_routes_reusing_lines = 488,
            new_lines = expectedNew,
            preserved_arcgis_only = expectedPreserved,
            final_lines = 502,
            patterns = 961,
            occurrences = 54_514,
            circular_patterns = 41,
            versao_atual_alterada = false,
            duration_ms = watch.ElapsedMilliseconds
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
