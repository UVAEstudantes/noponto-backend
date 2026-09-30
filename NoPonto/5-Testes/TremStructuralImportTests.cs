using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NoPonto.Application.TremV2;
using NoPonto.Data.Configuration;
using Xunit;

namespace NoPonto.Tests;

public sealed class TremStructuralSnapshotTests
{
    private readonly TremStructuralPlan _plan = new TremStructuralSnapshotLoader().Load();

    [Fact]
    public void SnapshotCurado_SatisfazGatesEstruturais()
    {
        Assert.Equal(8, _plan.Snapshot.Lines.Count);
        Assert.Equal(104, _plan.Snapshot.Stations.Count);
        Assert.Equal(151, _plan.Snapshot.Lines.Sum(x => x.Memberships.Count));
        Assert.Equal(104, _plan.Snapshot.Stations.Select(x => x.ExternalId).Distinct().Count());
        Assert.All(_plan.Snapshot.Lines, x => Assert.True(x.Geometry.Count >= 2));
        Assert.Contains(_plan.Snapshot.Lines.SelectMany(x => x.Memberships)
            .GroupBy(x => x.StationId), x => x.Count() > 1);
        Assert.Contains(_plan.Snapshot.Lines, x => x.Memberships.Zip(x.Memberships.Skip(1))
            .Any(pair => pair.Second.Order - pair.First.Order > 1));
    }

    [Fact]
    public void SnapshotCurado_PreservaDecisoesAprovadas()
    {
        Assert.Equal(3, _plan.Snapshot.Lines.Count(x => new[] { "Santa Cruz", "Japeri", "Deodoro" }.Contains(x.Name)));
        Assert.DoesNotContain(_plan.Snapshot.Lines, x => x.Name == "Gramacho");
        Assert.Contains(_plan.Snapshot.Stations, x => x.Name == "Silva Freire");
        var dalila = Assert.Single(_plan.Snapshot.Stations, x => x.Name == "Santa Dalila");
        Assert.Equal(-22.654004795683953, dalila.Latitude, 12);
        Assert.Equal(-43.15250996415523, dalila.Longitude, 12);
        Assert.Equal("DERIVADO_MULTIFONTE_ALTA_CONFIANCA", dalila.Confidence);
        Assert.Contains("GOOGLE_MAPS", dalila.PositionSource);
    }

    [Fact]
    public void Padroes_EspeciaisSaoSomenteOsFechados()
    {
        Assert.Equal(22, _plan.Patterns.Single(x => x.ExternalKey == "SANTA_CRUZ_CENTRAL_EXPRESSO").StationIds.Count);
        Assert.Equal(16, _plan.Patterns.Single(x => x.ExternalKey == "CAMPO_GRANDE_CENTRAL_LOCAL_EXPRESSO").StationIds.Count);
        Assert.Equal(19, _plan.Patterns.Single(x => x.ExternalKey == "JAPERI_CENTRAL_EXPRESSO_17").StationIds.Count);
        Assert.DoesNotContain(_plan.Patterns, x => x.ExternalKey.Contains("EXPRESSO_18"));
        Assert.Equal(19, _plan.Patterns.Count);
    }

    [Fact]
    public async Task DryRun_NaoConsultaConfiguracaoNemBanco()
    {
        var exit = await TremStructuralImportCommand.ExecuteAsync(["trem-structural-import", "--dry-run"]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task DryRun_PlanoInvalidoFalhaAntesDeConsultarAmbiente()
    {
        var exit = await TremStructuralImportCommand.ExecuteAsync(
            ["trem-structural-import", "--dry-run"],
            () => throw new InvalidDataException("fixture inválida"),
            _ => throw new InvalidOperationException("Ambiente não deveria ser consultado."));
        Assert.Equal(3, exit);
    }

    [Fact]
    public void HashCanonico_MudaSomenteComDefinicaoVersionavel()
    {
        var original = _plan.Snapshot;
        var replay = Build(original);
        Assert.Equal(_plan.Patterns.Select(x => x.StructuralHash), replay.Patterns.Select(x => x.StructuralHash));

        var line = original.Lines[0];
        var memberships = line.Memberships.ToArray();
        memberships[1] = memberships[1] with { Order = memberships[1].Order + 1 };
        var sourceSequence = Build(ReplaceLine(original, line with { Memberships = memberships }));
        Assert.NotEqual(_plan.Patterns[0].StructuralHash, sourceSequence.Patterns[0].StructuralHash);

        var special = original.SpecialPatterns[0];
        Assert.NotEqual(Build(original with { SpecialPatterns = Replace(original.SpecialPatterns, 0, special with { Method = special.Method + "_V2" }) }).Patterns[^3].StructuralHash, _plan.Patterns[^3].StructuralHash);
        Assert.NotEqual(Build(original with { SpecialPatterns = Replace(original.SpecialPatterns, 0, special with { Confidence = special.Confidence - .01 }) }).Patterns[^3].StructuralHash, _plan.Patterns[^3].StructuralHash);
        var geometry = line.Geometry.Select(x => x.ToArray()).ToArray(); geometry[1][0] += .000001;
        Assert.NotEqual(Build(ReplaceLine(original, line with { Geometry = geometry })).Patterns[0].StructuralHash, _plan.Patterns[0].StructuralHash);
    }

    [Fact]
    public void CoordenadaAlterada_MudaSomenteHashesAfetadosERecalculaMetricas()
    {
        var membershipCounts = _plan.Snapshot.Lines.SelectMany(x => x.Memberships)
            .GroupBy(x => x.StationId).ToDictionary(x => x.Key, x => x.Count());
        var stationId = _plan.Snapshot.Lines.Single(x => x.Name == "Belford Roxo").Memberships
            .Select(x => x.StationId).First(x => membershipCounts[x] == 1);
        var station = _plan.Snapshot.Stations.Single(x => x.ExternalId == stationId);
        var changed = _plan.Snapshot with { Stations = _plan.Snapshot.Stations
            .Select(x => x.ExternalId == stationId ? x with { Longitude = x.Longitude + .000001 } : x).ToArray() };
        var next = Build(changed);
        var affected = _plan.Patterns.Where(x => x.StationIds.Contains(stationId)).Select(x => x.ExternalKey).ToHashSet();
        Assert.NotEmpty(affected);
        Assert.All(_plan.Patterns, before => Assert.Equal(affected.Contains(before.ExternalKey),
            before.StructuralHash != next.Patterns.Single(x => x.ExternalKey == before.ExternalKey).StructuralHash));
        var oldOccurrence = _plan.Patterns.First(x => affected.Contains(x.ExternalKey)).Occurrences.Single(x => x.StationId == stationId);
        var newOccurrence = next.Patterns.First(x => x.ExternalKey == _plan.Patterns.First(p => affected.Contains(p.ExternalKey)).ExternalKey)
            .Occurrences.Single(x => x.StationId == stationId);
        Assert.True(oldOccurrence.Position != newOccurrence.Position
            || oldOccurrence.DistanceMetres != newOccurrence.DistanceMetres
            || oldOccurrence.LateralDistanceMetres != newOccurrence.LateralDistanceMetres);
    }

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public void PlanoOffline_RejeitaSnapshotInvalido(Func<TremStructuralSnapshot, TremStructuralSnapshot> mutate)
        => Assert.Throws<InvalidDataException>(() => Build(mutate(_plan.Snapshot)));

    public static IEnumerable<object[]> InvalidSnapshots()
    {
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => s with { Stations = Replace(s.Stations, 1, s.Stations[1] with { ExternalId = s.Stations[0].ExternalId }) })];
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => s with { Lines = Replace(s.Lines, 1, s.Lines[1] with { ExternalId = s.Lines[0].ExternalId }) })];
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => ReplaceLine(s, s.Lines[0] with { Memberships = Replace(s.Lines[0].Memberships, 0, s.Lines[0].Memberships[0] with { StationId = "AUSENTE" }) }))];
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => s with { Stations = Replace(s.Stations, 0, s.Stations[0] with { Longitude = 181 }) })];
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => s with { Stations = Replace(s.Stations, 0, s.Stations[0] with { Latitude = double.NaN }) })];
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => ReplaceLine(s, s.Lines[0] with { Geometry = [new[] { 181d, -22d }, new[] { -43d, -22d }] }))];
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => ReplaceLine(s, s.Lines[0] with { Geometry = [new[] { -43d, -22d }, new[] { -43d, -22d }] }))];
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => ReplaceLine(s, s.Lines[0] with { Geometry = [] }))];
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => ReplaceLine(s, s.Lines[0] with { Memberships = Replace(s.Lines[0].Memberships, 1, s.Lines[0].Memberships[1] with { Order = 0 }) }))];
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => s with { SpecialPatterns = Replace(s.SpecialPatterns, 0, s.SpecialPatterns[0] with { StationIds = ["AUSENTE", s.SpecialPatterns[0].StationIds[1]] }) })];
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => s with { SpecialPatterns = Replace(s.SpecialPatterns, 0, s.SpecialPatterns[0] with { StationIds = [] }) })];
        yield return [new Func<TremStructuralSnapshot, TremStructuralSnapshot>(s => s with { SpecialPatterns = s.SpecialPatterns.Append(s.SpecialPatterns[2] with { Key = "JAPERI_CENTRAL_EXPRESSO_18" }).ToArray() })];
    }

    [Fact]
    public void ToleranciaDeDoisMetros_NaoMascaraRegressaoDoSnapshotAprovado()
    {
        Assert.All(_plan.Patterns, pattern => Assert.True(pattern.Occurrences.Zip(pattern.Occurrences.Skip(1))
            .All(x => x.Second.DistanceMetres >= x.First.DistanceMetres)));
        Assert.Equal(2, TremStructuralSnapshotLoader.ProjectionRegressionToleranceMetres);
    }

    private static TremStructuralPlan Build(TremStructuralSnapshot snapshot) => TremStructuralSnapshotLoader.ValidateAndBuild(snapshot, new string('a', 64));
    private static TremStructuralSnapshot ReplaceLine(TremStructuralSnapshot snapshot, TremLineSnapshot line)
        => snapshot with { Lines = snapshot.Lines.Select(x => x.ExternalId == line.ExternalId ? line : x).ToArray() };
    private static IReadOnlyList<T> Replace<T>(IReadOnlyList<T> source, int index, T value)
        => source.Select((x, i) => i == index ? value : x).ToArray();
}

public sealed class TremStructuralImportPostgisTests
{
    [Fact]
    public async Task BancoVazio_ImportaPublicaERepeteSemDuplicar_QuandoConfigurado()
    {
        var connection = Environment.GetEnvironmentVariable("TREM_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("TREM_TEST_CONNECTION ausente: esta integração PostGIS não foi executada.");
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        var database = parsed.Database ?? string.Empty;
        if (!(database.Contains("test", StringComparison.OrdinalIgnoreCase)
              || database.Contains("descart", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("TREM_TEST_CONNECTION deve apontar para banco explicitamente descartável.");

        var services = new ServiceCollection();
        services.AdicionarPostgresCompartilhado(connection);
        services.AddScoped<TremStructuralImportService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        var plan = new TremStructuralSnapshotLoader().Load();
        var service = scope.ServiceProvider.GetRequiredService<TremStructuralImportService>();
        var first = await service.ImportAsync(plan);
        db.ChangeTracker.Clear();
        var pointers = await db.PadroesOperacionais.Where(x => x.Sentido.Linha.Modal.Nome == "Trem")
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.VersaoAtualId }).ToArrayAsync();
        var ids = await ReadIds(db);

        var second = await service.ImportAsync(plan);
        db.ChangeTracker.Clear();
        var secondIds = await ReadIds(db);
        var secondPointers = await db.PadroesOperacionais.Where(x => x.Sentido.Linha.Modal.Nome == "Trem")
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.VersaoAtualId }).ToArrayAsync();

        Assert.Equal(8, first.Lines); Assert.Equal(104, first.Stations); Assert.Equal(16, first.Directions);
        Assert.Equal(19, first.Patterns); Assert.Equal(19, first.Versions); Assert.Equal(359, first.Occurrences); Assert.Equal(19, first.Published);
        Assert.Equal(first.ImportacaoId, second.ImportacaoId); Assert.True(second.Reused);
        Assert.Equal(ids, secondIds); Assert.Equal(pointers, secondPointers);
        Assert.Equal(151, await CountStructuralMemberships(db));
        Assert.Equal(0, await db.OcorrenciasParadasPadroes.CountAsync(x => x.PadraoVersao.PadraoOperacional.Sentido.Linha.Modal.Nome == "Trem"
            && (!db.Paradas.Any(p => p.Id == x.ParadaId) || !db.PadroesVersoes.Any(v => v.Id == x.PadraoVersaoId))));

        // A -> A -> B -> B: somente o método do padrão fixture muda a definição imutável.
        var snapshot = plan.Snapshot;
        var special = snapshot.SpecialPatterns[0];
        var updatedLine = snapshot.Lines.Single(x => x.Name == "Belford Roxo");
        var updatedStation = snapshot.Stations[0];
        var bSnapshot = snapshot with
        {
            Lines = snapshot.Lines.Select(x => x.ExternalId == updatedLine.ExternalId ? x with { Name = x.Name + " Atualizada" } : x).ToArray(),
            Stations = snapshot.Stations.Select(x => x.ExternalId == updatedStation.ExternalId ? x with { Name = x.Name + " Atualizada" } : x).ToArray(),
            SpecialPatterns = snapshot.SpecialPatterns.Select((x, i) => i == 0 ? x with { Method = x.Method + "_V2" } : x).ToArray()
        };
        var b = TremStructuralSnapshotLoader.ValidateAndBuild(bSnapshot, new string('b', 64));
        var targetKey = special.Key;
        var bFirst = await service.ImportAsync(b); db.ChangeTracker.Clear();
        var targetPattern = await db.PadroesOperacionais.SingleAsync(x => x.Chave == targetKey);
        var targetVersions = await db.PadroesVersoes.Where(x => x.PadraoOperacionalId == targetPattern.Id).OrderBy(x => x.Numero).ToArrayAsync();
        Assert.Equal([1, 2], targetVersions.Select(x => x.Numero)); Assert.Equal(targetVersions[1].Id, targetPattern.VersaoAtualId);
        Assert.NotEqual(targetVersions[0].HashEstrutural, targetVersions[1].HashEstrutural);
        Assert.Equal("Belford Roxo Atualizada", (await db.Linhas.SingleAsync(x => x.Id == idsLine(db, updatedLine.ExternalId))).Nome);
        Assert.Equal(updatedStation.Name + " Atualizada", (await db.Paradas.SingleAsync(x => x.Id == idsStop(db, updatedStation.ExternalId))).Nome);
        var bSecond = await service.ImportAsync(b); db.ChangeTracker.Clear();
        Assert.Equal(bFirst.ImportacaoId, bSecond.ImportacaoId); Assert.True(bSecond.Reused);
        Assert.Equal(2, await db.PadroesVersoes.CountAsync(x => x.PadraoOperacionalId == targetPattern.Id));

        // Mudança espacial preserva a Parada e versiona somente padrões que dependem dela.
        var membershipCounts = b.Snapshot.Lines.SelectMany(x => x.Memberships).GroupBy(x => x.StationId)
            .ToDictionary(x => x.Key, x => x.Count());
        var coordinateStationId = b.Snapshot.Lines.Single(x => x.Name.StartsWith("Belford Roxo", StringComparison.Ordinal))
            .Memberships.Select(x => x.StationId).First(x => membershipCounts[x] == 1);
        var coordinateStation = b.Snapshot.Stations.Single(x => x.ExternalId == coordinateStationId);
        var cSnapshot = b.Snapshot with { Stations = b.Snapshot.Stations.Select(x => x.ExternalId == coordinateStationId
            ? x with { Longitude = x.Longitude + .000001, PositionSource = x.PositionSource + "_RECONCILIADA" } : x).ToArray() };
        var c = TremStructuralSnapshotLoader.ValidateAndBuild(cSnapshot, new string('c', 64));
        var affectedKeys = b.Patterns.Where(x => x.StationIds.Contains(coordinateStationId)).Select(x => x.ExternalKey).ToHashSet();
        var patternKeysById = await db.PadroesOperacionais.ToDictionaryAsync(x => x.Id, x => x.Chave);
        var versionCountsByPattern = await db.PadroesVersoes.GroupBy(x => x.PadraoOperacionalId)
            .Select(x => new { PatternId = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.PatternId, x => x.Count);
        var countsBeforeCoordinate = patternKeysById.ToDictionary(x => x.Value,
            x => versionCountsByPattern.GetValueOrDefault(x.Key));
        var stationEntityId = idsStop(db, coordinateStationId);
        var oldMetrics = await db.OcorrenciasParadasPadroes.Where(x => x.ParadaId == stationEntityId
            && x.PadraoVersao.PadraoOperacional.VersaoAtualId == x.PadraoVersaoId)
            .Select(x => new { x.PadraoVersao.PadraoOperacional.Chave, x.PosicaoTracado, x.DistanciaAcumuladaMetros, x.DistanciaDaLinhaMetros }).ToArrayAsync();
        await service.ImportAsync(c); db.ChangeTracker.Clear();
        Assert.Equal(stationEntityId, idsStop(db, coordinateStationId));
        var persistedStation = await db.Paradas.SingleAsync(x => x.Id == stationEntityId);
        Assert.Equal(coordinateStation.Longitude + .000001, persistedStation.Localizacao.X);
        var tremPatternKeys = c.Patterns.Select(x => x.ExternalKey).ToArray();
        var patternsAfterCoordinate = await db.PadroesOperacionais.Where(x => tremPatternKeys.Contains(x.Chave))
            .Select(x => new { x.Id, x.Chave }).ToArrayAsync();
        var countsAfterCoordinate = await db.PadroesVersoes.GroupBy(x => x.PadraoOperacionalId)
            .Select(x => new { PatternId = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.PatternId, x => x.Count);
        foreach (var pattern in patternsAfterCoordinate)
        {
            var expectedCount = countsBeforeCoordinate[pattern.Chave] + (affectedKeys.Contains(pattern.Chave) ? 1 : 0);
            var actualCount = countsAfterCoordinate.GetValueOrDefault(pattern.Id);
            Assert.True(actualCount == expectedCount,
                $"{pattern.Chave}: esperado {expectedCount}, obtido {actualCount}, afetado={affectedKeys.Contains(pattern.Chave)}");
        }
        var newMetrics = await db.OcorrenciasParadasPadroes.Where(x => x.ParadaId == stationEntityId
            && x.PadraoVersao.PadraoOperacional.VersaoAtualId == x.PadraoVersaoId)
            .Select(x => new { x.PadraoVersao.PadraoOperacional.Chave, x.PosicaoTracado, x.DistanciaAcumuladaMetros, x.DistanciaDaLinhaMetros }).ToArrayAsync();
        Assert.All(affectedKeys, key => Assert.Contains(newMetrics, current => current.Chave == key
            && oldMetrics.Any(old => old.Chave == key && (old.PosicaoTracado != current.PosicaoTracado
                || old.DistanciaAcumuladaMetros != current.DistanciaAcumuladaMetros
                || old.DistanciaDaLinhaMetros != current.DistanciaDaLinhaMetros))));
        var versionsAfterCoordinate = await db.PadroesVersoes.CountAsync();
        await service.ImportAsync(c); db.ChangeTracker.Clear();
        Assert.Equal(versionsAfterCoordinate, await db.PadroesVersoes.CountAsync());

        // Hash armazenado é necessário, mas não suficiente: conteúdo imutável corrompido deve falhar.
        var corruptPattern = await db.PadroesOperacionais.SingleAsync(x => x.Chave == affectedKeys.First());
        var corruptVersionId = corruptPattern.VersaoAtualId!.Value;
        var corruptOccurrence = await db.OcorrenciasParadasPadroes.Where(x => x.PadraoVersaoId == corruptVersionId)
            .OrderBy(x => x.Ordem).FirstAsync();
        var originalPosition = corruptOccurrence.PosicaoTracado;
        await AssertCorruptionRejected(async () => { corruptOccurrence.PosicaoTracado += .001; await db.SaveChangesAsync(); },
            async () => { var x = await db.OcorrenciasParadasPadroes.FindAsync(corruptOccurrence.Id); x!.PosicaoTracado = originalPosition; await db.SaveChangesAsync(); }, c, service, db);
        var originalSource = corruptOccurrence.SourceSequence;
        await AssertCorruptionRejected(async () => { var x = await db.OcorrenciasParadasPadroes.FindAsync(corruptOccurrence.Id); x!.SourceSequence = (originalSource ?? 0) + 1; await db.SaveChangesAsync(); },
            async () => { var x = await db.OcorrenciasParadasPadroes.FindAsync(corruptOccurrence.Id); x!.SourceSequence = originalSource; await db.SaveChangesAsync(); }, c, service, db);
        var originalStop = corruptOccurrence.ParadaId;
        var otherStop = await db.Paradas.Where(x => x.Id != originalStop).Select(x => x.Id).FirstAsync();
        await AssertCorruptionRejected(async () => { var x = await db.OcorrenciasParadasPadroes.FindAsync(corruptOccurrence.Id); x!.ParadaId = otherStop; await db.SaveChangesAsync(); },
            async () => { var x = await db.OcorrenciasParadasPadroes.FindAsync(corruptOccurrence.Id); x!.ParadaId = originalStop; await db.SaveChangesAsync(); }, c, service, db);
        var occurrenceCopy = new NoPonto.Domain.Entities.OcorrenciaParadaPadrao { Id = corruptOccurrence.Id, PadraoVersaoId = corruptOccurrence.PadraoVersaoId, ParadaId = originalStop, Ordem = corruptOccurrence.Ordem, SourceSequence = originalSource, PosicaoTracado = originalPosition, DistanciaAcumuladaMetros = corruptOccurrence.DistanciaAcumuladaMetros, DistanciaDaLinhaMetros = corruptOccurrence.DistanciaDaLinhaMetros, SourceShapeDistTraveledMetros = corruptOccurrence.SourceShapeDistTraveledMetros };
        await AssertCorruptionRejected(async () => { var x = await db.OcorrenciasParadasPadroes.FindAsync(corruptOccurrence.Id); db.Remove(x!); await db.SaveChangesAsync(); },
            async () => { db.OcorrenciasParadasPadroes.Add(occurrenceCopy); await db.SaveChangesAsync(); }, c, service, db);
        var versionEntity = await db.PadroesVersoes.FindAsync(corruptVersionId); var originalGeometry = (NetTopologySuite.Geometries.LineString)versionEntity!.Geometria.Copy();
        await AssertCorruptionRejected(async () => { var x = await db.PadroesVersoes.FindAsync(corruptVersionId); var coordinates = x!.Geometria.Coordinates; coordinates[0].X += .000001; x.Geometria = new NetTopologySuite.Geometries.LineString(coordinates) { SRID = x.Geometria.SRID }; await db.SaveChangesAsync(); },
            async () => { var x = await db.PadroesVersoes.FindAsync(corruptVersionId); x!.Geometria = (NetTopologySuite.Geometries.LineString)originalGeometry.Copy(); await db.SaveChangesAsync(); }, c, service, db);
        var originalMethod = versionEntity.MetodoConstrucao;
        await AssertCorruptionRejected(async () => { var x = await db.PadroesVersoes.FindAsync(corruptVersionId); x!.MetodoConstrucao += "_CORROMPIDO"; await db.SaveChangesAsync(); },
            async () => { var x = await db.PadroesVersoes.FindAsync(corruptVersionId); x!.MetodoConstrucao = originalMethod; await db.SaveChangesAsync(); }, c, service, db);

        // Divergência de identidade falha e a transação não altera pointer/versões.
        var lineIdentity = await db.LinhasIdentidadesExternas.Include(x => x.Linha).SingleAsync(x => x.FonteEstrutural.Codigo == TremStructuralImportService.SourceCode && x.ExternalId == updatedLine.ExternalId);
        lineIdentity.Linha.Codigo = "CORROMPIDA"; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var beforePointer = targetPattern.VersaoAtualId; var beforeVersions = await db.PadroesVersoes.CountAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(plan)); db.ChangeTracker.Clear();
        Assert.Equal(beforeVersions, await db.PadroesVersoes.CountAsync());
        Assert.Equal(beforePointer, (await db.PadroesOperacionais.SingleAsync(x => x.Id == targetPattern.Id)).VersaoAtualId);
    }

    private static async Task AssertCorruptionRejected(Func<Task> corrupt, Func<Task> restore,
        TremStructuralPlan plan, TremStructuralImportService service, TransporteDbContext db)
    {
        await SetImmutabilityTriggers(db, enabled: false);
        try { await corrupt(); }
        finally { await SetImmutabilityTriggers(db, enabled: true); }
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(plan)); db.ChangeTracker.Clear();
        await SetImmutabilityTriggers(db, enabled: false);
        try { await restore(); }
        finally { await SetImmutabilityTriggers(db, enabled: true); }
        db.ChangeTracker.Clear();
    }

    private static Task SetImmutabilityTriggers(TransporteDbContext db, bool enabled)
    {
        return db.Database.ExecuteSqlRawAsync(enabled
            ? """
              ALTER TABLE "OcorrenciasParadasPadroes" ENABLE TRIGGER "TR_OcorrenciasPadroes_Imutavel";
              ALTER TABLE "PadroesVersoes" ENABLE TRIGGER "TR_PadroesVersoes_Imutavel";
              """
            : """
              ALTER TABLE "OcorrenciasParadasPadroes" DISABLE TRIGGER "TR_OcorrenciasPadroes_Imutavel";
              ALTER TABLE "PadroesVersoes" DISABLE TRIGGER "TR_PadroesVersoes_Imutavel";
              """);
    }

    private static Guid idsLine(TransporteDbContext db, string externalId) => db.LinhasIdentidadesExternas.Single(x => x.FonteEstrutural.Codigo == TremStructuralImportService.SourceCode && x.ExternalId == externalId).LinhaId;
    private static Guid idsStop(TransporteDbContext db, string externalId) => db.ParadasIdentidadesExternas.Single(x => x.FonteEstrutural.Codigo == TremStructuralImportService.SourceCode && x.ExternalId == externalId).ParadaId;

    private static async Task<string[]> ReadIds(TransporteDbContext db) => await db.PadroesVersoes
        .Where(x => x.PadraoOperacional.Sentido.Linha.Modal.Nome == "Trem")
        .OrderBy(x => x.Id).Select(x => x.Id.ToString()).ToArrayAsync();

    private static async Task<int> CountStructuralMemberships(TransporteDbContext db) =>
        (await db.OcorrenciasParadasPadroes
            .Where(x => x.PadraoVersao.PadraoOperacional.Sentido.Linha.Modal.Nome == "Trem"
                && x.PadraoVersao.PadraoOperacional.Chave.EndsWith(":FORWARD:BASE"))
            .Select(x => new { x.PadraoVersao.PadraoOperacional.Sentido.LinhaId, x.ParadaId })
            .Distinct().ToArrayAsync()).Length;
}
