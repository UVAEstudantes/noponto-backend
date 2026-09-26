using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.GTFS;

public enum EstruturaRebuildModo { DryRun, Persistir }

public sealed record EstruturaRebuildOpcoes(
    EstruturaRebuildModo Modo = EstruturaRebuildModo.DryRun,
    bool Publicar = false,
    IReadOnlyDictionary<Guid, Guid?>? VersoesEsperadas = null,
    string? VersaoFonte = null,
    string? RawUri = null);

public sealed record EstruturaRebuildRelatorio(
    int Linhas, int Sentidos, int Padroes, int VersoesCriadas, int VersoesReutilizadas,
    int Ocorrencias, int Paradas, int Lineares, int Circulares, int SentidosComMultiplosPadroes,
    int Rejeitados, IReadOnlyList<string> MotivosRejeicao, int Publicados,
    string ConteudoHash, IReadOnlyList<string> HashesEstruturais);

/// <summary>
/// Rebuild estrutural manual e autocontido. GTFS e ArcGIS são entradas paralelas;
/// o legado não é consultado. Nenhum hosted service registra ou executa este fluxo.
/// </summary>
public sealed class EstruturaFinalRebuildService(
    TransporteDbContext db, GtfsFeedParser parser, GtfsProjecaoService projector)
{
    public const string AlgoritmoVersao = "ESTRUTURA_FINAL_V1";
    private const string FonteGtfs = "SMTR_GTFS";
    private const string FonteArcGis = "DADOS_RIO_ARCGIS_SPPO";

    public async Task<EstruturaRebuildRelatorio> ExecutarAsync(
        Stream gtfsZip, ArcGisSppoSnapshot? arcGis, EstruturaRebuildOpcoes opcoes,
        CancellationToken ct = default)
    {
        using var memory = new MemoryStream();
        await gtfsZip.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        var feed = parser.Parse(new MemoryStream(bytes, writable: false));
        var inputHash = HashTexto(Convert.ToHexString(SHA256.HashData(bytes)) + "|"
            + (arcGis is null ? "SEM_ARCGIS" : ArcGisSppoSnapshotClient.HashSnapshot(arcGis.Features)));

        var candidates = ConstruirCandidatos(feed, arcGis);
        var rejected = candidates.Where(x => x.Motivos.Count > 0).ToArray();
        var valid = candidates.Where(x => x.Motivos.Count == 0)
            .OrderBy(x => x.RouteId, StringComparer.Ordinal)
            .ThenBy(x => x.DirectionId, StringComparer.Ordinal)
            .ThenBy(x => x.PatternKey, StringComparer.Ordinal).ToArray();
        var hashesPreview = valid.Select(x => x.Hash).Distinct(StringComparer.Ordinal).Order().ToArray();
        var report = new EstruturaRebuildRelatorio(
            feed.Routes.Select(x => x.RouteId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            feed.Trips.Select(x => (x.RouteId, x.DirectionId)).Distinct().Count(),
            valid.Select(x => (x.RouteId, x.DirectionId, x.PatternKey)).Distinct().Count(),
            0, 0, valid.Sum(x => x.Occurrences.Count), feed.Stops.Count,
            valid.Count(x => x.Topology == TopologiasPadrao.Linear),
            valid.Count(x => x.Topology == TopologiasPadrao.Circular),
            valid.GroupBy(x => (x.RouteId, x.DirectionId)).Count(x => x.Count() > 1),
            rejected.Length, rejected.SelectMany(x => x.Motivos.Select(m => $"{x.ExternalPatternId}:{m}"))
                .Order(StringComparer.Ordinal).ToArray(), 0, inputHash, hashesPreview);

        if (opcoes.Modo == EstruturaRebuildModo.DryRun) return report;
        if (opcoes.Publicar && opcoes.VersoesEsperadas is null)
            throw new InvalidOperationException("Publicação exige o mapa CAS de versões esperadas.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var gtfsSource = await ObterFonteAsync(FonteGtfs, "GTFS oficial SMTR", ct);
        var arcSource = arcGis is null ? null : await ObterFonteAsync(FonteArcGis, "ArcGIS itinerários SPPO", ct);
        var previous = await db.ImportacoesEstruturais.AsNoTracking().FirstOrDefaultAsync(x =>
            x.FonteEstruturalId == gtfsSource.Id && x.ConteudoHash == inputHash
            && x.AlgoritmoVersao == AlgoritmoVersao && x.Status == StatusImportacaoEstrutural.Concluida, ct);
        if (previous is not null)
        {
            await transaction.RollbackAsync(ct);
            return report with { VersoesReutilizadas = valid.Length };
        }

        var importGtfs = NovaImportacao(gtfsSource.Id, inputHash, opcoes);
        db.ImportacoesEstruturais.Add(importGtfs);
        ImportacaoEstrutural? importArc = null;
        if (arcSource is not null)
        {
            importArc = NovaImportacao(arcSource.Id,
                ArcGisSppoSnapshotClient.HashSnapshot(arcGis!.Features), opcoes);
            db.ImportacoesEstruturais.Add(importArc);
        }
        await db.SaveChangesAsync(ct);

        var modal = await ObterModalAsync(ct);
        var lines = await ObterLinhasAsync(feed, gtfsSource, modal, ct);
        var directions = await ObterSentidosAsync(feed, gtfsSource, lines, ct);
        var stops = await ObterParadasAsync(feed, gtfsSource, modal, ct);
        var created = 0; var reused = 0; var occurrences = 0; var published = 0;

        foreach (var candidate in valid)
        {
            var direction = directions[(candidate.RouteId, candidate.DirectionId)];
            var pattern = await db.PadroesOperacionais.SingleOrDefaultAsync(x =>
                x.SentidoId == direction.Id && x.Chave == candidate.PatternKey, ct);
            if (pattern is null)
            {
                pattern = new PadraoOperacional { Id = DeterministicGuid("pattern", direction.Id.ToString("N"), candidate.PatternKey),
                    SentidoId = direction.Id, Chave = candidate.PatternKey, TipoServico = "REGULAR" };
                db.PadroesOperacionais.Add(pattern);
            }
            await GarantirIdentidadeAsync(pattern, gtfsSource, "TRIP_PATTERN_HASH", candidate.SequenceHash, ct);
            foreach (var shape in candidate.ShapeIds)
                await GarantirIdentidadeAsync(pattern, gtfsSource, "SHAPE_ID", shape, ct);
            if (candidate.ArcFeature is not null && arcSource is not null)
                await GarantirIdentidadeAsync(pattern, arcSource, "ARCGIS_FEATURE_ID",
                    candidate.ArcFeature.Fid.ToString(CultureInfo.InvariantCulture), ct);
            await db.SaveChangesAsync(ct);

            var existing = await db.PadroesVersoes.SingleOrDefaultAsync(x =>
                x.PadraoOperacionalId == pattern.Id && x.HashEstrutural == candidate.Hash, ct);
            if (existing is not null) { reused++; continue; }
            var number = (await db.PadroesVersoes.Where(x => x.PadraoOperacionalId == pattern.Id)
                .MaxAsync(x => (int?)x.Numero, ct) ?? 0) + 1;
            var version = new PadraoVersao {
                Id = DeterministicGuid("version", pattern.Id.ToString("N"), candidate.Hash),
                PadraoOperacionalId = pattern.Id, Numero = number, Geometria = candidate.Geometry,
                Topologia = candidate.Topology, ComprimentoMetros = candidate.Length,
                HashEstrutural = candidate.Hash, MetodoConstrucao = candidate.ArcFeature is null
                    ? "GTFS_AUTORITATIVO" : "GTFS_ARCGIS_RECONCILIADO",
                Confianca = candidate.ArcFeature is null ? 1 : .95,
                AlgoritmoVersao = AlgoritmoVersao, ResultadoValidacao = ResultadosValidacaoPadrao.Valida,
                Relatorio = JsonSerializer.Serialize(new { candidate.RouteId, candidate.DirectionId,
                    candidate.ShapeIds, candidate.ExternalPatternId, candidate.Topology }),
                CriadoEmUtc = DateTimeOffset.UtcNow };
            db.PadroesVersoes.Add(version);
            foreach (var occurrence in candidate.Occurrences)
            {
                var stop = stops[occurrence.StopId];
                db.OcorrenciasParadasPadroes.Add(new OcorrenciaParadaPadrao {
                    Id = DeterministicGuid("occurrence", version.Id.ToString("N"), occurrence.Order.ToString(CultureInfo.InvariantCulture)),
                    PadraoVersaoId = version.Id, ParadaId = stop.Id, Ordem = occurrence.Order,
                    PosicaoTracado = occurrence.Position, DistanciaAcumuladaMetros = occurrence.Accumulated,
                    DistanciaDaLinhaMetros = occurrence.Lateral,
                    SourceSequence = occurrence.SourceSequence,
                    SourceShapeDistTraveledMetros = occurrence.SourceDistance });
            }
            foreach (var role in new[] { PapeisImportacaoPadrao.Membership, PapeisImportacaoPadrao.Paradas,
                         PapeisImportacaoPadrao.Metadados })
                db.PadroesVersoesImportacoes.Add(new() { PadraoVersaoId = version.Id,
                    ImportacaoEstruturalId = importGtfs.Id, Papel = role });
            db.PadroesVersoesImportacoes.Add(new() { PadraoVersaoId = version.Id,
                ImportacaoEstruturalId = candidate.ArcFeature is null ? importGtfs.Id : importArc!.Id,
                Papel = PapeisImportacaoPadrao.Geometria });
            await db.SaveChangesAsync(ct);
            created++; occurrences += candidate.Occurrences.Count;

            if (opcoes.Publicar)
            {
                opcoes.VersoesEsperadas!.TryGetValue(pattern.Id, out var expected);
                await PublicarCoreAsync(pattern.Id, version.Id, expected, ct);
                published++;
            }
        }

        importGtfs.Status = StatusImportacaoEstrutural.Concluida;
        importGtfs.ConcluidaEmUtc = DateTimeOffset.UtcNow;
        importGtfs.Relatorio = JsonSerializer.Serialize(report with { VersoesCriadas = created,
            VersoesReutilizadas = reused, Ocorrencias = occurrences, Publicados = published });
        if (importArc is not null) { importArc.Status = StatusImportacaoEstrutural.Concluida;
            importArc.ConcluidaEmUtc = DateTimeOffset.UtcNow; importArc.Relatorio = importGtfs.Relatorio; }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return report with { VersoesCriadas = created, VersoesReutilizadas = reused,
            Ocorrencias = occurrences, Publicados = published };
    }

    public async Task PublicarAsync(Guid patternId, Guid versionId, Guid? expectedVersionId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PublicarCoreAsync(patternId, versionId, expectedVersionId, ct);
        await tx.CommitAsync(ct);
    }

    private async Task PublicarCoreAsync(Guid patternId, Guid versionId, Guid? expected, CancellationToken ct)
    {
        var candidate = await db.PadroesVersoes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == versionId
            && x.PadraoOperacionalId == patternId && x.ResultadoValidacao == ResultadosValidacaoPadrao.Valida, ct)
            ?? throw new InvalidOperationException("Candidata válida não pertence ao padrão.");
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "PadroesOperacionais" SET "VersaoAtualId"={versionId}
            WHERE "Id"={patternId} AND "VersaoAtualId" IS NOT DISTINCT FROM {expected}
            """, ct);
        if (affected != 1) throw new DbUpdateConcurrencyException("Publicação estrutural perdeu o CAS.");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "PadroesVersoes" SET "PublicadoEmUtc"=COALESCE("PublicadoEmUtc", now()) WHERE "Id"={candidate.Id}
            """, ct);
    }

    private Candidate[] ConstruirCandidatos(GtfsFeed feed, ArcGisSppoSnapshot? arcGis)
    {
        var stopCoordinates = feed.Stops.ToDictionary(x => x.StopId,
            x => new Point(x.Longitude, x.Latitude) { SRID = 4326 }, StringComparer.OrdinalIgnoreCase);
        return feed.Padroes.Select(source =>
        {
            var reasons = new List<string>();
            if (source.Ocorrencias.Count < 2 || source.Shape.Count < 2) reasons.Add("ESTRUTURA_INSUFICIENTE");
            if (source.Ocorrencias.Any(x => !stopCoordinates.ContainsKey(x.StopId))) reasons.Add("PARADA_INEXISTENTE");
            var gtfsGeometry = source.Shape.Count >= 2 ? new LineString(source.Shape.ToArray()) { SRID = 4326 } : null;
            ArcGisCandidateSelection? arc = null;
            if (reasons.Count == 0 && arcGis is not null)
            {
                var temporaryStops = feed.Stops.ToDictionary(x => x.StopId, x => new Parada { Id = DeterministicGuid("stop", FonteGtfs, x.StopId),
                    Codigo = x.StopId, Nome = x.StopName, Localizacao = stopCoordinates[x.StopId] }, StringComparer.OrdinalIgnoreCase);
                arc = ArcGisEstruturalV23Service.SelecionarCandidato(source, Guid.Empty, temporaryStops,
                    arcGis.Features.Where(x => string.Equals(x.Servico, source.RouteShortName,
                        StringComparison.OrdinalIgnoreCase)), projector);
                if (arc is not null && gtfsGeometry is not null)
                {
                    var gtfsProjection = projector.Projetar(source,
                        new Itinerario { Id=Guid.Empty, SentidoId=Guid.Empty, Geometria=gtfsGeometry }, temporaryStops);
                    if (gtfsProjection.Motivos.Count == 0)
                    {
                        var ordered = gtfsProjection.Ocorrencias.Select(x=>x.DistanciaMetros).Order().ToArray();
                        var gtfsP95 = ordered[(int)Math.Ceiling((ordered.Length-1)*.95)];
                        if (arc.P95 > gtfsP95 + 1) arc = null;
                    }
                }
            }
            var geometry = arc?.Feature.Geometria ?? gtfsGeometry;
            if (geometry is null) reasons.Add("GEOMETRIA_INEXISTENTE");
            var topology = IsCircular(source, geometry) ? TopologiasPadrao.Circular : TopologiasPadrao.Linear;
            var projected = geometry is null || reasons.Count > 0 ? [] : Project(source, geometry, stopCoordinates,
                topology == TopologiasPadrao.Circular, reasons);
            var sequenceHash = HashTexto(string.Join('|', source.Ocorrencias.OrderBy(x => x.StopSequence)
                .Select(x => x.StopId.Trim().ToUpperInvariant())));
            var key = "PATTERN_" + sequenceHash[..20];
            var length = geometry is null ? 0 : Metric.Length(geometry.Coordinates);
            var hash = geometry is null ? "" : EstruturaHash.Calcular(geometry, topology,
                projected.Select(x => new EstruturaHashOccurrence(x.StopId, x.Order, x.Position,
                    x.Accumulated, x.Lateral)));
            return new Candidate(source.PadraoExternoId, source.RouteId, source.DirectionId, key,
                sequenceHash, source.ShapeId is null ? [] : [source.ShapeId], geometry!, topology,
                length, projected, hash, reasons, arc?.Feature);
        }).ToArray();
    }

    private static List<ProjectedOccurrence> Project(GtfsPadrao source, LineString geometry,
        IReadOnlyDictionary<string, Point> stops, bool circular, List<string> reasons)
    {
        var result = new List<ProjectedOccurrence>();
        var previous = -1d; var total = Metric.Length(geometry.Coordinates);
        var ordered = source.Ocorrencias.OrderBy(x => x.StopSequence).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var item = ordered[index];
            var projection = Metric.Project(stops[item.StopId].Coordinate, geometry.Coordinates);
            if (circular && index == ordered.Length - 1
                && string.Equals(item.StopId, ordered[0].StopId, StringComparison.OrdinalIgnoreCase))
                projection = (1, projection.Distance);
            if (projection.Position + 1e-9 < previous) { reasons.Add("PROGRESSO_REGRESSIVO"); return []; }
            result.Add(new(item.StopId, result.Count + 1, item.StopSequence,
                item.ShapeDistTraveledMetros, projection.Position, projection.Position * total, projection.Distance));
            previous = projection.Position;
        }
        return result;
    }

    private static bool IsCircular(GtfsPadrao source, LineString? geometry) =>
        source.Ocorrencias.Count > 1 && string.Equals(source.Ocorrencias[0].StopId,
            source.Ocorrencias[^1].StopId, StringComparison.OrdinalIgnoreCase)
        || geometry is { IsClosed: true };

    private async Task<FonteEstrutural> ObterFonteAsync(string code, string name, CancellationToken ct)
    {
        var source = await db.FontesEstruturais.SingleOrDefaultAsync(x => x.Codigo == code, ct);
        if (source is not null) return source;
        source = new FonteEstrutural { Id = DeterministicGuid("source", code), Codigo = code, Nome = name };
        db.FontesEstruturais.Add(source); await db.SaveChangesAsync(ct); return source;
    }

    private async Task<Modal> ObterModalAsync(CancellationToken ct)
    {
        var modal = await db.Modais.OrderBy(x => x.Id).FirstOrDefaultAsync(x => x.Nome == "Ônibus", ct);
        if (modal is not null) return modal;
        modal = new Modal { Id = DeterministicGuid("modal", "ONIBUS"), Nome = "Ônibus" };
        db.Modais.Add(modal); await db.SaveChangesAsync(ct); return modal;
    }

    private async Task<Dictionary<string, Linha>> ObterLinhasAsync(GtfsFeed feed, FonteEstrutural source,
        Modal modal, CancellationToken ct)
    {
        var result = new Dictionary<string, Linha>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in feed.Routes.OrderBy(x => x.RouteId, StringComparer.Ordinal))
        {
            var identity = await db.LinhasIdentidadesExternas.Include(x => x.Linha).SingleOrDefaultAsync(x =>
                x.FonteEstruturalId == source.Id && x.Tipo == "ROUTE_ID" && x.ExternalId == route.RouteId, ct);
            var line = identity?.Linha ?? new Linha { Id = DeterministicGuid("line", source.Codigo, route.RouteId),
                ModalId = modal.Id, Codigo = route.RouteShortName, Nome = route.RouteShortName };
            if (identity is null) { db.Linhas.Add(line); db.LinhasIdentidadesExternas.Add(new() {
                Id = DeterministicGuid("line-identity", source.Codigo, route.RouteId), LinhaId = line.Id,
                FonteEstruturalId = source.Id, Tipo = "ROUTE_ID", ExternalId = route.RouteId, Confianca = 1 }); }
            result[route.RouteId] = line;
        }
        await db.SaveChangesAsync(ct); return result;
    }

    private async Task<Dictionary<(string,string), Sentido>> ObterSentidosAsync(GtfsFeed feed,
        FonteEstrutural source, IReadOnlyDictionary<string, Linha> lines, CancellationToken ct)
    {
        var result = new Dictionary<(string,string), Sentido>();
        foreach (var key in feed.Trips.Select(x => (x.RouteId, x.DirectionId)).Distinct()
                     .OrderBy(x => x.RouteId, StringComparer.Ordinal).ThenBy(x => x.DirectionId, StringComparer.Ordinal))
        {
            var external = $"{key.RouteId}:{key.DirectionId}";
            var identity = await db.SentidosIdentidadesExternas.Include(x => x.Sentido).SingleOrDefaultAsync(x =>
                x.FonteEstruturalId == source.Id && x.Tipo == "GTFS_DIRECTION" && x.ExternalId == external, ct);
            var direction = identity?.Sentido ?? new Sentido { Id = DeterministicGuid("direction", source.Codigo, external),
                LinhaId = lines[key.RouteId].Id, Nome = $"Sentido {key.DirectionId}" };
            if (identity is null) { db.Sentidos.Add(direction); db.SentidosIdentidadesExternas.Add(new() {
                Id = DeterministicGuid("direction-identity", source.Codigo, external), SentidoId = direction.Id,
                FonteEstruturalId = source.Id, Tipo = "GTFS_DIRECTION", ExternalId = external, Confianca = 1 }); }
            result[key] = direction;
        }
        await db.SaveChangesAsync(ct); return result;
    }

    private async Task<Dictionary<string, Parada>> ObterParadasAsync(GtfsFeed feed, FonteEstrutural source,
        Modal modal, CancellationToken ct)
    {
        var result = new Dictionary<string, Parada>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in feed.Stops.OrderBy(x => x.StopId, StringComparer.Ordinal))
        {
            var identity = await db.ParadasIdentidadesExternas.Include(x => x.Parada).SingleOrDefaultAsync(x =>
                x.FonteEstruturalId == source.Id && x.Tipo == "STOP_ID" && x.ExternalId == item.StopId, ct);
            var stop = identity?.Parada ?? new Parada { Id = DeterministicGuid("stop", source.Codigo, item.StopId),
                Codigo = item.StopId, ChaveCanonica = $"{source.Codigo}:STOP_ID:{item.StopId}", Nome = item.StopName,
                Localizacao = new Point(item.Longitude, item.Latitude) { SRID = 4326 }, ModalId = modal.Id,
                TipoLocal = TiposLocalParada.Parada };
            if (identity is null) { db.Paradas.Add(stop); db.ParadasIdentidadesExternas.Add(new() {
                Id = DeterministicGuid("stop-identity", source.Codigo, item.StopId), ParadaId = stop.Id,
                FonteEstruturalId = source.Id, Tipo = "STOP_ID", ExternalId = item.StopId, Confianca = 1 }); }
            result[item.StopId] = stop;
        }
        await db.SaveChangesAsync(ct); return result;
    }

    private async Task GarantirIdentidadeAsync(PadraoOperacional pattern, FonteEstrutural source,
        string type, string external, CancellationToken ct)
    {
        if (await db.PadroesIdentidadesExternas.AnyAsync(x => x.FonteEstruturalId == source.Id
            && x.Tipo == type && x.ExternalId == external, ct)) return;
        db.PadroesIdentidadesExternas.Add(new() { Id = DeterministicGuid("pattern-identity", source.Codigo, type, external),
            PadraoOperacionalId = pattern.Id, FonteEstruturalId = source.Id, Tipo = type,
            ExternalId = external, Confianca = 1 });
    }

    private static ImportacaoEstrutural NovaImportacao(Guid sourceId, string hash, EstruturaRebuildOpcoes options) =>
        new() { Id = Guid.NewGuid(), FonteEstruturalId = sourceId, Status = StatusImportacaoEstrutural.EmProcessamento,
            IniciadaEmUtc = DateTimeOffset.UtcNow, VersaoFonte = options.VersaoFonte, ConteudoHash = hash,
            RawUri = options.RawUri, AlgoritmoVersao = AlgoritmoVersao, Relatorio = "{}" };

    internal static Guid DeterministicGuid(params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        Span<byte> bytes = stackalloc byte[16]; hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
    internal static string HashTexto(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record Candidate(string ExternalPatternId, string RouteId, string DirectionId,
        string PatternKey, string SequenceHash, IReadOnlyList<string> ShapeIds, LineString Geometry,
        string Topology, double Length, IReadOnlyList<ProjectedOccurrence> Occurrences, string Hash,
        IReadOnlyList<string> Motivos, ArcGisSppoFeature? ArcFeature);
    private sealed record ProjectedOccurrence(string StopId, int Order, int SourceSequence,
        double? SourceDistance, double Position, double Accumulated, double Lateral);
}

internal sealed record EstruturaHashOccurrence(string CanonicalStopId, int Order, double Position,
    double Accumulated, double Lateral);

internal static class EstruturaHash
{
    internal static string Calcular(LineString geometry, string topology,
        IEnumerable<EstruturaHashOccurrence> occurrences)
    {
        static string D(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero)
            .ToString("0.######", CultureInfo.InvariantCulture);
        var canonical = new StringBuilder(topology).Append('|');
        foreach (var c in geometry.Coordinates) canonical.Append(D(c.X)).Append(',').Append(D(c.Y)).Append(';');
        canonical.Append('|');
        foreach (var o in occurrences.OrderBy(x => x.Order)) canonical.Append(o.Order).Append(':')
            .Append(o.CanonicalStopId.Trim().ToUpperInvariant()).Append(':').Append(D(o.Position)).Append(':')
            .Append(D(o.Accumulated)).Append(':').Append(D(o.Lateral)).Append(';');
        return EstruturaFinalRebuildService.HashTexto(canonical.ToString());
    }
}

internal static class Metric
{
    private const double EarthRadius = 6_371_008.8;
    internal static double Length(Coordinate[] coordinates) => Enumerable.Range(1, coordinates.Length - 1)
        .Sum(i => Distance(coordinates[i - 1], coordinates[i]));
    internal static (double Position, double Distance) Project(Coordinate point, Coordinate[] line)
    {
        var lengths = Enumerable.Range(1, line.Length - 1).Select(i => Distance(line[i - 1], line[i])).ToArray();
        var total = lengths.Sum(); var cumulative = 0d; var bestDistance = double.MaxValue; var bestPosition = 0d;
        for (var i = 0; i < lengths.Length; i++)
        {
            var a = line[i]; var b = line[i + 1]; var lat = (a.Y + b.Y + point.Y) / 3 * Math.PI / 180;
            var sx = EarthRadius * Math.Cos(lat) * Math.PI / 180; var sy = EarthRadius * Math.PI / 180;
            var vx = (b.X-a.X)*sx; var vy=(b.Y-a.Y)*sy; var wx=(point.X-a.X)*sx; var wy=(point.Y-a.Y)*sy;
            var den=vx*vx+vy*vy; var t=den <= 0 ? 0 : Math.Clamp((wx*vx+wy*vy)/den,0,1);
            var dx=wx-t*vx; var dy=wy-t*vy; var distance=Math.Sqrt(dx*dx+dy*dy);
            if (distance < bestDistance) { bestDistance=distance; bestPosition=total <= 0 ? 0 : (cumulative+t*lengths[i])/total; }
            cumulative += lengths[i];
        }
        return (bestPosition, bestDistance);
    }
    private static double Distance(Coordinate a, Coordinate b)
    {
        var p1=a.Y*Math.PI/180; var p2=b.Y*Math.PI/180; var dp=(b.Y-a.Y)*Math.PI/180; var dl=(b.X-a.X)*Math.PI/180;
        var h=Math.Sin(dp/2)*Math.Sin(dp/2)+Math.Cos(p1)*Math.Cos(p2)*Math.Sin(dl/2)*Math.Sin(dl/2);
        return 2*EarthRadius*Math.Asin(Math.Min(1,Math.Sqrt(h)));
    }
}
