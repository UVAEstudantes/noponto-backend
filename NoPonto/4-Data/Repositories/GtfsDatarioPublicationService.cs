using System.Data;
using Microsoft.EntityFrameworkCore;
using NoPonto.Domain.Entities;

namespace NoPonto.Data.Repositories;

public sealed record GtfsDatarioPublicationEntry(
    Guid PadraoOperacionalId,
    Guid? VersaoAnteriorId,
    Guid VersaoNovaId,
    string StructuralHash);

public sealed record GtfsDatarioPublicationManifest(
    Guid ImportacaoEstruturalId,
    DateTimeOffset PublicadoEmUtc,
    IReadOnlyList<GtfsDatarioPublicationEntry> Entries);

public sealed class GtfsDatarioPublicationService(TransporteDbContext db)
{
    private static readonly string[] RequiredRoles =
    [
        PapeisImportacaoPadrao.Membership,
        PapeisImportacaoPadrao.Geometria,
        PapeisImportacaoPadrao.Paradas,
        PapeisImportacaoPadrao.Metadados
    ];

    public async Task<GtfsDatarioPublicationManifest> PrepararAsync(
        Guid importacaoEstruturalId, CancellationToken ct = default)
    {
        var candidates = await ValidateAndReadCandidatesAsync(importacaoEstruturalId, ct);
        return new(importacaoEstruturalId, DateTimeOffset.UtcNow,
            candidates.OrderBy(x => x.PatternId).Select(x => new GtfsDatarioPublicationEntry(
                x.PatternId, x.CurrentVersionId, x.NewVersionId, x.StructuralHash)).ToArray());
    }

    public async Task PublicarAsync(GtfsDatarioPublicationManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateManifestShape(manifest);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var candidates = await ValidateAndReadCandidatesAsync(manifest.ImportacaoEstruturalId, ct);
            ValidateManifestCandidates(manifest, candidates);
            foreach (var entry in manifest.Entries)
            {
                var affected = await db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE ""PadroesOperacionais""
                    SET ""VersaoAtualId"" = {entry.VersaoNovaId}
                    WHERE ""Id"" = {entry.PadraoOperacionalId}
                      AND ""VersaoAtualId"" IS NOT DISTINCT FROM {entry.VersaoAnteriorId}", ct);
                if (affected != 1)
                    throw new DBConcurrencyException(
                        $"Ponteiro concorrente divergente para PadraoOperacional {entry.PadraoOperacionalId}.");
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task ReverterAsync(GtfsDatarioPublicationManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateManifestShape(manifest);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var candidates = await ValidateAndReadCandidatesAsync(manifest.ImportacaoEstruturalId, ct);
            ValidateManifestCandidates(manifest, candidates);
            foreach (var entry in manifest.Entries)
            {
                var affected = await db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE ""PadroesOperacionais""
                    SET ""VersaoAtualId"" = {entry.VersaoAnteriorId}
                    WHERE ""Id"" = {entry.PadraoOperacionalId}
                      AND ""VersaoAtualId"" = {entry.VersaoNovaId}", ct);
                if (affected != 1)
                    throw new DBConcurrencyException(
                        $"Rollback concorrente divergente para PadraoOperacional {entry.PadraoOperacionalId}.");
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<Candidate[]> ValidateAndReadCandidatesAsync(Guid importId, CancellationToken ct)
    {
        var import = await db.ImportacoesEstruturais.AsNoTracking()
            .Include(x => x.FonteEstrutural).SingleOrDefaultAsync(x => x.Id == importId, ct)
            ?? throw new InvalidDataException($"Importação estrutural {importId} inexistente.");
        if (import.FonteEstrutural.Codigo != GtfsDatarioPlanPersister.SourceCode
            || import.AlgoritmoVersao != GtfsDatarioPlanPersister.AlgorithmVersion
            || import.Status != StatusImportacaoEstrutural.Concluida)
            throw new InvalidDataException("Importação não é uma operação Data.Rio concluída e elegível.");

        var identities = await db.PadroesIdentidadesExternas.AsNoTracking()
            .Where(x => x.FonteEstruturalId == import.FonteEstruturalId && x.Tipo == "STRUCTURAL_KEY")
            .Select(x => new
            {
                PatternId = x.PadraoOperacionalId,
                x.ExternalId,
                CurrentVersionId = x.PadraoOperacional.VersaoAtualId,
                Modal = x.PadraoOperacional.Sentido.Linha.Modal.Nome
            }).ToArrayAsync(ct);
        if (identities.Length == 0) throw new InvalidDataException("Importação sem padrões Data.Rio identificados.");
        if (identities.Any(x => x.Modal != "Ônibus"))
            throw new InvalidDataException("A publicação Data.Rio contém padrão fora do modal Ônibus.");
        if (identities.GroupBy(x => x.PatternId).Any(x => x.Count() != 1))
            throw new InvalidDataException("Identidade STRUCTURAL_KEY inconsistente para padrão Data.Rio.");

        var links = await db.PadroesVersoesImportacoes.AsNoTracking()
            .Where(x => x.ImportacaoEstruturalId == importId)
            .Select(x => new
            {
                x.PadraoVersaoId,
                x.Papel,
                PatternId = x.PadraoVersao.PadraoOperacionalId,
                x.PadraoVersao.HashEstrutural,
                x.PadraoVersao.AlgoritmoVersao,
                x.PadraoVersao.ResultadoValidacao
            }).ToArrayAsync(ct);
        var byPattern = links.GroupBy(x => x.PatternId).ToDictionary(x => x.Key);
        var result = new List<Candidate>(identities.Length);
        foreach (var identity in identities)
        {
            if (!byPattern.TryGetValue(identity.PatternId, out var patternLinks))
                throw new InvalidDataException($"Padrão {identity.PatternId} sem versão elegível nesta importação.");
            var versions = patternLinks.GroupBy(x => x.PadraoVersaoId).ToArray();
            if (versions.Length != 1)
                throw new InvalidDataException($"Padrão {identity.PatternId} possui {versions.Length} versões elegíveis.");
            var version = versions[0];
            var sample = version.First();
            if (sample.PatternId != identity.PatternId)
                throw new InvalidDataException($"Versão ligada ao padrão incorreto: {sample.PadraoVersaoId}.");
            if (sample.AlgoritmoVersao != GtfsDatarioPlanPersister.AlgorithmVersion)
                throw new InvalidDataException($"Algoritmo inválido na versão {sample.PadraoVersaoId}.");
            if (sample.ResultadoValidacao != ResultadosValidacaoPadrao.Valida
                || string.IsNullOrWhiteSpace(sample.HashEstrutural))
                throw new InvalidDataException($"Versão estruturalmente inválida: {sample.PadraoVersaoId}.");
            var roles = version.Select(x => x.Papel).ToHashSet(StringComparer.Ordinal);
            if (RequiredRoles.Any(x => !roles.Contains(x)))
                throw new InvalidDataException($"Proveniência incompleta na versão {sample.PadraoVersaoId}.");
            result.Add(new(identity.PatternId, identity.CurrentVersionId, sample.PadraoVersaoId,
                sample.HashEstrutural));
        }
        if (links.Any(x => identities.All(y => y.PatternId != x.PatternId)))
            throw new InvalidDataException("Importação referencia versão sem identidade Data.Rio correspondente.");
        return result.ToArray();
    }

    private static void ValidateManifestShape(GtfsDatarioPublicationManifest manifest)
    {
        if (manifest.ImportacaoEstruturalId == Guid.Empty || manifest.Entries.Count == 0)
            throw new InvalidDataException("Manifesto de publicação vazio ou inválido.");
        if (manifest.Entries.Any(x => x.PadraoOperacionalId == Guid.Empty || x.VersaoNovaId == Guid.Empty
            || string.IsNullOrWhiteSpace(x.StructuralHash)))
            throw new InvalidDataException("Manifesto contém entrada inválida.");
        if (manifest.Entries.GroupBy(x => x.PadraoOperacionalId).Any(x => x.Count() != 1))
            throw new InvalidDataException("Manifesto contém padrão duplicado.");
    }

    private static void ValidateManifestCandidates(
        GtfsDatarioPublicationManifest manifest, IReadOnlyCollection<Candidate> candidates)
    {
        if (manifest.Entries.Count != candidates.Count)
            throw new InvalidDataException("Manifesto não contém exatamente o escopo Data.Rio elegível.");
        var expected = candidates.ToDictionary(x => x.PatternId);
        foreach (var entry in manifest.Entries)
        {
            if (!expected.TryGetValue(entry.PadraoOperacionalId, out var candidate)
                || candidate.NewVersionId != entry.VersaoNovaId
                || !string.Equals(candidate.StructuralHash, entry.StructuralHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Manifesto divergente para padrão {entry.PadraoOperacionalId}.");
        }
    }

    private sealed record Candidate(
        Guid PatternId, Guid? CurrentVersionId, Guid NewVersionId, string StructuralHash);
}
