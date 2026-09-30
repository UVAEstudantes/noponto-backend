using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.TremV2;

public sealed record TremImportReport(Guid ImportacaoId, int Lines, int Stations, int Directions, int Patterns, int Versions, int Occurrences, int Published, bool Reused);

public sealed class TremStructuralImportService(TransporteDbContext db)
{
    public const string SourceCode = "TRENSRJ_ESTRUTURAL_CURADO";

    public async Task<TremImportReport> ImportAsync(TremStructuralPlan plan, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var source = await db.FontesEstruturais.SingleOrDefaultAsync(x => x.Codigo == SourceCode, ct);
        if (source is null) { source = new() { Id = Id("source", SourceCode), Codigo = SourceCode, Nome = plan.Snapshot.SourceName }; db.FontesEstruturais.Add(source); }
        else source.Nome = plan.Snapshot.SourceName;
        var existingImport = await db.ImportacoesEstruturais.SingleOrDefaultAsync(x => x.FonteEstruturalId == source.Id && x.ConteudoHash == plan.ContentHash && x.AlgoritmoVersao == plan.Snapshot.AlgorithmVersion && x.Status == StatusImportacaoEstrutural.Concluida, ct);
        var import = existingImport ?? new ImportacaoEstrutural { Id = Id("import", plan.ContentHash), FonteEstruturalId = source.Id, Status = StatusImportacaoEstrutural.EmProcessamento, IniciadaEmUtc = now, VersaoFonte = plan.Snapshot.SchemaVersion, ConteudoHash = plan.ContentHash, AlgoritmoVersao = plan.Snapshot.AlgorithmVersion, Relatorio = JsonSerializer.Serialize(new { plan.Snapshot.CapturedAtUtc, Proveniencia = "snapshot_curado_offline" }) };
        if (existingImport is null) db.ImportacoesEstruturais.Add(import);

        var modal = await db.Modais.SingleOrDefaultAsync(x => x.Nome == "Trem", ct);
        if (modal is null) { modal = new() { Id = Id("modal", "Trem"), Nome = "Trem" }; db.Modais.Add(modal); }

        var lineIdentities = await db.LinhasIdentidadesExternas.Include(x => x.Linha).Where(x => x.FonteEstruturalId == source.Id && x.Tipo == "LINE_ID").ToDictionaryAsync(x => x.ExternalId, ct);
        foreach (var item in plan.Snapshot.Lines)
        {
            if (!lineIdentities.TryGetValue(item.ExternalId, out var identity))
            {
                var line = new Linha { Id = Id("line", item.ExternalId), Codigo = $"TREM-{item.Code}", Nome = item.Name, ModalId = modal.Id, TipoRota = item.Type, Consorcio = "TrensRJ" };
                identity = new() { Id = Id("line-external", item.ExternalId), Linha = line, LinhaId = line.Id, FonteEstruturalId = source.Id, Tipo = "LINE_ID", ExternalId = item.ExternalId, Confianca = 1 };
                db.Linhas.Add(line); db.LinhasIdentidadesExternas.Add(identity); lineIdentities[item.ExternalId] = identity;
            }
            var persisted = identity.Linha;
            if (persisted.Id != Id("line", item.ExternalId) || persisted.Codigo != $"TREM-{item.Code}" || persisted.ModalId != modal.Id) throw Divergence("Linha", item.ExternalId);
            persisted.Nome = item.Name; persisted.TipoRota = item.Type; persisted.Consorcio = "TrensRJ";
        }

        var stopIdentities = await db.ParadasIdentidadesExternas.Include(x => x.Parada).Where(x => x.FonteEstruturalId == source.Id && x.Tipo == "STATION_ID").ToDictionaryAsync(x => x.ExternalId, ct);
        foreach (var item in plan.Snapshot.Stations)
        {
            if (!stopIdentities.TryGetValue(item.ExternalId, out var identity))
            {
                var stop = new Parada { Id = Id("station", item.ExternalId), Codigo = $"TREM-{item.ExternalId}", ChaveCanonica = $"TREM:{item.ExternalId}", Nome = item.Name, Localizacao = Point(item), ModalId = modal.Id, TipoLocal = TiposLocalParada.Estacao };
                identity = new() { Id = Id("station-external", item.ExternalId), Parada = stop, ParadaId = stop.Id, FonteEstruturalId = source.Id, Tipo = "STATION_ID", ExternalId = item.ExternalId, Confianca = 1 };
                db.Paradas.Add(stop); db.ParadasIdentidadesExternas.Add(identity); stopIdentities[item.ExternalId] = identity;
            }
            var persisted = identity.Parada;
            if (persisted.Id != Id("station", item.ExternalId) || persisted.Codigo != $"TREM-{item.ExternalId}" || persisted.ChaveCanonica != $"TREM:{item.ExternalId}" || persisted.ModalId != modal.Id || persisted.TipoLocal != TiposLocalParada.Estacao || persisted.ParadaPaiId is not null) throw Divergence("Parada", item.ExternalId);
            persisted.Nome = item.Name; persisted.Localizacao = Point(item); identity.Justificativa = JsonSerializer.Serialize(new { item.Slug, item.PositionSource, item.PositionMethod, item.Confidence });
        }

        var directionIdentities = await db.SentidosIdentidadesExternas.Include(x => x.Sentido).Where(x => x.FonteEstruturalId == source.Id && x.Tipo == "DIRECTION").ToDictionaryAsync(x => x.ExternalId, ct);
        foreach (var line in plan.Snapshot.Lines) foreach (var direction in new[] { "FORWARD", "REVERSE" })
        {
            var external = $"{line.ExternalId}:{direction}"; var lineId = lineIdentities[line.ExternalId].LinhaId;
            if (!directionIdentities.TryGetValue(external, out var identity))
            {
                var sentido = new Sentido { Id = Id("direction", external), LinhaId = lineId, Nome = direction };
                identity = new() { Id = Id("direction-external", external), Sentido = sentido, SentidoId = sentido.Id, FonteEstruturalId = source.Id, Tipo = "DIRECTION", ExternalId = external, Confianca = 1 };
                db.Sentidos.Add(sentido); db.SentidosIdentidadesExternas.Add(identity); directionIdentities[external] = identity;
            }
            if (identity.Sentido.Id != Id("direction", external) || identity.Sentido.LinhaId != lineId) throw Divergence("Sentido", external);
            identity.Sentido.Nome = direction;
            var ordered = direction == "FORWARD" ? line.Memberships : line.Memberships.Reverse(); identity.Justificativa = $"{ordered.First().StationId}->{ordered.Last().StationId}";
        }

        var patternIdentities = await db.PadroesIdentidadesExternas.Include(x => x.PadraoOperacional).Where(x => x.FonteEstruturalId == source.Id && x.Tipo == "PATTERN_KEY").ToDictionaryAsync(x => x.ExternalId, ct);
        var publications = new List<(PadraoOperacional Pattern, Guid VersionId)>();
        foreach (var item in plan.Patterns)
        {
            var directionId = directionIdentities[$"{item.Line.ExternalId}:{item.Direction}"].SentidoId;
            if (!patternIdentities.TryGetValue(item.ExternalKey, out var identity))
            {
                var pattern = new PadraoOperacional { Id = Id("pattern", item.ExternalKey), SentidoId = directionId, Chave = item.ExternalKey, TipoServico = item.Type, NomePublico = item.Name };
                identity = new() { Id = Id("pattern-external", item.ExternalKey), PadraoOperacional = pattern, PadraoOperacionalId = pattern.Id, FonteEstruturalId = source.Id, Tipo = "PATTERN_KEY", ExternalId = item.ExternalKey, Confianca = item.Confidence };
                db.PadroesOperacionais.Add(pattern); db.PadroesIdentidadesExternas.Add(identity); patternIdentities[item.ExternalKey] = identity;
            }
            var persisted = identity.PadraoOperacional;
            if (persisted.Id != Id("pattern", item.ExternalKey) || persisted.Chave != item.ExternalKey || persisted.SentidoId != directionId) throw Divergence("Padrão", item.ExternalKey);
            persisted.TipoServico = item.Type; persisted.NomePublico = item.Name; identity.Confianca = item.Confidence;

            var version = await db.PadroesVersoes.SingleOrDefaultAsync(x => x.PadraoOperacionalId == persisted.Id && x.HashEstrutural == item.StructuralHash, ct);
            if (version is null)
            {
                var number = (await db.PadroesVersoes.Where(x => x.PadraoOperacionalId == persisted.Id).MaxAsync(x => (int?)x.Numero, ct) ?? 0) + 1;
                version = new() { Id = Id("version", item.StructuralHash), PadraoOperacionalId = persisted.Id, Numero = number, Geometria = (LineString)item.Geometry.Copy(), Topologia = TopologiasPadrao.Linear, ComprimentoMetros = item.LengthMetres, HashEstrutural = item.StructuralHash, MetodoConstrucao = item.Method, Confianca = item.Confidence, AlgoritmoVersao = plan.Snapshot.AlgorithmVersion, ResultadoValidacao = ResultadosValidacaoPadrao.Valida, Relatorio = item.VersionReport, CriadoEmUtc = now };
                db.PadroesVersoes.Add(version);
                foreach (var occurrence in item.Occurrences) db.OcorrenciasParadasPadroes.Add(new() { Id = Id("occurrence", $"{version.Id}:{occurrence.Order}"), PadraoVersaoId = version.Id, ParadaId = stopIdentities[occurrence.StationId].ParadaId, Ordem = occurrence.Order, SourceSequence = occurrence.SourceSequence, PosicaoTracado = occurrence.Position, DistanciaAcumuladaMetros = occurrence.DistanceMetres, DistanciaDaLinhaMetros = occurrence.LateralDistanceMetres });
            }
            foreach (var role in new[] { PapeisImportacaoPadrao.Membership, PapeisImportacaoPadrao.Geometria, PapeisImportacaoPadrao.Paradas, PapeisImportacaoPadrao.Metadados })
                if (!await db.PadroesVersoesImportacoes.AnyAsync(x => x.PadraoVersaoId == version.Id && x.ImportacaoEstruturalId == import.Id && x.Papel == role, ct)) db.PadroesVersoesImportacoes.Add(new() { PadraoVersaoId = version.Id, ImportacaoEstruturalId = import.Id, Papel = role });
            publications.Add((persisted, version.Id));
        }

        await db.SaveChangesAsync(ct); // versão precisa existir antes do pointer FK.
        foreach (var publication in publications) { publication.Pattern.VersaoAtualId = publication.VersionId; var version = await db.PadroesVersoes.FindAsync([publication.VersionId], ct) ?? throw new InvalidDataException("Versão não persistida."); version.PublicadoEmUtc ??= now; }
        import.Status = StatusImportacaoEstrutural.Concluida; import.ConcluidaEmUtc ??= now;
        await db.SaveChangesAsync(ct);
        await ValidateConvergence(plan, source.Id, ct);
        await tx.CommitAsync(ct);
        return await Summary(import.Id, existingImport is not null, ct);
    }

    private async Task ValidateConvergence(TremStructuralPlan plan, Guid sourceId, CancellationToken ct)
    {
        var lines = await db.LinhasIdentidadesExternas.Include(x => x.Linha).Where(x => x.FonteEstruturalId == sourceId && x.Tipo == "LINE_ID").ToDictionaryAsync(x => x.ExternalId, ct);
        var stops = await db.ParadasIdentidadesExternas.Include(x => x.Parada).Where(x => x.FonteEstruturalId == sourceId && x.Tipo == "STATION_ID").ToDictionaryAsync(x => x.ExternalId, ct);
        if (plan.Snapshot.Lines.Any(x => !lines.TryGetValue(x.ExternalId, out var p) || p.Linha.Nome != x.Name || p.Linha.TipoRota != x.Type) || plan.Snapshot.Stations.Any(x => !stops.TryGetValue(x.ExternalId, out var p) || p.Parada.Nome != x.Name || p.Parada.Localizacao.X != x.Longitude || p.Parada.Localizacao.Y != x.Latitude)) throw new InvalidDataException("Catálogo persistido não convergiu ao snapshot aprovado.");
        var patterns = await db.PadroesIdentidadesExternas.Include(x => x.PadraoOperacional)
            .Where(x => x.FonteEstruturalId == sourceId && x.Tipo == "PATTERN_KEY")
            .ToDictionaryAsync(x => x.ExternalId, ct);
        var currentIds = patterns.Values.Select(x => x.PadraoOperacional.VersaoAtualId)
            .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var versions = await db.PadroesVersoes.Include(x => x.Ocorrencias)
            .Where(x => currentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var expected in plan.Patterns)
        {
            if (!patterns.TryGetValue(expected.ExternalKey, out var identity)) throw Divergence("Padrão ausente", expected.ExternalKey);
            var actual = identity.PadraoOperacional;
            if (actual.TipoServico != expected.Type || actual.NomePublico != expected.Name
                || actual.VersaoAtualId is null || !versions.TryGetValue(actual.VersaoAtualId.Value, out var version))
                throw Divergence("Padrão", expected.ExternalKey);
            if (version.PadraoOperacionalId != actual.Id || version.Numero <= 0) throw Divergence("Versão/vínculo", expected.ExternalKey);
            if (version.HashEstrutural != expected.StructuralHash) throw Divergence("Versão/hash", expected.ExternalKey);
            if (version.Topologia != TopologiasPadrao.Linear) throw Divergence("Versão/topologia", expected.ExternalKey);
            if (!Same(version.ComprimentoMetros, expected.LengthMetres)) throw Divergence("Versão/comprimento", expected.ExternalKey);
            if (version.MetodoConstrucao != expected.Method) throw Divergence("Versão/método", expected.ExternalKey);
            if (!Same(version.Confianca, expected.Confidence)) throw Divergence("Versão/confiança", expected.ExternalKey);
            if (version.AlgoritmoVersao != plan.Snapshot.AlgorithmVersion) throw Divergence("Versão/algoritmo", expected.ExternalKey);
            if (version.ResultadoValidacao != ResultadosValidacaoPadrao.Valida) throw Divergence("Versão/validação", expected.ExternalKey);
            if (!SameJson(version.Relatorio, expected.VersionReport)) throw Divergence("Versão/relatório", expected.ExternalKey);
            if (!SameGeometry(version.Geometria, expected.Geometry)) throw Divergence("Versão/geometria", expected.ExternalKey);
            var actualOccurrences = version.Ocorrencias.OrderBy(x => x.Ordem).ToArray();
            if (actualOccurrences.Length != expected.Occurrences.Count) throw Divergence("Ocorrências", expected.ExternalKey);
            for (var i = 0; i < actualOccurrences.Length; i++)
            {
                var occurrence = actualOccurrences[i]; var planned = expected.Occurrences[i];
                if (occurrence.Ordem != planned.Order || occurrence.ParadaId != stops[planned.StationId].ParadaId
                    || occurrence.SourceSequence != planned.SourceSequence
                    || !Same(occurrence.PosicaoTracado, planned.Position)
                    || !Same(occurrence.DistanciaAcumuladaMetros, planned.DistanceMetres)
                    || !Same(occurrence.DistanciaDaLinhaMetros, planned.LateralDistanceMetres)
                    || occurrence.SourceShapeDistTraveledMetros is not null)
                    throw Divergence("Ocorrência imutável", $"{expected.ExternalKey}:{planned.Order}");
            }
        }
    }

    // PostgreSQL double precision preserva os bits enviados pelo provider; igualdade exata evita ocultar corrupção.
    private static bool Same(double left, double right) => BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);
    private static bool SameJson(string left, string right) => JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));
    private static bool SameGeometry(LineString actual, LineString expected)
    {
        if (actual.SRID != expected.SRID || actual.GeometryType != expected.GeometryType
            || actual.NumPoints != expected.NumPoints) return false;
        var left = actual.Coordinates; var right = expected.Coordinates;
        return left.Zip(right).All(pair => Same(pair.First.X, pair.Second.X) && Same(pair.First.Y, pair.Second.Y));
    }

    private async Task<TremImportReport> Summary(Guid importId, bool reused, CancellationToken ct)
    {
        var sourceId = await db.FontesEstruturais.Where(x => x.Codigo == SourceCode).Select(x => x.Id).SingleAsync(ct);
        var lineIds = await db.LinhasIdentidadesExternas.Where(x => x.FonteEstruturalId == sourceId).Select(x => x.LinhaId).ToArrayAsync(ct);
        var directionIds = await db.Sentidos.Where(x => lineIds.Contains(x.LinhaId)).Select(x => x.Id).ToArrayAsync(ct);
        var patternIds = await db.PadroesOperacionais.Where(x => directionIds.Contains(x.SentidoId)).Select(x => x.Id).ToArrayAsync(ct);
        var versions = await db.PadroesVersoes.Where(x => patternIds.Contains(x.PadraoOperacionalId)).Select(x => x.Id).ToArrayAsync(ct);
        return new(importId, lineIds.Length, await db.ParadasIdentidadesExternas.CountAsync(x => x.FonteEstruturalId == sourceId, ct), directionIds.Length, patternIds.Length, versions.Length, await db.OcorrenciasParadasPadroes.CountAsync(x => versions.Contains(x.PadraoVersaoId), ct), await db.PadroesOperacionais.CountAsync(x => patternIds.Contains(x.Id) && x.VersaoAtualId != null, ct), reused);
    }
    private static Point Point(TremStationSnapshot item) => new(item.Longitude, item.Latitude) { SRID = 4326 };
    private static InvalidDataException Divergence(string entity, string externalId) => new($"Divergência incompatível de identidade em {entity} {externalId}.");
    private static Guid Id(string kind, string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"noponto:trem:v2:{kind}:{key}"))[..16]);
}
