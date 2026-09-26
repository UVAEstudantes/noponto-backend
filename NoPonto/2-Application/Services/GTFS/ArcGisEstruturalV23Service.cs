using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NoPonto.Domain.Entities;

namespace NoPonto.Application.GTFS;

public sealed record ArcGisSppoFeature(
    long Fid, string Servico, string Destino, string Direcao, string TipoDia,
    double Extensao, string Consorcio, string TipoRota, string? Descricao,
    string? Tarifas, double ShapeLength, LineString Geometria, string GeometriaHash);

public sealed record ArcGisSppoSnapshot(
    string SourceUrl, DateTimeOffset CapturadoEmUtc, string ConteudoHash,
    IReadOnlyList<ArcGisSppoFeature> Features, int Paginas);

public sealed class ArcGisSppoSnapshotClient(HttpClient http)
{
    public const string DefaultLayerUrl =
        "https://pgeo3.rio.rj.gov.br/arcgis/rest/services/Hosted/Rede_%C3%94nibus_SPPO_visualiza%C3%A7%C3%A3o/FeatureServer/1";

    public async Task<ArcGisSppoSnapshot> BaixarAsync(
        string layerUrl = DefaultLayerUrl, int pageSize = 1000, CancellationToken ct = default)
    {
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var result = new List<ArcGisSppoFeature>();
        var offset = 0;
        var pages = 0;
        while (true)
        {
            var parameters = new Dictionary<string, string?>
            {
                ["where"] = "1=1", ["outFields"] = "*", ["returnGeometry"] = "true",
                ["outSR"] = "4326", ["orderByFields"] = "fid", ["f"] = "json",
                ["resultOffset"] = offset.ToString(CultureInfo.InvariantCulture),
                ["resultRecordCount"] = pageSize.ToString(CultureInfo.InvariantCulture)
            };
            var url = QueryHelpers.AddQueryString(layerUrl.TrimEnd('/') + "/query", parameters);
            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (json.RootElement.TryGetProperty("error", out var error))
                throw new InvalidDataException($"ArcGIS retornou erro: {error.GetRawText()}");
            if (!json.RootElement.TryGetProperty("features", out var features)
                || features.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Resposta ArcGIS sem features.");
            var read = 0;
            foreach (var item in features.EnumerateArray())
            {
                result.Add(ParseFeature(item));
                read++;
            }
            pages++;
            var exceeded = json.RootElement.TryGetProperty("exceededTransferLimit", out var flag)
                && flag.ValueKind == JsonValueKind.True;
            if (!exceeded) break;
            if (read == 0) break;
            offset += read;
        }
        var ordered = result.OrderBy(x => x.Fid).ToArray();
        var hash = HashSnapshot(ordered);
        return new(layerUrl, DateTimeOffset.UtcNow, hash, ordered, pages);
    }

    internal static ArcGisSppoFeature ParseFeature(JsonElement item)
    {
        var a = item.GetProperty("attributes");
        var paths = item.GetProperty("geometry").GetProperty("paths");
        if (paths.GetArrayLength() != 1)
            throw new InvalidDataException("Geometria ArcGIS multipart não cabe em PadraoVersao.LineString.");
        var coordinates = paths[0].EnumerateArray().Select(p =>
        {
            var values = p.EnumerateArray().ToArray();
            if (values.Length < 2) throw new InvalidDataException("Coordenada ArcGIS inválida.");
            return new Coordinate(values[0].GetDouble(), values[1].GetDouble());
        }).ToArray();
        if (coordinates.Length < 2) throw new InvalidDataException("Geometria ArcGIS sem comprimento.");
        var geometry = new LineString(coordinates) { SRID = 4326 };
        var geometryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(';', coordinates.Select(x => FormattableString.Invariant($"{x.X:F7},{x.Y:F7}")))))).ToLowerInvariant();
        return new(
            Long(a, "fid"), Required(a, "servico"), Required(a, "destino"),
            Required(a, "direcao"), Required(a, "tipo_dia"), Double(a, "extensao"),
            Required(a, "consorcio"), Required(a, "tipo_rota"), Optional(a, "descricao"),
            Optional(a, "tarifas"), Double(a, "SHAPE__Length"), geometry, geometryHash);
    }

    internal static string HashSnapshot(IEnumerable<ArcGisSppoFeature> features)
    {
        var canonical = string.Join('\n', features.OrderBy(x => x.Fid).Select(x => string.Join('|',
            x.Fid, x.Servico, x.Destino, x.Direcao, x.TipoDia,
            x.Extensao.ToString("R", CultureInfo.InvariantCulture), x.Consorcio, x.TipoRota,
            x.Descricao ?? "", x.Tarifas ?? "", x.ShapeLength.ToString("R", CultureInfo.InvariantCulture),
            x.GeometriaHash)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Required(JsonElement a, string name) =>
        Optional(a, name) is { Length: > 0 } value ? value : throw new InvalidDataException($"Campo ArcGIS obrigatório ausente: {name}.");
    private static string? Optional(JsonElement a, string name) => !a.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null
        ? null : v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : v.GetRawText().Trim('"').Trim();
    private static double Double(JsonElement a, string name) => a.GetProperty(name).ValueKind == JsonValueKind.Number
        ? a.GetProperty(name).GetDouble() : double.Parse(Required(a, name), CultureInfo.InvariantCulture);
    private static long Long(JsonElement a, string name) => a.GetProperty(name).ValueKind == JsonValueKind.Number
        ? a.GetProperty(name).GetInt64() : long.Parse(Required(a, name), CultureInfo.InvariantCulture);
}

public enum ArcGisEstruturalStatus { Concluida, NoOp }

public sealed record ArcGisEstruturalRelatorio(
    int Features, int Servicos, int LinhasConciliadas, int SentidosConciliados,
    int VersoesMultifonte, int Ocorrencias, int ArcGisAmbigua,
    int SemArcGisPlanoAtual, int GtfsInsuficienteComArcGis,
    int MatchesExatosLinha, int DirecoesDiretas, int DirecoesInvertidas,
    double? DistanciaArcGisMediana, double? DistanciaArcGisP95, double? DistanciaArcGisMaxima,
    int DistanciasAcima25m, int DistanciasAcima50m, int DistanciasAcima100m,
    IReadOnlyDictionary<string, int> TipoDia,
    IReadOnlyDictionary<string, int> Classificacoes,
    IReadOnlyList<string> Warnings, long DuracaoMs);

public sealed record ArcGisEstruturalResultado(
    ArcGisEstruturalStatus Status, Guid ImportacaoId, ArcGisEstruturalRelatorio Relatorio);

internal sealed record ArcGisCandidateSelection(
    ArcGisSppoFeature Feature,
    IReadOnlyList<GtfsProjecaoOcorrencia> Occurrences,
    double P95,
    double Max);

public sealed class ArcGisEstruturalV23Service(
    TransporteDbContext db, GtfsFeedParser parser, GtfsProjecaoService projector)
{
    public const string FonteCodigo = "DADOS_RIO_ARCGIS_SPPO";
    public const string AlgoritmoVersao = "V2.3_ARCGIS_1";

    public async Task<ArcGisEstruturalResultado> ImportarAsync(
        ArcGisSppoSnapshot snapshot, Stream gtfsZip, string? versaoFonte = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(gtfsZip);
        var timer = Stopwatch.StartNew();
        var fonte = await db.FontesEstruturais.SingleOrDefaultAsync(x => x.Codigo == FonteCodigo, ct);
        if (fonte is null)
        {
            fonte = new FonteEstrutural { Id = Guid.NewGuid(), Codigo = FonteCodigo,
                Nome = "Dados.Rio ArcGIS — Plano Operacional SPPO" };
            db.FontesEstruturais.Add(fonte);
            await db.SaveChangesAsync(ct);
        }
        var previous = await db.ImportacoesEstruturais.AsNoTracking().Where(x =>
                x.FonteEstruturalId == fonte.Id && x.ConteudoHash == snapshot.ConteudoHash
                && x.AlgoritmoVersao == AlgoritmoVersao && x.Status == StatusImportacaoEstrutural.Concluida)
            .OrderByDescending(x => x.ConcluidaEmUtc).FirstOrDefaultAsync(ct);
        if (previous is not null)
            return new(ArcGisEstruturalStatus.NoOp, previous.Id,
                JsonSerializer.Deserialize<ArcGisEstruturalRelatorio>(previous.Relatorio)
                ?? throw new InvalidDataException("Relatório V2.3 inválido."));

        var import = new ImportacaoEstrutural { Id = Guid.NewGuid(), FonteEstruturalId = fonte.Id,
            Status = StatusImportacaoEstrutural.EmProcessamento, IniciadaEmUtc = DateTimeOffset.UtcNow,
            VersaoFonte = versaoFonte, ConteudoHash = snapshot.ConteudoHash, RawUri = snapshot.SourceUrl,
            AlgoritmoVersao = AlgoritmoVersao, Relatorio = "{}" };
        db.ImportacoesEstruturais.Add(import);
        await db.SaveChangesAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var feed = parser.Parse(gtfsZip);
            var gtfsSource = await db.FontesEstruturais.SingleAsync(x => x.Codigo == GtfsEstruturalV22Service.FonteCodigo, ct);
            var gtfsImport = await db.ImportacoesEstruturais.AsNoTracking().Where(x =>
                    x.FonteEstruturalId == gtfsSource.Id && x.Status == StatusImportacaoEstrutural.Concluida)
                .OrderByDescending(x => x.ConcluidaEmUtc).FirstAsync(ct);
            var lineIdentities = await db.LinhasIdentidadesExternas.Include(x => x.Linha)
                .Where(x => x.FonteEstruturalId == gtfsSource.Id && x.Tipo == "ROUTE_ID").ToArrayAsync(ct);
            var linesByRoute = lineIdentities.ToDictionary(x => x.ExternalId, x => x.Linha, StringComparer.OrdinalIgnoreCase);
            var directions = await db.SentidosIdentidadesExternas.Include(x => x.Sentido)
                .Where(x => x.FonteEstruturalId == gtfsSource.Id && x.Tipo == "GTFS_DIRECTION").ToArrayAsync(ct);
            var directionsByExternal = directions.ToDictionary(x => x.ExternalId, x => x.Sentido, StringComparer.OrdinalIgnoreCase);
            var stops = await db.ParadasIdentidadesExternas.Include(x => x.Parada)
                .Where(x => x.FonteEstruturalId == gtfsSource.Id && x.Tipo == "STOP_ID")
                .ToDictionaryAsync(x => x.ExternalId, x => x.Parada, StringComparer.OrdinalIgnoreCase, ct);
            var byService = snapshot.Features.GroupBy(x => x.Servico, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            var classes = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["ARCGIS_PREFERIDA"] = 0, ["GTFS_PREFERIDA"] = 0, ["EQUIVALENTES"] = 0,
                ["ARCGIS_AMBIGUA"] = 0, ["SEM_ARCGIS_PLANO_ATUAL"] = 0
            };
            var lineIdsAdded = new HashSet<Guid>();
            var directionIdsAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var versions = 0; var occurrences = 0; var reconciledDirections = 0; var insufficientWithArc = 0;
            var directDirections = 0; var inverseDirections = 0;
            var arcDistances = new List<double>();
            foreach (var group in feed.Padroes.GroupBy(x => (x.RouteId, x.DirectionId)))
            {
                var routeCode = group.First().RouteShortName;
                if (group.Count() != 1 || group.Single().Ocorrencias.Count < 3 || group.Single().Shape.Count < 2)
                {
                    if (byService.ContainsKey(routeCode)) insufficientWithArc++;
                    continue;
                }
                if (!byService.TryGetValue(routeCode, out var candidates))
                {
                    classes["SEM_ARCGIS_PLANO_ATUAL"]++;
                    continue;
                }
                var source = group.Single();
                if (!linesByRoute.TryGetValue(source.RouteId, out var line)
                    || !directionsByExternal.TryGetValue($"{source.RouteId}:{source.DirectionId}", out var direction))
                    continue;
                var selected = SelecionarCandidato(source, direction.Id, stops, candidates, projector);
                if (selected is null)
                {
                    classes["ARCGIS_AMBIGUA"]++;
                    warnings.Add($"ARCGIS_AMBIGUA:{source.RouteId}:{source.DirectionId}");
                    continue;
                }
                var pattern = await db.PadroesOperacionais.SingleAsync(x =>
                    x.SentidoId == direction.Id && x.Chave == "GTFS_ROUTE_DIRECTION_DEFAULT", ct);
                var gtfsVersion = await db.PadroesVersoes.AsNoTracking().Where(x =>
                        x.PadraoOperacionalId == pattern.Id && x.AlgoritmoVersao == GtfsEstruturalV22Service.AlgoritmoVersao)
                    .OrderByDescending(x => x.Numero).FirstAsync(ct);
                var gtfsProjection = projector.Projetar(source,
                    new Itinerario { Id = Guid.Empty, SentidoId = direction.Id, Geometria = gtfsVersion.Geometria }, stops);
                var gtfsP95 = Percentile(gtfsProjection.Ocorrencias.Select(x => x.DistanciaMetros), .95);
                var classification = selected.P95 + 1 < gtfsP95 ? "ARCGIS_PREFERIDA"
                    : gtfsP95 + 1 < selected.P95 ? "GTFS_PREFERIDA" : "EQUIVALENTES";
                classes[classification]++;
                if (classification == "GTFS_PREFERIDA") continue;

                if (lineIdsAdded.Add(line.Id) && !await db.LinhasIdentidadesExternas.AnyAsync(x =>
                    x.FonteEstruturalId == fonte.Id && x.Tipo == "SERVICO" && x.ExternalId == routeCode, ct))
                    db.LinhasIdentidadesExternas.Add(new() { Id = Guid.NewGuid(), LinhaId = line.Id,
                        FonteEstruturalId = fonte.Id, Tipo = "SERVICO", ExternalId = routeCode });
                var directionExternal = $"{routeCode}:{selected.Feature.Direcao}";
                if (directionIdsAdded.Add(directionExternal) && !await db.SentidosIdentidadesExternas.AnyAsync(x =>
                    x.FonteEstruturalId == fonte.Id && x.Tipo == "DIRECAO" && x.ExternalId == directionExternal, ct))
                    db.SentidosIdentidadesExternas.Add(new() { Id = Guid.NewGuid(), SentidoId = direction.Id,
                        FonteEstruturalId = fonte.Id, Tipo = "DIRECAO", ExternalId = directionExternal,
                        OrigemMapeamento = OrigensMapeamento.Fonte });
                reconciledDirections++;
                if (selected.Feature.Direcao == source.DirectionId) directDirections++;
                else inverseDirections++;
                arcDistances.AddRange(selected.Occurrences.Select(x => x.DistanciaMetros));
                var number = (await db.PadroesVersoes.Where(x => x.PadraoOperacionalId == pattern.Id)
                    .Select(x => (int?)x.Numero).MaxAsync(ct) ?? 0) + 1;
                var version = new PadraoVersao { Id = Guid.NewGuid(), PadraoOperacionalId = pattern.Id,
                    Numero = number, Geometria = selected.Feature.Geometria,
                    DistanciaMetros = selected.Feature.ShapeLength, MetodoConstrucao = "GTFS_ARCGIS_MULTIFONTE",
                    Topologia = selected.Feature.Geometria.IsClosed ? TopologiasPadrao.Circular : TopologiasPadrao.Linear,
                    HashEstrutural = EstruturaHash.Calcular(selected.Feature.Geometria,
                        selected.Feature.Geometria.IsClosed ? TopologiasPadrao.Circular : TopologiasPadrao.Linear,
                        selected.Occurrences.Select(x => new EstruturaHashOccurrence(x.ParadaCodigo,
                            x.Ordem, x.PosicaoLinha, x.PosicaoLinha * selected.Feature.ShapeLength, x.DistanciaMetros))),
                    Confianca = 1, AlgoritmoVersao = AlgoritmoVersao,
                    ResultadoValidacao = ResultadosValidacaoPadrao.Valida, CriadaEmUtc = DateTimeOffset.UtcNow,
                    Relatorio = JsonSerializer.Serialize(new { source.RouteId, source.DirectionId,
                        selected.Feature.Fid, selected.Feature.Servico, selected.Feature.Direcao,
                        selected.Feature.Destino, selected.Feature.TipoDia, selected.Feature.Extensao,
                        selected.Feature.ShapeLength, selected.Feature.Consorcio, selected.Feature.TipoRota,
                        selected.Feature.Descricao, selected.Feature.Tarifas, selected.Feature.GeometriaHash,
                        ArcGisP95 = selected.P95, ArcGisMax = selected.Max, GtfsP95 = gtfsP95, Classificacao = classification }) };
                db.PadroesVersoes.Add(version);
                foreach (var occurrence in selected.Occurrences)
                    db.OcorrenciasParadasPadroes.Add(new() { Id = Guid.NewGuid(), PadraoVersaoId = version.Id,
                        ParadaId = occurrence.ParadaId, Ordem = occurrence.Ordem,
                        SourceSequence = occurrence.SourceStopSequence, PosicaoTracado = occurrence.PosicaoLinha,
                        DistanciaAcumuladaMetros = occurrence.PosicaoLinha * version.ComprimentoMetros,
                        DistanciaDaLinhaMetros = occurrence.DistanciaMetros,
                        SourceShapeDistTraveledMetros = occurrence.SourceShapeDistTraveledMetros });
                db.PadroesVersoesImportacoes.AddRange(
                    new() { PadraoVersaoId = version.Id, ImportacaoEstruturalId = gtfsImport.Id, Papel = PapeisImportacaoPadrao.Membership },
                    new() { PadraoVersaoId = version.Id, ImportacaoEstruturalId = gtfsImport.Id, Papel = PapeisImportacaoPadrao.Paradas },
                    new() { PadraoVersaoId = version.Id, ImportacaoEstruturalId = import.Id, Papel = PapeisImportacaoPadrao.Geometria },
                    new() { PadraoVersaoId = version.Id, ImportacaoEstruturalId = import.Id, Papel = PapeisImportacaoPadrao.Metadados });
                versions++; occurrences += selected.Occurrences.Count;
            }
            await db.SaveChangesAsync(ct);
            timer.Stop();
            var report = new ArcGisEstruturalRelatorio(snapshot.Features.Count,
                snapshot.Features.Select(x => x.Servico).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                lineIdsAdded.Count, reconciledDirections, versions, occurrences, classes["ARCGIS_AMBIGUA"],
                classes["SEM_ARCGIS_PLANO_ATUAL"], insufficientWithArc,
                feed.Routes.Select(x => x.RouteShortName).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(byService.ContainsKey), directDirections, inverseDirections,
                arcDistances.Count == 0 ? null : Percentile(arcDistances, .5),
                arcDistances.Count == 0 ? null : Percentile(arcDistances, .95),
                arcDistances.Count == 0 ? null : arcDistances.Max(),
                arcDistances.Count(x => x > 25), arcDistances.Count(x => x > 50),
                arcDistances.Count(x => x > 100),
                snapshot.Features.GroupBy(x => x.TipoDia).ToDictionary(x => x.Key, x => x.Count()),
                classes, warnings, timer.ElapsedMilliseconds);
            import.Status = StatusImportacaoEstrutural.Concluida;
            import.ConcluidaEmUtc = DateTimeOffset.UtcNow;
            import.Relatorio = JsonSerializer.Serialize(report);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(ArcGisEstruturalStatus.Concluida, import.Id, report);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            var failed = await db.ImportacoesEstruturais.SingleAsync(x => x.Id == import.Id, CancellationToken.None);
            failed.Status = StatusImportacaoEstrutural.Falhou;
            failed.ConcluidaEmUtc = DateTimeOffset.UtcNow;
            failed.Relatorio = JsonSerializer.Serialize(new { Erro = ex.GetType().Name, ex.Message });
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    internal static ArcGisCandidateSelection? SelecionarCandidato(
        GtfsPadrao source, Guid directionId, IReadOnlyDictionary<string, Parada> stops,
        IEnumerable<ArcGisSppoFeature> candidates, GtfsProjecaoService projector)
    {
        var valid = candidates.Select(feature =>
        {
            var projection = projector.Projetar(source,
                new Itinerario { Id = Guid.Empty, SentidoId = directionId, Geometria = feature.Geometria }, stops);
            return projection.Motivos.Count == 0
                ? new ArcGisCandidateSelection(feature, projection.Ocorrencias,
                    Percentile(projection.Ocorrencias.Select(x => x.DistanciaMetros), .95),
                    projection.Ocorrencias.Max(x => x.DistanciaMetros))
                : null;
        }).Where(x => x is not null).Cast<ArcGisCandidateSelection>()
            .OrderBy(x => x.P95).ThenBy(x => x.Max).ToArray();
        if (valid.Length == 0) return null;
        if (valid.Length > 1 && !(valid[1].P95 - valid[0].P95 >= 10
            && valid[0].P95 <= valid[1].P95 * .75)) return null;
        return valid[0];
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.OrderBy(x => x).ToArray();
        if (ordered.Length == 0) return double.PositiveInfinity;
        return ordered[(int)Math.Ceiling((ordered.Length - 1) * percentile)];
    }
}
