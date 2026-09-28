using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Application.GTFS;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class GtfsDatarioPublicationIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PostgresDescartavel_PublicacaoERollbackSaoAtomicos_QuandoConfiguradoExplicitamente()
    {
        var connection = Environment.GetEnvironmentVariable("GTFS_PUBLISH_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var zip = Environment.GetEnvironmentVariable("GTFS_TEST_ZIP");
        var crosswalk = Environment.GetEnvironmentVariable("GTFS_TEST_CROSSWALK");
        Assert.False(string.IsNullOrWhiteSpace(zip), "GTFS_TEST_ZIP é obrigatório.");
        Assert.False(string.IsNullOrWhiteSpace(crosswalk), "GTFS_TEST_CROSSWALK é obrigatório.");

        var options = new DbContextOptionsBuilder<TransporteDbContext>()
            .UseNpgsql(connection, x => x.UseNetTopologySuite()).Options;
        await using var db = new TransporteDbContext(options);
        await db.Database.MigrateAsync();
        Assert.Equal(0, await db.PadroesOperacionais.CountAsync());

        var importer = new GtfsDatarioImportService(new GtfsFeedParser(), new GtfsDatarioPlanPersister(db));
        var plan = await importer.ExecuteAsync(new(zip!, GtfsDatarioMode.Persist, crosswalk));
        Assert.Equal(961, plan.Patterns.Count);
        db.ChangeTracker.Clear();

        var source = await db.FontesEstruturais.SingleAsync(x => x.Codigo == GtfsDatarioPlanPersister.SourceCode);
        var import = await db.ImportacoesEstruturais.SingleAsync(x =>
            x.FonteEstruturalId == source.Id && x.AlgoritmoVersao == GtfsDatarioPlanPersister.AlgorithmVersion);
        Assert.Equal(StatusImportacaoEstrutural.Concluida, import.Status);
        Assert.Equal(961 * 4, await db.PadroesVersoesImportacoes.CountAsync(x =>
            x.ImportacaoEstruturalId == import.Id));

        var patterns = await db.PadroesOperacionais.Include(x => x.Versoes).ToArrayAsync();
        Assert.Equal(961, patterns.Length);
        var oldByPattern = new Dictionary<Guid, Guid>();
        var newByPattern = new Dictionary<Guid, Guid>();
        foreach (var pattern in patterns)
        {
            var staged = Assert.Single(pattern.Versoes);
            newByPattern[pattern.Id] = staged.Id;
            var old = new PadraoVersao
            {
                Id = Guid.NewGuid(), PadraoOperacionalId = pattern.Id, Numero = staged.Numero + 1,
                Geometria = (LineString)staged.Geometria.Copy(), Topologia = staged.Topologia,
                ComprimentoMetros = staged.ComprimentoMetros,
                HashEstrutural = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("OLD|" + pattern.Id)))
                    .ToLowerInvariant(),
                MetodoConstrucao = "FIXTURE_ANTIGA", Confianca = 1, AlgoritmoVersao = "LEGACY_FIXTURE",
                ResultadoValidacao = ResultadosValidacaoPadrao.Valida, Relatorio = "{}",
                CriadoEmUtc = DateTimeOffset.UtcNow.AddDays(-1)
            };
            db.PadroesVersoes.Add(old);
            pattern.VersaoAtualId = old.Id;
            oldByPattern[pattern.Id] = old.Id;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var outside = await CreateOutsideScopeAsync(db);
        var service = new GtfsDatarioPublicationService(db);

        // Sem candidata.
        var missingPattern = patterns[0].Id;
        var removedLinks = await db.PadroesVersoesImportacoes
            .Where(x => x.ImportacaoEstruturalId == import.Id && x.PadraoVersao.PadraoOperacionalId == missingPattern)
            .ToArrayAsync();
        db.PadroesVersoesImportacoes.RemoveRange(removedLinks); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepararAsync(import.Id));
        db.PadroesVersoesImportacoes.AddRange(removedLinks); await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        // Duas identidades estruturais para o mesmo padrão.
        var originalIdentity = await db.PadroesIdentidadesExternas.SingleAsync(x =>
            x.FonteEstruturalId == source.Id && x.Tipo == "STRUCTURAL_KEY"
            && x.PadraoOperacionalId == missingPattern);
        var duplicateIdentity = new PadraoIdentidadeExterna
        {
            Id = Guid.NewGuid(), PadraoOperacionalId = missingPattern, FonteEstruturalId = source.Id,
            Tipo = "STRUCTURAL_KEY", ExternalId = originalIdentity.ExternalId + "|DUPLICADA"
        };
        db.PadroesIdentidadesExternas.Add(duplicateIdentity); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepararAsync(import.Id));
        db.PadroesIdentidadesExternas.Remove(await db.PadroesIdentidadesExternas.SingleAsync(x => x.Id == duplicateIdentity.Id));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        // Duas candidatas.
        var stagedForDuplicate = await db.PadroesVersoes.SingleAsync(x => x.Id == newByPattern[missingPattern]);
        var duplicate = CloneCandidate(stagedForDuplicate, 3, "DUPLICATE|");
        db.PadroesVersoes.Add(duplicate);
        db.PadroesVersoesImportacoes.AddRange(Roles().Select(role => new PadraoVersaoImportacao
            { PadraoVersaoId = duplicate.Id, ImportacaoEstruturalId = import.Id, Papel = role }));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepararAsync(import.Id));
        db.PadroesVersoesImportacoes.RemoveRange(await db.PadroesVersoesImportacoes
            .Where(x => x.PadraoVersaoId == duplicate.Id).ToArrayAsync());
        db.PadroesVersoes.Remove(await db.PadroesVersoes.SingleAsync(x => x.Id == duplicate.Id));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        // Hash ausente, algoritmo incorreto e fonte incorreta.
        var stagedToPoison = await db.PadroesVersoes.SingleAsync(x => x.Id == newByPattern[missingPattern]);
        var originalHash = stagedToPoison.HashEstrutural;
        stagedToPoison.HashEstrutural = ""; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepararAsync(import.Id));
        stagedToPoison = await db.PadroesVersoes.SingleAsync(x => x.Id == newByPattern[missingPattern]);
        stagedToPoison.HashEstrutural = originalHash; stagedToPoison.AlgoritmoVersao = "ALGORITMO_ERRADO";
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepararAsync(import.Id));
        stagedToPoison = await db.PadroesVersoes.SingleAsync(x => x.Id == newByPattern[missingPattern]);
        stagedToPoison.AlgoritmoVersao = GtfsDatarioPlanPersister.AlgorithmVersion;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var wrongSource = new FonteEstrutural { Id = Guid.NewGuid(), Codigo = "FONTE_ERRADA", Nome = "Fixture" };
        db.FontesEstruturais.Add(wrongSource); await db.SaveChangesAsync();
        import = await db.ImportacoesEstruturais.SingleAsync(x => x.Id == import.Id);
        import.FonteEstruturalId = wrongSource.Id; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepararAsync(import.Id));
        import = await db.ImportacoesEstruturais.SingleAsync(x => x.Id == import.Id);
        import.FonteEstruturalId = source.Id; await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        var manifest = await service.PrepararAsync(import.Id);
        Assert.Equal(961, manifest.Entries.Count);
        Assert.All(manifest.Entries, x => Assert.Equal(oldByPattern[x.PadraoOperacionalId], x.VersaoAnteriorId));

        // Lost update no fim do lote provoca rollback integral do que veio antes.
        var concurrent = manifest.Entries.OrderBy(x => x.PadraoOperacionalId).Last();
        await SetCurrentAsync(db, concurrent.PadraoOperacionalId, concurrent.VersaoNovaId);
        await Assert.ThrowsAsync<DBConcurrencyException>(() => service.PublicarAsync(manifest));
        db.ChangeTracker.Clear();
        var afterConcurrentPublish = await db.PadroesOperacionais
            .Where(x => oldByPattern.Keys.Contains(x.Id) && x.Id != concurrent.PadraoOperacionalId).ToArrayAsync();
        Assert.Equal(960, afterConcurrentPublish.Count(x => x.VersaoAtualId == oldByPattern[x.Id]));
        await SetCurrentAsync(db, concurrent.PadraoOperacionalId, concurrent.VersaoAnteriorId);

        await service.PublicarAsync(manifest); db.ChangeTracker.Clear();
        Assert.All(await db.PadroesOperacionais.Where(x => newByPattern.Keys.Contains(x.Id)).ToArrayAsync(),
            x => Assert.Equal(newByPattern[x.Id], x.VersaoAtualId));
        Assert.Equal(outside.VersionId, await db.PadroesOperacionais.Where(x => x.Id == outside.PatternId)
            .Select(x => x.VersaoAtualId).SingleAsync());
        Assert.Equal(961, await db.PadroesVersoes.CountAsync(x => oldByPattern.Values.Contains(x.Id)));

        // Divergência no rollback também reverte integralmente o lote parcial.
        await SetCurrentAsync(db, concurrent.PadraoOperacionalId, concurrent.VersaoAnteriorId);
        await Assert.ThrowsAsync<DBConcurrencyException>(() => service.ReverterAsync(manifest));
        db.ChangeTracker.Clear();
        var afterConcurrentRollback = await db.PadroesOperacionais
            .Where(x => newByPattern.Keys.Contains(x.Id) && x.Id != concurrent.PadraoOperacionalId).ToArrayAsync();
        Assert.Equal(960, afterConcurrentRollback.Count(x => x.VersaoAtualId == newByPattern[x.Id]));
        await SetCurrentAsync(db, concurrent.PadraoOperacionalId, concurrent.VersaoNovaId);
        await service.ReverterAsync(manifest); db.ChangeTracker.Clear();
        Assert.All(await db.PadroesOperacionais.Where(x => oldByPattern.Keys.Contains(x.Id)).ToArrayAsync(),
            x => Assert.Equal(oldByPattern[x.Id], x.VersaoAtualId));
        Assert.Equal(outside.VersionId, await db.PadroesOperacionais.Where(x => x.Id == outside.PatternId)
            .Select(x => x.VersaoAtualId).SingleAsync());

        output.WriteLine($"Publicação e rollback validados para {manifest.Entries.Count} padrões; importação {import.Id}.");
    }

    private static async Task<(Guid PatternId, Guid VersionId)> CreateOutsideScopeAsync(TransporteDbContext db)
    {
        var modal = new Modal { Id = Guid.NewGuid(), Nome = "Trem fixture" };
        var line = new Linha { Id = Guid.NewGuid(), ModalId = modal.Id, Codigo = "T-FIX", Nome = "Trem", TipoRota = "trem" };
        var direction = new Sentido { Id = Guid.NewGuid(), LinhaId = line.Id, Nome = "Trem" };
        var pattern = new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = direction.Id, Chave = "OUTSIDE", TipoServico = "TREM" };
        var version = new PadraoVersao
        {
            Id = Guid.NewGuid(), PadraoOperacionalId = pattern.Id, Numero = 1,
            Geometria = new LineString([new(0, 0), new(1, 1)]) { SRID = 4326 },
            Topologia = TopologiasPadrao.Linear, ComprimentoMetros = 1,
            HashEstrutural = new string('a', 64), MetodoConstrucao = "FIXTURE", Confianca = 1,
            AlgoritmoVersao = "FIXTURE", ResultadoValidacao = ResultadosValidacaoPadrao.Valida,
            Relatorio = "{}", CriadoEmUtc = DateTimeOffset.UtcNow
        };
        db.AddRange(modal, line, direction, pattern); await db.SaveChangesAsync();
        db.Add(version); await db.SaveChangesAsync();
        pattern.VersaoAtualId = version.Id; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        return (pattern.Id, version.Id);
    }

    private static PadraoVersao CloneCandidate(PadraoVersao source, int number, string prefix) => new()
    {
        Id = Guid.NewGuid(), PadraoOperacionalId = source.PadraoOperacionalId, Numero = number,
        Geometria = (LineString)source.Geometria.Copy(), Topologia = source.Topologia,
        ComprimentoMetros = source.ComprimentoMetros,
        HashEstrutural = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix + source.Id))).ToLowerInvariant(),
        MetodoConstrucao = source.MetodoConstrucao, Confianca = source.Confianca,
        AlgoritmoVersao = source.AlgoritmoVersao, ResultadoValidacao = source.ResultadoValidacao,
        Relatorio = source.Relatorio, CriadoEmUtc = DateTimeOffset.UtcNow
    };

    private static string[] Roles() =>
    [PapeisImportacaoPadrao.Membership, PapeisImportacaoPadrao.Geometria,
        PapeisImportacaoPadrao.Paradas, PapeisImportacaoPadrao.Metadados];

    private static async Task SetCurrentAsync(TransporteDbContext db, Guid patternId, Guid? versionId)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""PadroesOperacionais"" SET ""VersaoAtualId"" = {versionId} WHERE ""Id"" = {patternId}");
        db.ChangeTracker.Clear();
    }
}
